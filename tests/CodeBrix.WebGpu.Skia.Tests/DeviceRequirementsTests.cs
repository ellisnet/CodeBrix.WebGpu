using System;
using System.Collections.Generic;
using System.Linq;
using CodeBrix.WebGpu.Native;
using CodeBrix.WebGpu.Skia.Interop;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Skia.Tests;

/// <summary>
/// Required features and limits on the owned device (<see cref="WebGpuSkiaDeviceOptions" />), the typed report of an
/// unsatisfiable requirement, and the adapter-available versus device-enabled sets.
/// </summary>
public unsafe class DeviceRequirementsTests : IClassFixture<SkiaGpuFixture>
{
    private readonly SkiaGpuFixture gpu;

    public DeviceRequirementsTests(SkiaGpuFixture fixture)
    {
        gpu = fixture;
    }

    private static WebGpuSkiaDeviceOptions OnFixtureBackend(WebGpuSkiaDevice device) =>
        new WebGpuSkiaDeviceOptions { PreferredBackend = device.Backend, AllowFallbackBackend = false };

    private static Limits ReadDeviceLimits(Device device)
    {
        Limits limits = default;
        Native.WebGpu.DeviceGetLimits(device, &limits).Should().Be(Status.Success);
        return limits;
    }

    // A limit (not an alignment) whose adapter maximum is above the default device's value: raising it is a real request.
    private static int RaisableLimit(WebGpuSkiaDevice device)
    {
        for (int index = 0; index < LimitFields.Names.Length; index++)
        {
            if (LimitFields.IsAlignment(index)) continue;
            if (LimitFields.Get(device.AdapterLimits, index) > LimitFields.Get(device.Limits, index)) return index;
        }

        return -1;
    }

    private static Limits WithField(Limits limits, string name, ulong value)
    {
        object boxed = limits;
        System.Reflection.FieldInfo field = typeof(Limits).GetField(name);
        field.SetValue(boxed, field.FieldType == typeof(ulong) ? (object)value : (object)(uint)value);
        return (Limits)boxed;
    }

    [Fact]
    public void Default_options_ask_for_nothing_and_the_device_reports_both_sets()
    {
        //Arrange
        gpu.RequireDevice();
        WebGpuSkiaDeviceOptions options = new WebGpuSkiaDeviceOptions();
        WebGpuSkiaDevice device = gpu.Device;

        //Act
        HashSet<FeatureName> available = new HashSet<FeatureName>(device.AdapterFeatures);
        HashSet<NativeFeature> availableNative = new HashSet<NativeFeature>(device.AdapterNativeFeatures);

        //Assert
        options.RequiredFeatures.Should().BeEmpty();
        options.RequiredNativeFeatures.Should().BeEmpty();
        options.RequiredLimits.HasValue.Should().BeFalse();
        device.UnmetRequirements.Should().BeEmpty();
        device.AdapterFeatures.Count.Should().BeGreaterThan(0);
        device.Features.All(available.Contains).Should().BeTrue();
        device.NativeFeatures.All(availableNative.Contains).Should().BeTrue();
        device.Limits.MaxTextureDimension2D.Should().BeGreaterThan(0u);
        device.Limits.MaxTextureDimension2D.Should().Be(ReadDeviceLimits(device.Device).MaxTextureDimension2D);
        device.AdapterLimits.MaxTextureDimension2D.Should().BeGreaterThanOrEqualTo(device.Limits.MaxTextureDimension2D);
    }

