using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CodeBrix.WebGpu.Native.Interop;

namespace CodeBrix.WebGpu.Native;

/// <summary>
/// Loading and diagnostics for the native codebrix_wgpu library: load it, check its contract version and
/// struct layouts, and report what it was built from. Everything else is in <see cref="WebGpu" />.
/// </summary>
public static unsafe class CodeBrixWebGpuNative
{
    private static readonly object Gate = new object();
    private static bool layoutsVerified;

    /// <summary>
    /// Loads the native library (if it is not loaded yet), refuses it unless its contract version is the
    /// one this binding was generated from, and checks every generated struct's size and alignment against
    /// the native library. Call it once before anything else; it is cheap after the first call.
    /// </summary>
    /// <exception cref="WgpuException">
    /// The library is missing (the message lists every path tried), reports another contract version, or
    /// disagrees about a struct layout (the message lists the structs).
    /// </exception>
    public static void EnsureLoaded()
    {
        WgpuLibrary.EnsureLoaded();
        lock (Gate)
        {
            if (layoutsVerified) return;
        }

        VerifyStructLayouts();
        lock (Gate) layoutsVerified = true;
    }

    /// <summary>The contract version this binding was generated from (<c>CODEBRIX_API_VERSION</c>).</summary>
    public static uint ExpectedApiVersion => Constants.ApiVersion;

    /// <summary>The contract version the loaded native library reports.</summary>
    /// <exception cref="WgpuException">The library cannot be loaded or speaks another contract version.</exception>
    public static uint ApiVersion
    {
        get
        {
            WgpuLibrary.EnsureLoaded();
            return WebGpu.CodebrixGetApiVersion();
        }
    }

    /// <summary>Where the native library was loaded from, or null before it has been.</summary>
    public static string LoadedPath => WgpuLibrary.LoadedPath;

    /// <summary>The runtime identifier whose native this process uses (for example <c>linux-x64</c>).</summary>
    /// <exception cref="WgpuException">The package has no native for this platform.</exception>
    public static string RuntimeIdentifier => WgpuLibrary.RuntimeIdentifier;

    /// <summary>The wgpu engine release the native library wraps, as <c>wgpuGetVersion</c> packs it (one byte per part, most significant first).</summary>
    /// <exception cref="WgpuException">The library cannot be loaded or speaks another contract version.</exception>
    public static uint PackedLibraryVersion
    {
        get
        {
            WgpuLibrary.EnsureLoaded();
            return WebGpu.GetVersion();
        }
    }

    /// <summary>The wgpu engine release the native library wraps (major.minor.patch.build).</summary>
    /// <exception cref="WgpuException">The library cannot be loaded or speaks another contract version.</exception>
    public static Version LibraryVersion => UnpackVersion(PackedLibraryVersion);

    /// <summary>What the native library was built from.</summary>
    /// <exception cref="WgpuException">The library cannot be loaded or speaks another contract version.</exception>
    public static WgpuBuildInfo BuildInfo
    {
        get
        {
            WgpuLibrary.EnsureLoaded();
            CodebrixBuildInfo info = default;
            WebGpu.CodebrixGetBuildInfo(&info);
            return new WgpuBuildInfo(
                info.ApiVersion,
                WgpuString.ToManaged(info.WgpuNativeVersion),
                WgpuString.ToManaged(info.WgpuCoreVersion),
                WgpuString.ToManaged(info.Backends),
                WgpuString.ToManaged(info.TargetTriple),
                WgpuString.ToManaged(info.Profile));
        }
    }

    /// <summary>Splits a <c>wgpuGetVersion</c> value into its four bytes.</summary>
    /// <param name="packed">The packed value (0x1D000101 is 29.0.1.1).</param>
    /// <returns>The version.</returns>
    public static Version UnpackVersion(uint packed) =>
        new Version((int)(packed >> 24), (int)((packed >> 16) & 0xFF), (int)((packed >> 8) & 0xFF), (int)(packed & 0xFF));

    /// <summary>The managed size and alignment of every generated struct, sorted by C name.</summary>
    /// <returns>The layouts.</returns>
    public static IReadOnlyList<StructLayoutInfo> ManagedStructLayouts() => GeneratedStructLayouts.Managed();

    /// <summary>The native library's size and alignment for a struct, by C type name.</summary>
    /// <param name="cName">The C type name, for example <c>WGPUTextureDescriptor</c>.</param>
    /// <returns>The native layout, or null when the library does not know the name.</returns>
    /// <exception cref="WgpuException">The library cannot be loaded or speaks another contract version.</exception>
    public static StructLayoutInfo? NativeStructLayout(string cName)
    {
        ArgumentNullException.ThrowIfNull(cName);
        WgpuLibrary.EnsureLoaded();
        byte[] utf8 = Encoding.UTF8.GetBytes(cName);
        nuint size = 0;
        nuint alignment = 0;
        Status status;
        fixed (byte* name = utf8)
        {
            status = WebGpu.CodebrixGetStructLayout(WgpuString.FromPinned(name, utf8.Length), &size, &alignment);
        }

        return status == Status.Success ? new StructLayoutInfo(cName, checked((int)size), checked((int)alignment)) : null;
    }

    /// <summary>Compares every generated struct with the native library's layout.</summary>
    /// <returns>One check per generated struct, sorted by C name.</returns>
    /// <exception cref="WgpuException">The library cannot be loaded or speaks another contract version.</exception>
    public static IReadOnlyList<StructLayoutCheck> CheckStructLayouts() =>
        GeneratedStructLayouts.Managed().Select(managed => new StructLayoutCheck(managed, NativeStructLayout(managed.CName))).ToList();

    /// <summary>Throws unless every generated struct has the native library's size and alignment.</summary>
    /// <exception cref="WgpuException">At least one struct differs or is unknown to the native library; the message lists them all.</exception>
    public static void VerifyStructLayouts()
    {
        List<StructLayoutCheck> failures = CheckStructLayouts().Where(check => !check.Matches).ToList();
        if (failures.Count == 0) return;

        StringBuilder message = new StringBuilder();
        message.Append("The native codebrix_wgpu library at '").Append(LoadedPath)
            .Append("' disagrees with this binding about ").Append(failures.Count).Append(" struct layout(s); nothing may be called:");
        foreach (StructLayoutCheck failure in failures)
        {
            message.Append(Environment.NewLine).Append("    ").Append(failure.Managed.CName)
                .Append(": managed size ").Append(failure.Managed.Size).Append(" align ").Append(failure.Managed.Alignment)
                .Append(failure.Native.HasValue
                    ? ", native size " + failure.Native.Value.Size + " align " + failure.Native.Value.Alignment
                    : ", unknown to the native library");
        }

        throw new WgpuException(message.ToString());
    }
}

/// <summary>What the native codebrix_wgpu library was built from (<c>wgpuCodebrixGetBuildInfo</c>).</summary>
/// <param name="ApiVersion">The contract version.</param>
/// <param name="WgpuNativeVersion">The native C API release wrapped, dotted.</param>
/// <param name="WgpuCoreVersion">The wgpu engine version compiled in.</param>
/// <param name="Backends">Comma-separated backends compiled in (for example <c>vulkan,gles</c>).</param>
/// <param name="TargetTriple">The target triple the library was built for.</param>
/// <param name="Profile">The build profile.</param>
public sealed record WgpuBuildInfo(
    uint ApiVersion,
    string WgpuNativeVersion,
    string WgpuCoreVersion,
    string Backends,
    string TargetTriple,
    string Profile);
