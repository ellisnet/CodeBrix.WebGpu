using System;

namespace CodeBrix.WebGpu.Native;

/// <summary>Filling the device hand-off for a native module.</summary>
public unsafe partial struct CodebrixDeviceHandoff
{
    /// <summary>
    /// Fills a device hand-off from a device source into caller-provided memory: size, layout version, the loaded
    /// library's contract version, backend, flags, the function table (<see cref="CodeBrixWebGpuNative.GetProcTable" />)
    /// and the instance / adapter / device / queue handles. Nothing is allocated after the table's first build.
    /// </summary>
    /// <remarks>
    /// The handles are borrowed: the source keeps owning them and they stay valid until the source is disposed. A
    /// module that keeps one longer calls its <c>*AddRef</c> and later its <c>*Release</c>.
    /// </remarks>
    /// <param name="source">The device source (for example a <c>WebGpuSkiaDevice</c>).</param>
    /// <param name="destination">Where to write the struct (native or pinned memory).</param>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> or <paramref name="destination" /> is null.</exception>
    /// <exception cref="WgpuException">The native library cannot be loaded or checked.</exception>
    public static void Fill(IWebGpuDeviceSource source, CodebrixDeviceHandoff* destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (destination == null) throw new ArgumentNullException(nameof(destination));

        CodebrixProcTable* procs = CodeBrixWebGpuNative.GetProcTable();
        destination->Size = (uint)sizeof(CodebrixDeviceHandoff);
        destination->Version = CurrentVersion;
        destination->ApiVersion = procs->ApiVersion;
        destination->Backend = source.Backend;
        destination->Flags = source.HasUncapturedErrorHandler ? FlagUncapturedErrorHandler : 0u;
        destination->Reserved = 0;
        destination->Procs = procs;
        destination->Instance = source.Instance;
        destination->Adapter = source.Adapter;
        destination->Device = source.Device;
        destination->Queue = source.Queue;
    }

    /// <summary>Fills a device hand-off from a device source (see <see cref="Fill(IWebGpuDeviceSource, CodebrixDeviceHandoff*)" />).</summary>
    /// <param name="source">The device source.</param>
    /// <param name="destination">The struct to fill.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is null.</exception>
    /// <exception cref="WgpuException">The native library cannot be loaded or checked.</exception>
    public static void Fill(IWebGpuDeviceSource source, ref CodebrixDeviceHandoff destination)
    {
        fixed (CodebrixDeviceHandoff* pointer = &destination) Fill(source, pointer);
    }

    /// <summary>Returns a device hand-off for a device source (see <see cref="Fill(IWebGpuDeviceSource, CodebrixDeviceHandoff*)" />).</summary>
    /// <param name="source">The device source.</param>
    /// <returns>The hand-off.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> is null.</exception>
    /// <exception cref="WgpuException">The native library cannot be loaded or checked.</exception>
    public static CodebrixDeviceHandoff From(IWebGpuDeviceSource source)
    {
        CodebrixDeviceHandoff result = default;
        Fill(source, &result);
        return result;
    }
}
