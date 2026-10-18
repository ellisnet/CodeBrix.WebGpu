using System;
using System.Collections.Concurrent;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>Engine version, build info, struct layouts, instance limits and the log callback.</summary>
public class GraphicsDiagnosticsTests : IClassFixture<GraphicsGpuFixture>
{
    private readonly GraphicsGpuFixture gpu;

    public GraphicsDiagnosticsTests(GraphicsGpuFixture fixture)
    {
        gpu = fixture;
    }

    [Fact]
    public void EngineVersion_matches_the_build_info()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        Version version = GraphicsDiagnostics.EngineVersion;
        GraphicsBuildInfo build = GraphicsDiagnostics.BuildInfo;

        //Assert
        version.Major.Should().BeGreaterThan(0);
        build.EngineVersion.TrimStart('v').Should().StartWith(version.Major + "." + version.Minor);
        build.CoreVersion.Should().NotBeNullOrEmpty();
        build.TargetTriple.Should().NotBeNullOrEmpty();
        build.Backends.Should().NotBeNullOrEmpty();
        build.ApiVersion.Should().Be(GraphicsDiagnostics.NativeApiVersion);
        build.ToString().Should().Contain(build.TargetTriple);
    }

    [Fact]
    public void TryGetNativeStructLayout_knows_the_limits_struct()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        bool known = GraphicsDiagnostics.TryGetNativeStructLayout("Limits", out ulong size, out ulong alignment);
        bool prefixed = GraphicsDiagnostics.TryGetNativeStructLayout("WGPULimits", out ulong prefixedSize, out _);
        bool unknown = GraphicsDiagnostics.TryGetNativeStructLayout("NoSuchStruct", out _, out _);

        //Assert
        known.Should().BeTrue();
        prefixed.Should().BeTrue();
        prefixedSize.Should().Be(size);
        size.Should().Be((ulong)System.Runtime.CompilerServices.Unsafe.SizeOf<Limits>());
        alignment.Should().Be(8UL);
        unknown.Should().BeFalse();
    }

    [Fact]
    public void Timed_waits_on_futures_are_not_available()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        ulong count = GraphicsDiagnostics.TimedWaitAnyMaxCount;

        //Assert
        count.Should().Be(0UL);
    }

    [Fact]
    public void Log_callback_receives_engine_messages_at_the_chosen_level()
    {
        //Arrange
        gpu.RequireDevice();
        ConcurrentQueue<(LogLevel Level, string Message)> messages = new ConcurrentQueue<(LogLevel, string)>();
        LogLevel previous = GraphicsDiagnostics.LogLevel;

        //Act
        try
        {
            // Debug, not Info. Creating a context and waiting on it logs NOTHING at Info or above on any backend:
            // wgpu-core's instance and adapter-selection lines are Debug, wgpu-hal's Direct3D 12 and Metal backends
            // have no Info line at all, and Vulkan's only one ("Found N cooperative matrix configurations") fires
            // just for drivers that report VK_KHR_cooperative_matrix. At Info this test therefore passed only on
            // machines with such a driver (Mesa's llvmpipe) and failed on Windows (2026-10-17). The backend-creation
            // and adapter-selection lines are Debug on every backend, on every platform.
            GraphicsDiagnostics.LogLevel = LogLevel.Debug;
            GraphicsDiagnostics.SetLogCallback((level, message) => messages.Enqueue((level, message)));
            using (GraphicsContext context = GraphicsContext.Create())
            {
                context.WaitIdle();
            }
        }
        finally
        {
            GraphicsDiagnostics.SetLogCallback(null);
            GraphicsDiagnostics.LogLevel = previous;
        }

        //Assert
        messages.Should().NotBeEmpty();
        messages.Should().Contain(entry => entry.Level == LogLevel.Debug);
        messages.Should().OnlyContain(entry => entry.Level <= LogLevel.Debug && entry.Level != LogLevel.Off);
    }

    [Fact]
    public void LogLevel_rejects_unknown_levels()
    {
        //Arrange / Act
        Action act = () => GraphicsDiagnostics.LogLevel = (LogLevel)42;

        //Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
