using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics.Lighting;

/// <summary>
/// Builds <see cref="EnvironmentMap" />s on the GPU and owns the per-context BRDF LUT. Every pass is a full-screen
/// triangle into one face (array layer) and mip of an RGBA16Float six-layer texture:
/// <list type="number">
/// <item>radiance mip 0 from the panorama (equirectangular, sampled at the lod that matches the cube's texel size) or
/// from the analytic sky; then the radiance mip chain (box filter, <see cref="GraphicsContext.GenerateMipmaps" />);</item>
/// <item>irradiance: every texel of one small radiance mip, weighted by solid angle and cosine (exact for a constant
/// environment, no noise);</item>
/// <item>prefiltered specular: mip m = GGX roughness m / (levels - 1), filtered importance sampling (Hammersley points,
/// each sample read from the radiance mip matching its solid angle); mip 0 is the radiance itself.</item>
/// </list>
/// The BRDF LUT (128 x 128 RG16Float: scale and bias of F0 by N.V and roughness) is rendered once per context.
/// Owned by the context; GPU objects are created on first use.
/// </summary>
internal sealed unsafe class EnvironmentBaker
{
    /// <summary>The LUT's size.</summary>
    internal const int LutSize = 128;

    private const TextureFormat CubeFormat = TextureFormat.RGBA16Float;
    private const int SlotSize = 256;

    private readonly GraphicsContext context;
    private GpuBindGroupLayout equirectLayout;
    private GpuBindGroupLayout skyLayout;
    private GpuBindGroupLayout irradianceLayout;
    private GpuBindGroupLayout prefilterLayout;
    private GpuBindGroupLayout lutLayout;
    private GpuRenderPipeline equirectPipeline;
    private GpuRenderPipeline skyPipeline;
    private GpuRenderPipeline irradiancePipeline;
    private GpuRenderPipeline prefilterPipeline;
    private GpuSampler panoramaSampler;
    private GpuTexture brdfLut;

    public EnvironmentBaker(GraphicsContext context)
    {
        this.context = context;
    }

    /// <summary>The BRDF LUT (rendered on first use; owned by the context).</summary>
    public GpuTexture BrdfLut
    {
        get
        {
            if (brdfLut == null || brdfLut.IsDisposed) brdfLut = RenderLut();
            return brdfLut;
        }
    }

    /// <summary>The sizes an <see cref="EnvironmentOptions" /> resolves to on this device.</summary>
    internal readonly record struct Sizes(int Cube, int Irradiance, int IrradianceSource, int Prefiltered, int PrefilteredLevels, int Samples);

    /// <summary>Resolves (clamps and rounds) the options.</summary>
    /// <param name="options">The options.</param>
    /// <param name="maxDimension">The device's 2D texture limit (0 = unknown).</param>
    /// <returns>The sizes.</returns>
    internal static Sizes Resolve(EnvironmentOptions options, uint maxDimension)
    {
        int limit = maxDimension == 0 ? 4096 : (int)Math.Min(4096u, maxDimension);
        limit = 1 << BitOperations.Log2((uint)limit);
        int cube = Math.Min(PowerOfTwo(Math.Clamp(options.CubeSize, 16, 4096)), limit);
        int irradiance = PowerOfTwo(Math.Clamp(options.IrradianceSize, 4, 128));
        int source = Math.Min(PowerOfTwo(Math.Clamp(options.IrradianceSourceSize, 4, 64)), cube);
        int prefiltered = options.PrefilteredSize <= 0 ? Math.Clamp(cube / 2, 16, 256) : PowerOfTwo(Math.Clamp(options.PrefilteredSize, 16, 4096));
        prefiltered = Math.Min(prefiltered, cube);
        int levels = Math.Clamp(options.PrefilteredMipLevels, 2, 8);
        levels = Math.Min(levels, BitOperations.Log2((uint)prefiltered) - 1);
        int samples = Math.Clamp(options.SpecularSampleCount, 8, 4096);
        return new Sizes(cube, irradiance, source, prefiltered, Math.Max(levels, 2), samples);
    }

