using System;
using System.Collections.Generic;
using System.Linq;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>Adapters, features, limits and the device-creation options that request them.</summary>
public class GraphicsCapabilitiesTests : IClassFixture<GraphicsGpuFixture>
{
    private readonly GraphicsGpuFixture gpu;

    public GraphicsCapabilitiesTests(GraphicsGpuFixture fixture)
    {
        gpu = fixture;
    }

    [Fact]
    public void Undefined_limits_mark_every_field_undefined()
    {
        //Arrange / Act
        Limits limits = GraphicsLimits.Undefined();

        //Assert
        limits.MaxImmediateSize.Should().Be(Constants.LimitU32Undefined);
        limits.MaxBufferSize.Should().Be(Constants.LimitU64Undefined);
        limits.MaxTextureDimension2D.Should().Be(Constants.LimitU32Undefined);
        limits.MaxComputeWorkgroupsPerDimension.Should().Be(Constants.LimitU32Undefined);
    }

    [Fact]
    public void Options_default_to_no_extra_features_or_limits()
    {
        //Arrange / Act
        GraphicsContextOptions options = new GraphicsContextOptions();

        //Assert
        options.RequiredFeatures.Should().BeEmpty();
        options.RequiredNativeFeatures.Should().BeEmpty();
        options.RequiredLimits.HasValue.Should().BeFalse();
        options.RequestAllAdapterFeatures.Should().BeFalse();
        options.AdapterSelector.Should().BeNull();
        options.InstanceFlags.Should().Be(InstanceFlag.Empty);
    }

    [Fact]
    public void Device_capabilities_report_features_and_limits()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        GraphicsCapabilities capabilities = gpu.Context.Capabilities;

