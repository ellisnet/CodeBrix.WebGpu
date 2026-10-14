using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>How the native WebGPU library was built (<see cref="GraphicsDiagnostics.BuildInfo" />).</summary>
public sealed class GraphicsBuildInfo
{
    internal GraphicsBuildInfo(CodebrixBuildInfo info)
    {
        ApiVersion = info.ApiVersion;
        EngineVersion = WgpuString.ToManaged(info.WgpuNativeVersion) ?? string.Empty;
        CoreVersion = WgpuString.ToManaged(info.WgpuCoreVersion) ?? string.Empty;
        Backends = WgpuString.ToManaged(info.Backends) ?? string.Empty;
        TargetTriple = WgpuString.ToManaged(info.TargetTriple) ?? string.Empty;
        Profile = WgpuString.ToManaged(info.Profile) ?? string.Empty;
    }

    /// <summary>The native extras contract version.</summary>
    public uint ApiVersion { get; }

    /// <summary>The native engine's release version.</summary>
    public string EngineVersion { get; }

    /// <summary>The version of the engine's core crate.</summary>
    public string CoreVersion { get; }

    /// <summary>The GPU backends compiled in, comma separated.</summary>
    public string Backends { get; }

    /// <summary>The target the library was compiled for.</summary>
    public string TargetTriple { get; }

    /// <summary>The build profile (release or debug).</summary>
    public string Profile { get; }

    /// <summary>Summarises the build.</summary>
    /// <returns>For example <c>29.0.1.1 (core 29.0.3) x86_64-unknown-linux-gnu release: vulkan, gl</c>.</returns>
    public override string ToString() => EngineVersion + " (core " + CoreVersion + ") " + TargetTriple + " " + Profile + ": " + Backends;
}
