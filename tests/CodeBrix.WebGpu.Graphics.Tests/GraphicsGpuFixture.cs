using System;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>
/// One <see cref="GraphicsContext" /> on the platform's default backend (Vulkan on Linux, Direct3D 12 or Vulkan on
/// Windows, Metal on macOS), shared by the GPU tests of a class. When no device can be created the tests skip with
/// the reason.
/// </summary>
public sealed class GraphicsGpuFixture : IDisposable
{
    /// <summary>Creates the context.</summary>
    public GraphicsGpuFixture()
    {
        GraphicsContext.TryCreate(null, out GraphicsContext context, out string reason);
        Context = context;
        Reason = reason;
    }

    /// <summary>The context, or null.</summary>
    public GraphicsContext Context { get; }

    /// <summary>Why there is no context, or null.</summary>
    public string Reason { get; }

    /// <summary>Skips the test when there is no device.</summary>
    public void RequireDevice() => Assert.SkipWhen(Context == null, "No WebGPU device: " + Reason);

    /// <summary>Releases the context.</summary>
    public void Dispose() => Context?.Dispose();
}
