using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>How <see cref="GraphicsContext.Create(GraphicsContextOptions)" /> chooses its backend and adapter.</summary>
public sealed class GraphicsContextOptions
{
    /// <summary>The GPU API. <see cref="GraphicsBackend.Default" /> (the default) lets the platform choose.</summary>
    public GraphicsBackend Backend { get; set; } = GraphicsBackend.Default;

    /// <summary>The adapter power preference. Default <see cref="PowerPreference.HighPerformance" />.</summary>
    public PowerPreference PowerPreference { get; set; } = PowerPreference.HighPerformance;

    /// <summary>Ask for the software (fallback) adapter, for example on a machine without a GPU. Default false.</summary>
    public bool ForceFallbackAdapter { get; set; }

    /// <summary>
    /// A window the adapter must be able to present to, or null (the default). When set, the window's surface is
    /// created first and the adapter request asks for one compatible with it; pass the SAME object to
    /// <see cref="GraphicsContext.CreateWindowSurface(NativeWindowSource)" /> to use that surface.
    /// </summary>
    public NativeWindowSource CompatibleWindow { get; set; }

    /// <summary>A label for the device, shown in native validation messages and debuggers. Default null.</summary>
    public string Label { get; set; }
}
