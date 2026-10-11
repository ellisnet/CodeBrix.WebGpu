using System;

namespace CodeBrix.WebGpu.Native.Tests;

/// <summary>One Vulkan <see cref="GpuSession" /> shared by the GPU tests of a class.</summary>
public sealed class GpuFixture : IDisposable
{
    /// <summary>Creates the session.</summary>
    public GpuFixture()
    {
        Session = GpuSession.CreateVulkan();
    }

    /// <summary>The session.</summary>
    public GpuSession Session { get; }

    /// <summary>Releases the session.</summary>
    public void Dispose() => Session.Dispose();
}