        //Assert
        capabilities.Limits.MaxTextureDimension2D.Should().BeGreaterThanOrEqualTo(2048u);
        capabilities.Limits.MaxBindGroups.Should().BeGreaterThanOrEqualTo(4u);
        capabilities.Features.Where(feature => (uint)feature >= 0x00030000u).Should().BeEmpty();
        capabilities.NativeFeatures.Where(feature => (uint)feature < 0x00030000u).Should().BeEmpty();
        capabilities.ToString().Should().Contain("native:");
    }

    [Fact]
    public void Adapter_capabilities_include_every_device_feature()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        GraphicsCapabilities adapter = gpu.Context.AdapterCapabilities;
        GraphicsCapabilities device = gpu.Context.Capabilities;

        //Assert
        adapter.Should().NotBeNull();
        device.Features.Where(feature => !adapter.Has(feature)).Should().BeEmpty();
        device.NativeFeatures.Where(feature => !adapter.Has(feature)).Should().BeEmpty();
    }

    [Fact]
    public void HasFeature_agrees_with_the_capability_lists()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;

        //Act / Assert
        foreach (FeatureName feature in Enum.GetValues<FeatureName>())
        {
            context.HasFeature(feature).Should().Be(context.Capabilities.Has(feature));
            context.AdapterHasFeature(feature).Should().Be(context.AdapterCapabilities.Has(feature));
        }

        foreach (NativeFeature feature in Enum.GetValues<NativeFeature>())
        {
            context.HasFeature(feature).Should().Be(context.Capabilities.Has(feature));
            context.AdapterHasFeature(feature).Should().Be(context.AdapterCapabilities.Has(feature));
        }
    }

    [Fact]
    public void AdapterInfo_names_the_context_adapter()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        GraphicsAdapterInfo info = gpu.Context.AdapterInfo;

        //Assert
        info.Should().NotBeNull();
        info.Name.Should().NotBeNullOrEmpty();
        info.Backend.Should().Be(gpu.Context.Backend);
        info.Index.Should().Be(-1);
        info.Capabilities.Should().NotBeNull();
    }

    [Fact]
    public void EnumerateAdapters_lists_the_adapter_in_use()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        IReadOnlyList<GraphicsAdapterInfo> adapters = GraphicsContext.EnumerateAdapters();

        //Assert
        adapters.Should().NotBeEmpty();
        adapters.Select(adapter => adapter.Index).Should().Equal(Enumerable.Range(0, adapters.Count));
        adapters.Should().Contain(adapter => adapter.Name == gpu.Context.AdapterInfo.Name);
    }

    [Fact]
    public void AdapterSelector_picks_the_matching_adapter()
    {
        //Arrange
        gpu.RequireDevice();
        string name = gpu.Context.AdapterInfo.Name;
        BackendType backend = gpu.Context.Backend;

        //Act
        using GraphicsContext context = GraphicsContext.Create(new GraphicsContextOptions { AdapterSelector = info => info.Name == name && info.Backend == backend });

        //Assert
        context.AdapterInfo.Name.Should().Be(name);
        context.Backend.Should().Be(backend);
    }

    [Fact]
    public void AdapterSelector_matching_nothing_fails_with_the_adapter_list()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        bool created = GraphicsContext.TryCreate(new GraphicsContextOptions { AdapterSelector = info => false }, out GraphicsContext context, out string reason);

        //Assert
        created.Should().BeFalse();
        context.Should().BeNull();
        reason.Should().Contain("selector");
    }

    [Fact]
    public void Required_features_are_enabled_on_the_device()
    {
        //Arrange
        gpu.RequireDevice();
        Assert.SkipUnless(gpu.Context.AdapterHasFeature(FeatureName.TimestampQuery), "The adapter has no timestamp queries.");

        //Act
        using GraphicsContext context = GraphicsContext.Create(new GraphicsContextOptions { RequiredFeatures = { FeatureName.TimestampQuery } });

        //Assert
        context.HasFeature(FeatureName.TimestampQuery).Should().BeTrue();
        context.Capabilities.Has(FeatureName.TimestampQuery).Should().BeTrue();
    }

    [Fact]
    public void RequestAllAdapterFeatures_enables_every_non_experimental_adapter_feature()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsCapabilities adapter = gpu.Context.AdapterCapabilities;

        //Act
        using GraphicsContext context = GraphicsContext.Create(new GraphicsContextOptions { RequestAllAdapterFeatures = true });

        //Assert
        context.Capabilities.Features.Should().BeEquivalentTo(adapter.Features);
        context.Capabilities.NativeFeatures.Should().BeEquivalentTo(adapter.NativeFeatures.Where(feature => !GraphicsCapabilities.ExperimentalNativeFeatures.Contains(feature)));
    }

    [Fact]
    public void RequestAdapterLimits_raises_the_device_limits_to_the_adapter_limits()
    {
        //Arrange
        gpu.RequireDevice();
        Limits adapter = gpu.Context.AdapterCapabilities.Limits;

        //Act
        using GraphicsContext context = GraphicsContext.Create(new GraphicsContextOptions { RequestAdapterLimits = true });

        //Assert
        context.Limits.MaxTextureDimension2D.Should().Be(adapter.MaxTextureDimension2D);
        context.Limits.MaxImmediateSize.Should().Be(adapter.MaxImmediateSize);
    }

    [Fact]
    public void RequiredLimits_sets_one_limit_and_keeps_the_defaults()
    {
        //Arrange
        gpu.RequireDevice();
        Assert.SkipUnless(gpu.Context.AdapterCapabilities.Limits.MaxImmediateSize >= 64, "The adapter has no immediate data.");
        Limits limits = GraphicsLimits.Undefined();
        limits.MaxImmediateSize = 64;

        //Act
        using GraphicsContext context = GraphicsContext.Create(new GraphicsContextOptions { RequiredLimits = limits, RequiredNativeFeatures = { NativeFeature.Immediates } });

        //Assert
        context.Limits.MaxImmediateSize.Should().BeGreaterThanOrEqualTo(64u);
        context.Limits.MaxTextureDimension2D.Should().Be(gpu.Context.Limits.MaxTextureDimension2D);
        context.HasFeature(NativeFeature.Immediates).Should().BeTrue();
    }

    [Fact]
    public void TimestampPeriod_is_positive()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        float period = gpu.Context.TimestampPeriod;

        //Assert
        period.Should().BeGreaterThan(0f);
    }
}
