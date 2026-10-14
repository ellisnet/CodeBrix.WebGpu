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
            GraphicsDiagnostics.LogLevel = LogLevel.Info;
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
        messages.Should().OnlyContain(entry => entry.Level <= LogLevel.Info && entry.Level != LogLevel.Off);
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
