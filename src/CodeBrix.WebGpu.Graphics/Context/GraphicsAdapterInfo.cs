using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// One GPU adapter: its identity, backend and capabilities. <see cref="GraphicsContext.EnumerateAdapters" /> lists
/// them; <see cref="GraphicsContextOptions.AdapterSelector" /> chooses one; <see cref="GraphicsContext.AdapterInfo" />
/// describes the one a context runs on.
/// </summary>
public sealed class GraphicsAdapterInfo
{
    internal GraphicsAdapterInfo(int index, AdapterInfo info, GraphicsCapabilities capabilities)
    {
        Index = index;
        Name = WgpuString.ToManaged(info.Device) ?? string.Empty;
        Vendor = WgpuString.ToManaged(info.Vendor) ?? string.Empty;
        Architecture = WgpuString.ToManaged(info.Architecture) ?? string.Empty;
        Description = WgpuString.ToManaged(info.Description) ?? string.Empty;
        Backend = info.BackendType;
        AdapterType = info.AdapterType;
        VendorId = info.VendorID;
        DeviceId = info.DeviceID;
        Capabilities = capabilities;
    }

    /// <summary>The position in the enumeration (-1 for an adapter that was not enumerated, such as a context's own).</summary>
    public int Index { get; }

    /// <summary>The GPU's name, for example <c>Intel(R) UHD Graphics</c>.</summary>
    public string Name { get; }

    /// <summary>The vendor or driver name reported by the backend (may be empty).</summary>
    public string Vendor { get; }

    /// <summary>The architecture (may be empty).</summary>
    public string Architecture { get; }

    /// <summary>The driver description (may be empty).</summary>
    public string Description { get; }

    /// <summary>The native GPU API.</summary>
    public BackendType Backend { get; }

    /// <summary>Discrete, integrated, CPU or unknown.</summary>
    public AdapterType AdapterType { get; }

    /// <summary>The PCI vendor id (0 when unknown).</summary>
    public uint VendorId { get; }

    /// <summary>The PCI device id (0 when unknown).</summary>
    public uint DeviceId { get; }

    /// <summary>The adapter's features and limits.</summary>
    public GraphicsCapabilities Capabilities { get; }

    /// <summary>Describes the adapter.</summary>
    /// <returns>For example <c>#0 Intel(R) UHD Graphics (Vulkan, IntegratedGPU)</c>.</returns>
    public override string ToString() => "#" + Index + " " + Name + " (" + Backend + ", " + AdapterType + ")";

    /// <summary>Reads an adapter's info and capabilities.</summary>
    /// <param name="adapter">The adapter.</param>
    /// <param name="index">Its enumeration index, or -1.</param>
    /// <returns>The description, or null when the adapter reports no info.</returns>
    internal static unsafe GraphicsAdapterInfo Read(Adapter adapter, int index)
    {
        AdapterInfo info = default;
        if (Native.WebGpu.AdapterGetInfo(adapter, &info) != Status.Success) return null;
        try
        {
            return new GraphicsAdapterInfo(index, info, GraphicsCapabilities.FromAdapter(adapter));
        }
        finally
        {
            Native.WebGpu.AdapterInfoFreeMembers(info);
        }
    }
}
