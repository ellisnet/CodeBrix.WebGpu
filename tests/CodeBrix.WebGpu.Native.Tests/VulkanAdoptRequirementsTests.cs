using System;
using System.Collections.Generic;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Native.Tests;

/// <summary>wgpuCodebrixVulkanGetAdoptRequirements through <see cref="VulkanAdoptRequirements" /> on the Linux Vulkan device.</summary>
public unsafe class VulkanAdoptRequirementsTests : IClassFixture<GpuFixture>
{
    // VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_FEATURES_2
    private const uint PhysicalDeviceFeatures2Type = 1000059000u;

    private readonly GpuSession gpu;

    public VulkanAdoptRequirementsTests(GpuFixture fixture)
    {
        gpu = fixture.Session;
    }

    private CodebrixVulkanDeviceInfo VulkanInfo()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The Vulkan extras are exercised on Linux.");
        Assert.SkipWhen(gpu.SkipReason != null, gpu.SkipReason ?? string.Empty);
        CodebrixVulkanDeviceInfo info = default;
        WebGpu.CodebrixDeviceGetVulkanInfo(gpu.Device, &info).Should().Be(Status.Success);
        return info;
    }

    private static CodebrixVulkanAdoptDescriptor DescriptorFor(CodebrixVulkanDeviceInfo info) => new CodebrixVulkanAdoptDescriptor
    {
        Instance = info.Instance,
        PhysicalDevice = info.PhysicalDevice,
        InstanceApiVersion = info.InstanceApiVersion,
        GetInstanceProcAddr = info.GetInstanceProcAddr,
        EnabledInstanceExtensionCount = info.EnabledInstanceExtensionCount,
        EnabledInstanceExtensions = info.EnabledInstanceExtensions,
    };

    private static HashSet<string> Names(CodebrixName* names, nuint count)
    {
        HashSet<string> result = new HashSet<string>(StringComparer.Ordinal);
        for (nuint index = 0; index < count; index++) result.Add(WgpuString.ToManaged(names[index].Name));
        return result;
    }

    [Fact]
    public void Query_lists_the_extensions_wgpu_enabled_on_its_own_device()
    {
        //Arrange
        CodebrixVulkanDeviceInfo info = VulkanInfo();
        CodebrixVulkanAdoptDescriptor descriptor = DescriptorFor(info);

        //Act
        using VulkanAdoptRequirements requirements = VulkanAdoptRequirements.Query(&descriptor);

        //Assert
        requirements.RequiredDeviceExtensions.Should().NotBeEmpty().And.OnlyContain(name => name.StartsWith("VK_"));
        Names(info.EnabledDeviceExtensions, info.EnabledDeviceExtensionCount).IsSupersetOf(requirements.RequiredDeviceExtensions).Should().BeTrue();
        requirements.PhysicalDeviceApiVersion.Should().BeGreaterThanOrEqualTo(1u << 22);
    }

    [Fact]
    public void Query_returns_a_features2_chain_the_library_owns_until_disposed()
    {
        //Arrange
        CodebrixVulkanDeviceInfo info = VulkanInfo();
        CodebrixVulkanAdoptDescriptor descriptor = DescriptorFor(info);
        VulkanAdoptRequirements requirements = VulkanAdoptRequirements.Query(&descriptor);
        uint* features2 = (uint*)requirements.PhysicalDeviceFeatures2;

        //Act
        uint structureType = features2 == null ? 0u : features2[0];
        nint next = features2 == null ? 0 : *(nint*)((byte*)features2 + 8);
        requirements.Dispose();

        //Assert
        structureType.Should().Be(PhysicalDeviceFeatures2Type);
        next.Should().NotBe(0, "wgpu enables feature structures beyond Vulkan 1.0 (robustness2, timeline semaphores, ...)");
        requirements.PhysicalDeviceFeatures2.Should().Be(0);
        requirements.Dispose();
    }

    [Fact]
    public void Query_refuses_a_feature_the_adapter_lacks()
    {
        //Arrange
        CodebrixVulkanDeviceInfo info = VulkanInfo();
        CodebrixVulkanAdoptDescriptor descriptor = DescriptorFor(info);
        FeatureName unknown = (FeatureName)0x7FFF0001;
        descriptor.RequiredFeatureCount = 1;
        descriptor.RequiredFeatures = &unknown;

        nint address = (nint)(&descriptor);

        //Act
        Action query = () => VulkanAdoptRequirements.Query((CodebrixVulkanAdoptDescriptor*)address);

        //Assert
        query.Should().Throw<WgpuException>();
    }

    [Fact]
    public void Query_refuses_missing_handles_and_a_null_descriptor()
    {
        //Arrange
        CodeBrixWebGpuNative.EnsureLoaded();
        CodebrixVulkanAdoptDescriptor empty = default;
        CodebrixVulkanAdoptRequirements raw = default;
        nint address = (nint)(&empty);

        //Act
        Action missing = () => VulkanAdoptRequirements.Query((CodebrixVulkanAdoptDescriptor*)address);
        Action nothing = () => VulkanAdoptRequirements.Query(null);
        Status status = WebGpu.CodebrixVulkanGetAdoptRequirements(null, &raw);

        //Assert
        missing.Should().Throw<WgpuException>();
        nothing.Should().Throw<ArgumentNullException>();
        status.Should().Be(Status.Error);
        ((IntPtr)raw.PhysicalDeviceFeatures2).Should().Be(IntPtr.Zero);
    }
}
