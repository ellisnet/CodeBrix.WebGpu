namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// How an <see cref="EnvironmentMap" /> is built: the sizes of its three cube maps and the sample counts of the
/// filtering passes. The defaults suit a first-class look at a modest build cost; every value is clamped to a sensible
/// range when the environment is built.
/// </summary>
public sealed class EnvironmentOptions
{
    /// <summary>The defaults (a fresh instance each time).</summary>
    public static EnvironmentOptions Default => new EnvironmentOptions();

    /// <summary>
    /// The face size of the radiance cube (the environment itself, drawn by the <see cref="SkyboxRenderer" /> and the
    /// source of the filtered maps), rounded up to a power of two, 16 to 4096 (and the device's limit). Default 512 (a
    /// 2048 x 1024 panorama maps about one to one).
    /// </summary>
    public int CubeSize { get; set; } = 512;

    /// <summary>The face size of the diffuse irradiance cube, a power of two from 4 to 128. Default 32 (irradiance is smooth).</summary>
    public int IrradianceSize { get; set; } = 32;

    /// <summary>
    /// The face size of the radiance mip the irradiance pass integrates (every texel of it, weighted by solid angle and
    /// the cosine), a power of two from 4 to 64. Default 32 (6 144 source texels per output texel; exact for a constant
    /// environment).
    /// </summary>
    public int IrradianceSourceSize { get; set; } = 32;

    /// <summary>
    /// The face size of mip 0 of the GGX-prefiltered specular cube (mip 0 = roughness 0), a power of two from 16 to the
    /// cube size, or 0 for half the cube size (at most 256). Default 0.
    /// </summary>
    public int PrefilteredSize { get; set; }

    /// <summary>
    /// The mip levels of the prefiltered cube; mip m holds roughness m / (levels - 1). 2 to 8, and the smallest mip is
    /// never below 4 x 4. Default 6.
    /// </summary>
    public int PrefilteredMipLevels { get; set; } = 6;

    /// <summary>
    /// GGX importance samples per texel of each rough prefiltered mip (filtered importance sampling: each sample reads a
    /// radiance mip matched to its solid angle, so few samples give smooth results), 8 to 4096. Default 128.
    /// </summary>
    public int SpecularSampleCount { get; set; } = 128;

    /// <summary>
    /// The widest panorama the GPU receives: wider equirectangular sources are halved on the CPU until they fit (and
    /// never beyond the device's 2D texture limit). Default 8192.
    /// </summary>
    public int MaxSourceWidth { get; set; } = 8192;

    /// <summary>A label for the GPU objects, or null.</summary>
    public string Label { get; set; }
}
