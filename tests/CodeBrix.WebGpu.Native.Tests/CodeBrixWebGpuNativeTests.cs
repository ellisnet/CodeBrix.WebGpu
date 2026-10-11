using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Native.Tests;

public class CodeBrixWebGpuNativeTests
{
    [Fact]
    public void EnsureLoaded_loads_the_native_library()
    {
        //Act
        CodeBrixWebGpuNative.EnsureLoaded();

        //Assert
        CodeBrixWebGpuNative.LoadedPath.Should().NotBeNullOrEmpty();
        File.Exists(CodeBrixWebGpuNative.LoadedPath).Should().BeTrue();
    }

    [Fact]
    public void ApiVersion_is_the_generated_contract_version()
    {
        //Act
        uint version = CodeBrixWebGpuNative.ApiVersion;

        //Assert
        version.Should().Be(1u);
        version.Should().Be(CodeBrixWebGpuNative.ExpectedApiVersion);
    }

    [Fact]
    public void PackedLibraryVersion_is_29_0_1_1()
        => CodeBrixWebGpuNative.PackedLibraryVersion.Should().Be(0x1D000101u);

    [Fact]
    public void LibraryVersion_unpacks_the_packed_value()
        => CodeBrixWebGpuNative.LibraryVersion.Should().Be(new Version(29, 0, 1, 1));

    [Theory]
    [InlineData(0x1D000101u, "29.0.1.1")]
    [InlineData(0x04010000u, "4.1.0.0")]
    [InlineData(0xFFFFFFFFu, "255.255.255.255")]
    public void UnpackVersion_reads_one_byte_per_part(uint packed, string expected)
        => CodeBrixWebGpuNative.UnpackVersion(packed).Should().Be(Version.Parse(expected));

    [Fact]
    public void BuildInfo_reports_every_field()
    {
        //Act
        WgpuBuildInfo info = CodeBrixWebGpuNative.BuildInfo;

        //Assert
        info.ApiVersion.Should().Be(1u);
        info.WgpuNativeVersion.Should().StartWith("29.0.1");
        info.WgpuCoreVersion.Should().StartWith("29.");
        info.Backends.Should().NotBeNullOrEmpty();
        info.TargetTriple.Should().NotBeNullOrEmpty();
        info.Profile.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void BuildInfo_lists_vulkan_on_linux()
    {
        //Arrange
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux builds enable Vulkan; other platforms differ.");

        //Act
        string backends = CodeBrixWebGpuNative.BuildInfo.Backends;

        //Assert
        backends.Split(',').Should().Contain("vulkan");
    }

    [Fact]
    public void CheckStructLayouts_matches_every_generated_struct()
    {
        //Act
        IReadOnlyList<StructLayoutCheck> checks = CodeBrixWebGpuNative.CheckStructLayouts();

        //Assert
        checks.Should().HaveCount(CodeBrixWebGpuNative.ManagedStructLayouts().Count);
        checks.Where(check => !check.Matches).Select(check => check.Managed.CName + " " + check.Managed + " vs " + check.Native)
            .Should().BeEmpty();
    }

    [Fact]
    public void ManagedStructLayouts_covers_the_core_extension_and_codebrix_structs()
        => CodeBrixWebGpuNative.ManagedStructLayouts().Select(layout => layout.CName).Should().Contain(new[]
        {
            "WGPUStringView", "WGPUChainedStruct", "WGPUTextureDescriptor", "WGPURequestAdapterCallbackInfo",
            "WGPUInstanceExtras", "WGPUNativeDisplayHandle", "WGPUCodebrixVulkanDeviceInfo", "WGPUCodebrixName",
        });

    [Fact]
    public void NativeStructLayout_returns_null_for_an_unknown_name()
        => CodeBrixWebGpuNative.NativeStructLayout("WGPUNoSuchStruct").Should().BeNull();

    [Fact]
    public void NativeStructLayout_reports_the_string_view()
        => CodeBrixWebGpuNative.NativeStructLayout("WGPUStringView").Should().Be(new StructLayoutInfo("WGPUStringView", IntPtr.Size * 2, IntPtr.Size));

    [Fact]
    public void VerifyStructLayouts_does_not_throw()
    {
        //Act
        Action act = CodeBrixWebGpuNative.VerifyStructLayouts;

        //Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void RuntimeIdentifier_names_this_platform()
        => CodeBrixWebGpuNative.RuntimeIdentifier.Should().MatchRegex("^(linux|win|osx|android)-(x64|arm64|riscv64)$");

    [Fact]
    public unsafe void CodebrixGetStructLayout_returns_error_for_an_unknown_name()
    {
        //Arrange
        CodeBrixWebGpuNative.EnsureLoaded();
        byte[] name = "WGPUNotAStruct"u8.ToArray();
        nuint size = 99;
        nuint alignment = 99;
        Status status;

        //Act
        fixed (byte* text = name)
        {
            status = WebGpu.CodebrixGetStructLayout(WgpuString.FromPinned(text, name.Length), &size, &alignment);
        }

        //Assert
        status.Should().Be(Status.Error);
    }
}