    [Fact]
    public void Owned_device_gets_a_required_feature_a_native_feature_and_a_raised_limit()
    {
        //Arrange
        gpu.RequireDevice();
        WebGpuSkiaDevice reference = gpu.Device;
        FeatureName feature = reference.AdapterFeatures.FirstOrDefault(candidate => candidate != FeatureName.CoreFeaturesAndLimits);
        NativeFeature native = reference.AdapterNativeFeatures.FirstOrDefault(candidate => !DeviceRequirements.IsExperimental(candidate));
        int limit = RaisableLimit(reference);
        Assert.SkipWhen(feature == default || native == default, "The adapter offers no optional standard and native feature.");
        Assert.SkipWhen(limit < 0, "The adapter offers no limit above the defaults.");
        string limitName = LimitFields.Names[limit];
        ulong raised = LimitFields.Get(reference.AdapterLimits, limit);
        WebGpuSkiaDeviceOptions options = OnFixtureBackend(reference);
        options.RequiredFeatures.Add(feature);
        options.RequiredNativeFeatures.Add(native);
        options.RequiredLimits = WithField(WebGpuSkiaLimits.Undefined(), limitName, raised);

        //Act
        using WebGpuSkiaDevice device = WebGpuSkiaDevice.Create(options);

        //Assert
        device.IsAvailable.Should().BeTrue(device.Reason);
        device.UnmetRequirements.Should().BeEmpty();
        device.Features.Should().Contain(feature);
        device.NativeFeatures.Should().Contain(native);
        device.HasFeature(feature).Should().BeTrue();
        device.HasFeature(native).Should().BeTrue();
        Native.WebGpu.DeviceHasFeature(device.Device, feature).Should().NotBe(0u);
        Native.WebGpu.DeviceHasFeature(device.Device, (FeatureName)(uint)native).Should().NotBe(0u);
        LimitFields.Get(ReadDeviceLimits(device.Device), limit).Should().Be(raised);
        LimitFields.Get(device.Limits, limit).Should().Be(raised);
        device.Features.All(device.AdapterFeatures.Contains).Should().BeTrue();
        device.NativeFeatures.All(device.AdapterNativeFeatures.Contains).Should().BeTrue();
        device.CreateGrContext().Should().NotBeNull(device.GrContextReason);
    }

    [Fact]
    public void A_feature_the_adapter_lacks_fails_with_the_feature_named_and_the_caller_can_fall_back()
    {
        //Arrange
        gpu.RequireDevice();
        WebGpuSkiaDevice reference = gpu.Device;
        FeatureName missing = Enum.GetValues<FeatureName>().FirstOrDefault(candidate => (uint)candidate > 1 && (uint)candidate < 0x100 && !reference.AdapterFeatures.Contains(candidate));
        Assert.SkipWhen(missing == default, "The adapter offers every standard feature.");
        WebGpuSkiaDeviceOptions options = OnFixtureBackend(reference);
        options.RequiredFeatures.Add(missing);

        //Act
        using WebGpuSkiaDevice refused = WebGpuSkiaDevice.Create(options);
        options.RequiredFeatures.Clear();
        using WebGpuSkiaDevice fallback = WebGpuSkiaDevice.Create(options);

        //Assert
        refused.IsAvailable.Should().BeFalse();
        refused.UnmetRequirements.Should().HaveCount(1);
        WebGpuSkiaUnmetRequirement unmet = refused.UnmetRequirements[0];
        unmet.Kind.Should().Be(WebGpuSkiaRequirementKind.Feature);
        unmet.Name.Should().Be(missing.ToString());
        unmet.RequestedValue.Should().Be((ulong)(uint)missing);
        unmet.Backend.Should().Be(reference.Backend);
        unmet.AdapterName.Should().Be(reference.AdapterName);
        unmet.Message.Should().Contain(missing.ToString());
        refused.Reason.Should().Contain(missing.ToString());
        fallback.IsAvailable.Should().BeTrue(fallback.Reason);
        fallback.UnmetRequirements.Should().BeEmpty();
    }

    [Fact]
    public void A_limit_above_the_adapter_fails_with_the_limit_and_both_values()
    {
        //Arrange
        gpu.RequireDevice();
        WebGpuSkiaDevice reference = gpu.Device;
        uint best = reference.AdapterLimits.MaxTextureDimension2D;
        Limits limits = WebGpuSkiaLimits.Undefined();
        limits.MaxTextureDimension2D = best + 1;
        limits.MinUniformBufferOffsetAlignment = 3;
        WebGpuSkiaDeviceOptions options = OnFixtureBackend(reference);
        options.RequiredLimits = limits;

        //Act
        using WebGpuSkiaDevice refused = WebGpuSkiaDevice.Create(options);

        //Assert
        refused.IsAvailable.Should().BeFalse();
        refused.UnmetRequirements.Should().HaveCount(2);
        WebGpuSkiaUnmetRequirement dimension = refused.UnmetRequirements.Single(entry => entry.Name == "MaxTextureDimension2D");
        dimension.Kind.Should().Be(WebGpuSkiaRequirementKind.Limit);
        dimension.RequestedValue.Should().Be((ulong)best + 1);
        dimension.AdapterValue.Should().Be(best);
        dimension.Message.Should().Contain("exceeds the adapter's maximum of " + best);
        WebGpuSkiaUnmetRequirement alignment = refused.UnmetRequirements.Single(entry => entry.Name == "MinUniformBufferOffsetAlignment");
        alignment.Message.Should().Contain("not a power of two");
        refused.Reason.Should().Contain("MaxTextureDimension2D");
    }

