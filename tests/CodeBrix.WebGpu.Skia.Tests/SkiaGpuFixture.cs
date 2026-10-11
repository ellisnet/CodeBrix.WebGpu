using System;
using SkiaSharp;
using Xunit;

namespace CodeBrix.WebGpu.Skia.Tests;

/// <summary>
/// One <see cref="WebGpuSkiaDevice" /> on the platform's backend (Vulkan on Linux, Direct3D 12 on Windows, Metal
/// on macOS) and its Skia context, shared by the GPU tests of a class. When either cannot be created the tests
/// skip with the reason.
/// </summary>
public sealed class SkiaGpuFixture : IDisposable
{
    /// <summary>Creates the device and its Skia context.</summary>
    public SkiaGpuFixture()
    {
        Device = WebGpuSkiaDevice.Create();
        GrContext = Device.IsAvailable ? Device.CreateGrContext() : null;
    }

    /// <summary>The device (check <see cref="WebGpuSkiaDevice.IsAvailable" />).</summary>
    public WebGpuSkiaDevice Device { get; }

    /// <summary>The device's Skia context, or null.</summary>
    public GRContext GrContext { get; }

    /// <summary>Skips the test when the device is unavailable.</summary>
    public void RequireDevice() => Assert.SkipWhen(!Device.IsAvailable, "No WebGPU device: " + Device.Reason);

    /// <summary>Skips the test when the device or its Skia context is unavailable.</summary>
    public void RequireGrContext()
    {
        RequireDevice();
        Assert.SkipWhen(GrContext == null, "No Skia GRContext on the WebGPU device: " + Device.GrContextReason);
    }

    /// <summary>Releases the device (and its context).</summary>
    public void Dispose() => Device.Dispose();
}
