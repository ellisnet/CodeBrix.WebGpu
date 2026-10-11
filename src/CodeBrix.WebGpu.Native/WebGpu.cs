using CodeBrix.WebGpu.Native.Interop;

namespace CodeBrix.WebGpu.Native;

/// <summary>The hand-written half of the generated <see cref="WebGpu" /> import class.</summary>
public static unsafe partial class WebGpu
{
    /// <summary>The library name every import asks for; the resolver maps it to the platform's file.</summary>
    public const string LibraryName = WgpuLibrary.LibraryName;

    // Installs the DllImportResolver before any import can run, so a first call that does not go through
    // CodeBrixWebGpuNative.EnsureLoaded still finds the native under runtimes/<rid>/native/.
    static WebGpu()
    {
        WgpuLibrary.EnsureResolverInstalled();
    }
}
