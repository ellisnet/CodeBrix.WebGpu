using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using CodeBrix.WebGpu.Native.Interop;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Native.Tests;

public class WgpuLibraryTests
{
    [Fact]
    public void EnumerateProbePaths_tries_the_runtimes_folder_before_the_application_folder()
    {
        //Arrange
        string root = Path.Combine(Path.GetTempPath(), "app");

        //Act
        IReadOnlyList<string> paths = WgpuLibrary.EnumerateProbePaths(new[] { root, root, null }, "linux-arm64", "libcodebrix_wgpu.so");

        //Assert
        paths.Should().Equal(
            Path.Combine(root, "runtimes", "linux-arm64", "native", "libcodebrix_wgpu.so"),
            Path.Combine(root, "libcodebrix_wgpu.so"));
    }

    [Fact]
    public void DescribeLoadFailure_names_the_runtime_and_every_path()
    {
        //Arrange
        string root = Path.Combine(Path.GetTempPath(), "app");

        //Act
        string message = WgpuLibrary.DescribeLoadFailure(new[] { root }, "osx-arm64", "libcodebrix_wgpu.dylib");

        //Assert
        message.Should().Contain("'osx-arm64'");
        message.Should().Contain(Path.Combine(root, "runtimes", "osx-arm64", "native", "libcodebrix_wgpu.dylib"));
        message.Should().Contain("runtimes/osx-arm64/native/");
    }

    [Fact]
    public void DescribeVersionMismatch_names_both_versions()
    {
        //Act
        string message = WgpuLibrary.DescribeVersionMismatch("/x/libcodebrix_wgpu.so", 7);

        //Assert
        message.Should().Contain("contract version 7");
        message.Should().Contain("contract version 1");
    }

    [Fact]
    public void NativeFileName_matches_the_platform()
    {
        //Arrange
        string expected = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "codebrix_wgpu.dll"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "libcodebrix_wgpu.dylib"
            : "libcodebrix_wgpu.so";

        //Act
        string name = WgpuLibrary.NativeFileName;

        //Assert
        name.Should().Be(expected);
    }

    [Fact]
    public void EnsureLoaded_marks_the_library_loaded()
    {
        //Act
        WgpuLibrary.EnsureLoaded();

        //Assert
        WgpuLibrary.IsLoaded.Should().BeTrue();
    }

    [Fact]
    public void LibraryName_is_the_import_name()
        => WebGpu.LibraryName.Should().Be("codebrix_wgpu");
}