    [Fact]
    public void An_alignment_below_the_adapter_minimum_and_an_experimental_native_feature_are_refused()
    {
        //Arrange
        gpu.RequireDevice();
        WebGpuSkiaDevice reference = gpu.Device;
        uint minimum = reference.AdapterLimits.MinStorageBufferOffsetAlignment;
        Assert.SkipWhen(minimum <= 1, "The adapter's storage alignment minimum is 1.");
        Limits limits = WebGpuSkiaLimits.Undefined();
        limits.MinStorageBufferOffsetAlignment = minimum / 2;
        WebGpuSkiaDeviceOptions options = OnFixtureBackend(reference);
        options.RequiredLimits = limits;
        options.RequiredNativeFeatures.Add(NativeFeature.RayQuery);

        //Act
        using WebGpuSkiaDevice refused = WebGpuSkiaDevice.Create(options);

        //Assert
        refused.IsAvailable.Should().BeFalse();
        refused.UnmetRequirements.Select(entry => entry.Name).Should().BeEquivalentTo(new[] { "RayQuery", "MinStorageBufferOffsetAlignment" });
        refused.UnmetRequirements.Single(entry => entry.Name == "RayQuery").Kind.Should().Be(WebGpuSkiaRequirementKind.NativeFeature);
        refused.UnmetRequirements.Single(entry => entry.Name == "MinStorageBufferOffsetAlignment").AdapterValue.Should().Be(minimum);
    }

    [Fact]
    public void Undefined_limits_ask_for_the_defaults()
    {
        //Arrange
        gpu.RequireDevice();
        WebGpuSkiaDeviceOptions options = OnFixtureBackend(gpu.Device);
        options.RequiredLimits = WebGpuSkiaLimits.Undefined();

        //Act
        using WebGpuSkiaDevice device = WebGpuSkiaDevice.Create(options);

        //Assert
        device.IsAvailable.Should().BeTrue(device.Reason);
        device.Limits.MaxTextureDimension2D.Should().Be(gpu.Device.Limits.MaxTextureDimension2D);
        device.Limits.MaxBindGroups.Should().Be(gpu.Device.Limits.MaxBindGroups);
    }

    [Fact]
    public void An_unavailable_device_exposes_empty_sets_and_no_loss()
    {
        //Arrange
        WebGpuSkiaBackend absent = OperatingSystem.IsMacOS() ? WebGpuSkiaBackend.Direct3D12 : WebGpuSkiaBackend.Metal;

        //Act
        using WebGpuSkiaDevice device = WebGpuSkiaDevice.Create(new WebGpuSkiaDeviceOptions { PreferredBackend = absent, AllowFallbackBackend = false });

        //Assert
        device.IsAvailable.Should().BeFalse();
        device.Features.Should().BeEmpty();
        device.NativeFeatures.Should().BeEmpty();
        device.AdapterFeatures.Should().BeEmpty();
        device.UnmetRequirements.Should().BeEmpty();
        device.HasFeature(FeatureName.TimestampQuery).Should().BeFalse();
        device.IsLost.Should().BeFalse();
        device.LostReason.Should().BeNull();
    }

    [Fact]
    public void The_first_device_loss_report_wins()
    {
        //Arrange
        gpu.RequireDevice();
        using WebGpuSkiaDevice device = WebGpuSkiaDevice.Create(OnFixtureBackend(gpu.Device));
        bool lostBefore = device.IsLost;

        //Act
        device.ReportDeviceLost(DeviceLostReason.Destroyed, "first");
        device.ReportDeviceLost(DeviceLostReason.Unknown, "second");

        //Assert
        lostBefore.Should().BeFalse();
        device.IsLost.Should().BeTrue();
        device.LostReason.Should().Be("Destroyed: first");
    }
}
