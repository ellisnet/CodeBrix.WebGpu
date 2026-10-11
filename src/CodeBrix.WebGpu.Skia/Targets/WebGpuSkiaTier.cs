namespace CodeBrix.WebGpu.Skia;

/// <summary>
/// How WebGPU pixels reach the Skia surface of a <see cref="WebGpuSkiaTarget" />. Higher values are faster;
/// <see cref="WebGpuSkiaTarget.Tier" /> reports the tier a target took and
/// <see cref="WebGpuSkiaTarget.TierReason" /> says why it could not take a better one.
/// </summary>
public enum WebGpuSkiaTier
{
    /// <summary>Nothing works: no device, no adapter, an unsupported format or size. See the reason.</summary>
    Unavailable = 0,

    /// <summary>
    /// No Skia surface at all: WebGPU renders into its own texture, read back on request with
    /// <see cref="WebGpuSkiaTarget.ReadImage" />, <see cref="WebGpuSkiaTarget.ReadBitmap" /> or
    /// <see cref="WebGpuSkiaTarget.ReadPixels" />.
    /// </summary>
    Headless = 1,

    /// <summary>
    /// WebGPU renders into its own texture; every frame it is copied into a GPU buffer, mapped, and drawn onto
    /// the surface from CPU memory. Used for raster surfaces, surfaces on another GPU device or on OpenGL, and
    /// canvases without a surface. Counted by <see cref="WebGpuSkiaTarget.ReadbackCount" />.
    /// </summary>
    Readback = 2,

    /// <summary>
    /// The surface lives on the same GPU device, but its own texture cannot be handed to WebGPU: WebGPU renders
    /// into its own texture, which Skia then draws onto the surface with one GPU-side draw. No CPU readback.
    /// </summary>
    GpuCopy = 3,

    /// <summary>
    /// WebGPU renders directly into the texture that backs the surface: nothing is copied at all.
    /// </summary>
    ZeroCopy = 4,
}
