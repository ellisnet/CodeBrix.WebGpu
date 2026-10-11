using System;

namespace CodeBrix.WebGpu.Native.Tests;

/// <summary>One <see cref="GpuSession" /> on <see cref="GpuSession.PlatformBackend" /> shared by the GPU tests of a class.</summary>
public sealed class GpuFixture : IDisposable
{
    /// <summary>Creates the session.</summary>
    public GpuFixture()
    {
        Session = GpuSession.CreateForThisPlatform();
    }

    /// <summary>The session.</summary>
    public GpuSession Session { get; }

    /// <summary>Releases the session.</summary>
    public void Dispose() => Session.Dispose();
}