    /// <summary>Builds an environment from a panorama.</summary>
    /// <param name="image">The equirectangular image (linear).</param>
    /// <param name="options">The options.</param>
    /// <returns>The environment.</returns>
    public EnvironmentMap FromPanorama(FloatImage image, EnvironmentOptions options)
    {
        Sizes sizes = Resolve(options, context.Limits.MaxTextureDimension2D);
        int maxWidth = Math.Max(16, options.MaxSourceWidth);
        if (context.Limits.MaxTextureDimension2D != 0) maxWidth = Math.Min(maxWidth, (int)context.Limits.MaxTextureDimension2D);
        while (image.Width > maxWidth || image.Height > maxWidth) image = Halve(image);

        using GpuTexture panorama = context.CreateTexture2D(image.Width, image.Height, CubeFormat, TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.RenderAttachment, GpuTexture.FullMipChainLength(image.Width, image.Height), "environment panorama");
        panorama.WriteMipLevel(0, ToHalfRgba(image));
        if (panorama.MipLevelCount > 1) context.GenerateMipmaps(panorama);

        EnsureEquirect();
        float lod = MathF.Max(0f, MathF.Log2(image.Width / (4f * sizes.Cube)));
        return Build(sizes, options.Label ?? "environment", false, (frame, radiance, uniforms, slot, views, groups) =>
        {
            GpuBindGroup group = context.CreateBindGroup(equirectLayout, "environment panorama", BindingEntry.ForBuffer(0, uniforms, 0, (ulong)Unsafe.SizeOf<BakeUniforms>()), BindingEntry.ForTexture(1, panorama), BindingEntry.ForSampler(2, panoramaSampler));
            groups.Add(group);
            for (int face = 0; face < 6; face++)
            {
                BakeUniforms data = new BakeUniforms { Face = (uint)face, Size = (uint)sizes.Cube, A = new Vector4(lod, 0, 0, 0) };
                Draw(frame, equirectPipeline, group, uniforms, slot++, in data, radiance, 0, face, sizes.Cube, views);
            }

            return slot;
        });
    }

    /// <summary>Builds the analytic sky.</summary>
    /// <param name="sky">The sky.</param>
    /// <param name="options">The options.</param>
    /// <returns>The environment.</returns>
    public EnvironmentMap FromSky(ProceduralSkyOptions sky, EnvironmentOptions options)
    {
        Sizes sizes = Resolve(options, context.Limits.MaxTextureDimension2D);
        EnsureSky();
        SkyUniforms skyData = PackSky(sky);
        using GpuBuffer skyBuffer = context.CreateBuffer((ulong)Unsafe.SizeOf<SkyUniforms>(), BufferUsage.Uniform | BufferUsage.CopyDst, "procedural sky");
        skyBuffer.WriteValue(in skyData);
        return Build(sizes, options.Label ?? "procedural sky", true, (frame, radiance, uniforms, slot, views, groups) =>
        {
            GpuBindGroup group = context.CreateBindGroup(skyLayout, "procedural sky", BindingEntry.ForBuffer(0, uniforms, 0, (ulong)Unsafe.SizeOf<BakeUniforms>()), BindingEntry.ForBuffer(1, skyBuffer));
            groups.Add(group);
            for (int face = 0; face < 6; face++)
            {
                BakeUniforms data = new BakeUniforms { Face = (uint)face, Size = (uint)sizes.Cube };
                Draw(frame, skyPipeline, group, uniforms, slot++, in data, radiance, 0, face, sizes.Cube, views);
            }

            return slot;
        });
    }

