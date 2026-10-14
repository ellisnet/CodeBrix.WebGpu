namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// An environment for image-based lighting and sky backgrounds, built on the GPU from an HDR panorama
/// (<see cref="GraphicsContext.CreateEnvironment(string, EnvironmentOptions)" />) or an analytic sky
/// (<see cref="GraphicsContext.CreateProceduralSky" />): three RGBA16Float cube maps -
/// <see cref="Radiance" /> (the environment itself, full mip chain), <see cref="Irradiance" /> (diffuse irradiance / PI)
/// and <see cref="Prefiltered" /> (GGX-prefiltered specular radiance, roughness 0..1 over its mips). Use it through
/// <see cref="SceneLighting.Environment" /> (model lighting) and <see cref="SkyboxRenderer" /> (background), or bind the
/// cube views with <see cref="GraphicsContext.BrdfLut" /> in your own shaders. Immutable once built; dispose it when no
/// renderer uses it any more.
/// </summary>
/// <remarks>
/// Cube layout: layers +X, -X, +Y, -Y, +Z, -Z (WebGPU). A panorama maps with its horizontal centre towards -Z and its
/// top row straight up (+Y), the u coordinate growing towards +X; turn it about +Y at draw time with
/// <see cref="SceneLighting.EnvironmentRotation" /> or <see cref="SkyboxRenderer.Draw(RenderPass, Camera, EnvironmentMap, float, float, float)" />.
/// </remarks>
public sealed class EnvironmentMap : GraphicsResource
{
    private readonly GpuTexture radianceTexture;
    private readonly GpuTexture irradianceTexture;
    private readonly GpuTexture prefilteredTexture;

    internal EnvironmentMap(GraphicsContext context, string label, GpuTexture radianceTexture, GpuTexture irradianceTexture, GpuTexture prefilteredTexture, bool isProcedural)
        : base(context, label)
    {
        this.radianceTexture = radianceTexture;
        this.irradianceTexture = irradianceTexture;
        this.prefilteredTexture = prefilteredTexture;
        Radiance = radianceTexture.CreateCubeView(label == null ? null : label + " radiance cube");
        Irradiance = irradianceTexture.CreateCubeView(label == null ? null : label + " irradiance cube");
        Prefiltered = prefilteredTexture.CreateCubeView(label == null ? null : label + " prefiltered cube");
        IsProcedural = isProcedural;
    }

    /// <summary>The environment (RGBA16Float cube view, full mip chain; the textures are 6-layer arrays).</summary>
    public GpuTexture Radiance { get; }

    /// <summary>Diffuse irradiance divided by PI (RGBA16Float cube view, one mip): a white matte surface with normal n shows Irradiance(n).</summary>
    public GpuTexture Irradiance { get; }

    /// <summary>GGX-prefiltered specular radiance (RGBA16Float cube view); mip m holds roughness m / (<see cref="PrefilteredMipLevels" /> - 1).</summary>
    public GpuTexture Prefiltered { get; }

    /// <summary>The face size of <see cref="Radiance" />.</summary>
    public int Size => radianceTexture.Width;

    /// <summary>The face size of <see cref="Irradiance" />.</summary>
    public int IrradianceSize => irradianceTexture.Width;

    /// <summary>The face size of mip 0 of <see cref="Prefiltered" />.</summary>
    public int PrefilteredSize => prefilteredTexture.Width;

    /// <summary>The mip levels of <see cref="Prefiltered" />.</summary>
    public int PrefilteredMipLevels => prefilteredTexture.MipLevelCount;

    /// <summary>True for an environment made by <see cref="GraphicsContext.CreateProceduralSky" />.</summary>
    public bool IsProcedural { get; }

    /// <summary>The six-layer array texture behind <see cref="Radiance" /> (tests and readback).</summary>
    internal GpuTexture RadianceArray => radianceTexture;

    /// <summary>The six-layer array texture behind <see cref="Irradiance" />.</summary>
    internal GpuTexture IrradianceArray => irradianceTexture;

    /// <summary>The six-layer array texture behind <see cref="Prefiltered" />.</summary>
    internal GpuTexture PrefilteredArray => prefilteredTexture;

    private protected override void ReleaseNative()
    {
        Prefiltered.Dispose();
        Irradiance.Dispose();
        Radiance.Dispose();
        prefilteredTexture.Dispose();
        irradianceTexture.Dispose();
        radianceTexture.Dispose();
    }
}