    /// <summary>Packs the sky options into the shader's uniforms.</summary>
    /// <param name="sky">The options.</param>
    /// <returns>The uniforms.</returns>
    internal static SkyUniforms PackSky(ProceduralSkyOptions sky)
    {
        float haze = Math.Clamp((sky.Turbidity - 1f) / 9f, 0f, 1f);
        Vector3 up = sky.Up.LengthSquared() > 0f ? Vector3.Normalize(sky.Up) : Vector3.UnitY;
        Vector3 toSun = sky.SunDirection.LengthSquared() > 0f ? -Vector3.Normalize(sky.SunDirection) : up;
        float radius = Math.Clamp(sky.SunAngularRadius, 0.002f, 0.2f);
        float solidAngle = 2f * MathF.PI * (1f - MathF.Cos(radius));
        float discRadiance = Math.Max(0f, sky.SunIntensity) * MathF.PI / solidAngle;
        Vector3 zenith = Vector3.Lerp(Rgb(sky.ZenithColor), Rgb(sky.HorizonColor), haze * 0.35f);
        float glow = Math.Max(0f, sky.SunGlow);
        return new SkyUniforms
        {
            Zenith = new Vector4(zenith, 0f),
            Horizon = new Vector4(Rgb(sky.HorizonColor), Lerp(8f, 1.6f, haze)),
            Ground = new Vector4(Rgb(sky.GroundColor), 0f),
            Sun = new Vector4(toSun, radius),
            SunColor = new Vector4(Rgb(sky.SunColor), discRadiance),
            Glow = new Vector4(glow * Lerp(6f, 2f, haze), Lerp(1200f, 150f, haze), glow * Lerp(0.25f, 0.8f, haze), Lerp(12f, 3f, haze)),
            Up = new Vector4(up, Math.Max(0f, sky.Intensity)),
        };
    }

    /// <summary>Converts a panorama to RGBA16Float texels (negative and NaN values become 0, large ones the half maximum).</summary>
    /// <param name="image">The image.</param>
    /// <returns>The texel bytes.</returns>
    internal static byte[] ToHalfRgba(FloatImage image)
    {
        int width = image.Width;
        byte[] bytes = new byte[width * image.Height * 8];
        float[] source = image.Pixels;
        Parallel.For(0, image.Height, row =>
        {
            Span<Half> target = MemoryMarshal.Cast<byte, Half>(bytes.AsSpan(row * width * 8, width * 8));
            int at = row * width * 3;
            for (int x = 0; x < width; x++)
            {
                target[x * 4] = ToHalf(source[at]);
                target[(x * 4) + 1] = ToHalf(source[at + 1]);
                target[(x * 4) + 2] = ToHalf(source[at + 2]);
                target[(x * 4) + 3] = Half.One;
                at += 3;
            }
        });
        return bytes;
    }

    /// <summary>Halves an image (2 x 2 box filter; an odd last row or column is folded into the previous one).</summary>
    /// <param name="image">The image.</param>
    /// <returns>The smaller image.</returns>
    internal static FloatImage Halve(FloatImage image)
    {
        int width = Math.Max(1, image.Width / 2);
        int height = Math.Max(1, image.Height / 2);
        float[] target = new float[width * height * 3];
        float[] source = image.Pixels;
        int sourceWidth = image.Width;
        int sourceHeight = image.Height;
        Parallel.For(0, height, y =>
        {
            int y0 = Math.Min(y * 2, sourceHeight - 1);
            int y1 = Math.Min((y * 2) + 1, sourceHeight - 1);
            for (int x = 0; x < width; x++)
            {
                int x0 = Math.Min(x * 2, sourceWidth - 1);
                int x1 = Math.Min((x * 2) + 1, sourceWidth - 1);
                for (int c = 0; c < 3; c++)
                {
                    target[(((y * width) + x) * 3) + c] = 0.25f * (source[(((y0 * sourceWidth) + x0) * 3) + c] + source[(((y0 * sourceWidth) + x1) * 3) + c] + source[(((y1 * sourceWidth) + x0) * 3) + c] + source[(((y1 * sourceWidth) + x1) * 3) + c]);
                }
            }
        });
        return new FloatImage(width, height, target);
    }

    private static Half ToHalf(float value)
    {
        if (!(value > 0f)) return Half.Zero;
        return (Half)Math.Min(value, 65504f);
    }

    private static Vector3 Rgb(Color4 color) => new Vector3(color.R, color.G, color.B);

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);

    private static int PowerOfTwo(int value) => (int)BitOperations.RoundUpToPowerOf2((uint)value);

    private delegate int FillRadiance(GraphicsFrame frame, GpuTexture radiance, GpuBuffer uniforms, int slot, List<TextureView> views, List<GpuBindGroup> groups);

    private EnvironmentMap Build(Sizes sizes, string label, bool procedural, FillRadiance fill)
    {
        EnsureFilters();
        int slots = 6 + 6 + (6 * sizes.PrefilteredLevels);
        GpuTexture radiance = null;
        GpuTexture irradiance = null;
        GpuTexture prefiltered = null;
        List<TextureView> views = new List<TextureView>();
        List<GpuBindGroup> groups = new List<GpuBindGroup>();
        GpuBuffer uniforms = null;
        try
        {
            TextureUsage usage = TextureUsage.TextureBinding | TextureUsage.RenderAttachment | TextureUsage.CopySrc;
            radiance = context.CreateTextureArray(sizes.Cube, sizes.Cube, 6, CubeFormat, usage, GpuTexture.FullMipChainLength(sizes.Cube, sizes.Cube), label + " radiance");
            irradiance = context.CreateTextureArray(sizes.Irradiance, sizes.Irradiance, 6, CubeFormat, usage, 1, label + " irradiance");
            prefiltered = context.CreateTextureArray(sizes.Prefiltered, sizes.Prefiltered, 6, CubeFormat, usage, sizes.PrefilteredLevels, label + " prefiltered");
            uniforms = context.CreateBuffer((ulong)(slots * SlotSize), BufferUsage.Uniform | BufferUsage.CopyDst, label + " bake");

            using (GraphicsFrame frame = context.BeginFrame())
            {
                fill(frame, radiance, uniforms, 0, views, groups);
                frame.Submit();
            }

            context.GenerateMipmaps(radiance);

            using (GraphicsFrame frame = context.BeginFrame())
            {
                int slot = 6;
                int sourceLevel = BitOperations.Log2((uint)sizes.Cube) - BitOperations.Log2((uint)sizes.IrradianceSource);
                TextureView arrayView = CreateView(radiance, TextureViewDimension.Dimension2DArray, 0, radiance.MipLevelCount, 0, 6, views);
                GpuBindGroup irradianceGroup = context.CreateBindGroup(irradianceLayout, "environment irradiance", BindingEntry.ForBuffer(0, uniforms, 0, (ulong)Unsafe.SizeOf<BakeUniforms>()), BindingEntry.ForTextureView(1, arrayView));
                groups.Add(irradianceGroup);
                for (int face = 0; face < 6; face++)
                {
                    BakeUniforms data = new BakeUniforms { Face = (uint)face, Size = (uint)sizes.Irradiance, Count = (uint)sizes.IrradianceSource, Level = (uint)sourceLevel };
                    Draw(frame, irradiancePipeline, irradianceGroup, uniforms, slot++, in data, irradiance, 0, face, sizes.Irradiance, views);
                }

                TextureView cubeView = CreateView(radiance, TextureViewDimension.Cube, 0, radiance.MipLevelCount, 0, 6, views);
                GpuBindGroup prefilterGroup = context.CreateBindGroup(prefilterLayout, "environment prefilter", BindingEntry.ForBuffer(0, uniforms, 0, (ulong)Unsafe.SizeOf<BakeUniforms>()), BindingEntry.ForTextureView(1, cubeView), BindingEntry.ForSampler(2, context.LinearClampSampler));
                groups.Add(prefilterGroup);
                float lastLevel = radiance.MipLevelCount - 1;
                float matchLevel = MathF.Log2(sizes.Cube / (float)sizes.Prefiltered);
                for (int level = 0; level < sizes.PrefilteredLevels; level++)
                {
                    int size = Math.Max(1, sizes.Prefiltered >> level);
                    float roughness = level / (float)(sizes.PrefilteredLevels - 1);
                    for (int face = 0; face < 6; face++)
                    {
                        BakeUniforms data = new BakeUniforms { Face = (uint)face, Size = (uint)size, Count = (uint)sizes.Samples, Level = (uint)level, A = new Vector4(roughness, sizes.Cube, lastLevel, matchLevel) };
                        Draw(frame, prefilterPipeline, prefilterGroup, uniforms, slot++, in data, prefiltered, level, face, size, views);
                    }
                }

                frame.Submit();
            }

            EnvironmentMap environment = new EnvironmentMap(context, label, radiance, irradiance, prefiltered, procedural);
            radiance = null;
            irradiance = null;
            prefiltered = null;
            return environment;
        }
        finally
        {
            foreach (GpuBindGroup group in groups) group.Dispose();
            foreach (TextureView view in views) Native.WebGpu.TextureViewRelease(view);
            uniforms?.Dispose();
            radiance?.Dispose();
            irradiance?.Dispose();
            prefiltered?.Dispose();
        }
    }

    private void Draw(GraphicsFrame frame, GpuRenderPipeline pipeline, GpuBindGroup group, GpuBuffer uniforms, int slot, in BakeUniforms data, GpuTexture target, int level, int face, int size, List<TextureView> views)
    {
        uniforms.WriteValue(in data, (ulong)(slot * SlotSize));
        TextureView view = CreateView(target, TextureViewDimension.Dimension2D, level, 1, face, 1, views);
        Span<uint> offset = stackalloc uint[1] { (uint)(slot * SlotSize) };
        using RenderPass pass = frame.BeginRenderPass(new RenderTargetView(view, target.Format, size, size), Color4.Transparent, null);
        pass.SetPipeline(pipeline);
        pass.SetBindGroup(0, group, offset);
        pass.Draw(3);
    }

    private GpuTexture RenderLut()
    {
        EnsureFilters();
        GpuTexture lut = context.CreateTexture2D(LutSize, LutSize, TextureFormat.RG16Float, TextureUsage.TextureBinding | TextureUsage.RenderAttachment | TextureUsage.CopySrc, 1, "BRDF LUT");
        try
        {
            using GpuShader shader = context.CreateShader(EnvironmentShaders.BrdfLut, "BRDF LUT");
            using GpuRenderPipeline pipeline = context.CreateRenderPipeline(shader).WithLabel("BRDF LUT").WithColorTarget(TextureFormat.RG16Float).WithBindGroupLayouts(lutLayout).Build();
            using GpuBuffer uniforms = context.CreateBuffer(SlotSize, BufferUsage.Uniform | BufferUsage.CopyDst, "BRDF LUT");
            BakeUniforms data = new BakeUniforms { Size = LutSize };
            uniforms.WriteValue(in data);
            using GpuBindGroup group = context.CreateBindGroup(lutLayout, "BRDF LUT", BindingEntry.ForBuffer(0, uniforms, 0, (ulong)Unsafe.SizeOf<BakeUniforms>()));
            using (GraphicsFrame frame = context.BeginFrame())
            {
                Span<uint> offset = stackalloc uint[1] { 0 };
                using (RenderPass pass = frame.BeginRenderPass(lut.AsRenderTarget(), Color4.Transparent, null))
                {
                    pass.SetPipeline(pipeline);
                    pass.SetBindGroup(0, group, offset);
                    pass.Draw(3);
                }

                frame.Submit();
            }
        }
        catch
        {
            lut.Dispose();
            throw;
        }

        return lut;
    }

    private void EnsureEquirect()
    {
        if (equirectPipeline != null && !equirectPipeline.IsDisposed) return;
        equirectLayout = context.CreateBindGroupLayout(
            "environment panorama",
            BindingLayoutEntry.Uniform(0, ShaderStage.Fragment, true, (ulong)Unsafe.SizeOf<BakeUniforms>()),
            BindingLayoutEntry.Texture(1, ShaderStage.Fragment),
            BindingLayoutEntry.Sampler(2, ShaderStage.Fragment));
        panoramaSampler = context.CreateSampler(new SamplerDescription { AddressModeU = AddressMode.Repeat, AddressModeV = AddressMode.ClampToEdge, Label = "environment panorama" });
        equirectPipeline = BuildPipeline(EnvironmentShaders.Equirect, "environment panorama", equirectLayout);
    }

    private void EnsureSky()
    {
        if (skyPipeline != null && !skyPipeline.IsDisposed) return;
        skyLayout = context.CreateBindGroupLayout(
            "procedural sky",
            BindingLayoutEntry.Uniform(0, ShaderStage.Fragment, true, (ulong)Unsafe.SizeOf<BakeUniforms>()),
            BindingLayoutEntry.Uniform(1, ShaderStage.Fragment, false, (ulong)Unsafe.SizeOf<SkyUniforms>()));
        skyPipeline = BuildPipeline(EnvironmentShaders.ProceduralSky, "procedural sky", skyLayout);
    }

    private void EnsureFilters()
    {
        if (prefilterPipeline != null && !prefilterPipeline.IsDisposed) return;
        irradianceLayout = context.CreateBindGroupLayout(
            "environment irradiance",
            BindingLayoutEntry.Uniform(0, ShaderStage.Fragment, true, (ulong)Unsafe.SizeOf<BakeUniforms>()),
            BindingLayoutEntry.Texture(1, ShaderStage.Fragment, TextureSampleType.UnfilterableFloat, TextureViewDimension.Dimension2DArray));
        prefilterLayout = context.CreateBindGroupLayout(
            "environment prefilter",
            BindingLayoutEntry.Uniform(0, ShaderStage.Fragment, true, (ulong)Unsafe.SizeOf<BakeUniforms>()),
            BindingLayoutEntry.Texture(1, ShaderStage.Fragment, TextureSampleType.Float, TextureViewDimension.Cube),
            BindingLayoutEntry.Sampler(2, ShaderStage.Fragment));
        lutLayout = context.CreateBindGroupLayout("BRDF LUT", BindingLayoutEntry.Uniform(0, ShaderStage.Fragment, true, (ulong)Unsafe.SizeOf<BakeUniforms>()));
        irradiancePipeline = BuildPipeline(EnvironmentShaders.Irradiance, "environment irradiance", irradianceLayout);
        prefilterPipeline = BuildPipeline(EnvironmentShaders.Prefilter, "environment prefilter", prefilterLayout);
    }

    private GpuRenderPipeline BuildPipeline(string wgsl, string label, GpuBindGroupLayout layout)
    {
        GpuShader shader = context.CreateShader(wgsl, label);
        return context.CreateRenderPipeline(shader).WithLabel(label).WithColorTarget(CubeFormat).WithBindGroupLayouts(layout).Build();
    }

    private TextureView CreateView(GpuTexture texture, TextureViewDimension dimension, int baseLevel, int levels, int baseLayer, int layers, List<TextureView> views)
    {
        TextureViewDescriptor descriptor = new TextureViewDescriptor
        {
            Format = texture.Format,
            Dimension = dimension,
            BaseMipLevel = (uint)baseLevel,
            MipLevelCount = (uint)levels,
            BaseArrayLayer = (uint)baseLayer,
            ArrayLayerCount = (uint)layers,
            Aspect = TextureAspect.All,
        };
        context.BeginGuard();
        TextureView view = Native.WebGpu.TextureCreateView(texture.Handle, &descriptor);
        string error = context.EndGuard();
        if (error != null || view.IsNull)
        {
            if (!view.IsNull) Native.WebGpu.TextureViewRelease(view);
            throw new GraphicsException("An environment view could not be created: " + (error ?? "wgpuTextureCreateView returned null."));
        }

        views.Add(view);
        return view;
    }
}

/// <summary>The parameters of one bake pass (80 bytes, one 256-byte dynamic-offset slot each).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BakeUniforms
{
    public uint Face;
    public uint Count;
    public uint Level;
    public uint Size;
    public Vector4 A;
    public Vector4 B;
    public Vector4 C;
    public Vector4 D;
}

/// <summary>The procedural sky's parameters (112 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SkyUniforms
{
    public Vector4 Zenith;
    public Vector4 Horizon;
    public Vector4 Ground;
    public Vector4 Sun;
    public Vector4 SunColor;
    public Vector4 Glow;
    public Vector4 Up;
}
