using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CodeBrix.WebGpu.Graphics.Lighting;
using CodeBrix.WebGpu.Graphics.Models;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>The curve a <see cref="ToneMapper" /> compresses high-dynamic-range colour with.</summary>
public enum ToneMapOperator
{
    /// <summary>Stephen Hill's fit of the ACES reference rendering + sRGB output transforms: filmic contrast, highlights roll off to white, slight hue shifts. The default.</summary>
    AcesFitted = 0,

    /// <summary>Khronos PBR Neutral: colours up to about 0.8 pass through unchanged (base colours stay true), highlights compress and desaturate. Best for product and model viewing.</summary>
    KhronosPbrNeutral = 1,

    /// <summary>Reinhard, c / (1 + c) per channel: cheap and flat; never reaches white.</summary>
    Reinhard = 2,

    /// <summary>Krzysztof Narkowicz's single-curve ACES approximation (per channel, more saturated than <see cref="AcesFitted" />).</summary>
    AcesNarkowicz = 3,

    /// <summary>No compression: values are clamped to 0..1 (inspect data, or content that is already display-referred).</summary>
    Clamp = 4,
}

/// <summary>
/// The tone-map pass of the HDR rendering path: reads a linear high-dynamic-range texture (for example an RGBA16Float
/// <see cref="HeadlessRenderTarget" /> the scene was drawn into), multiplies by <see cref="Exposure" />, applies
/// <see cref="Operator" /> and writes display values into the final target (hardware sRGB encoding for sRGB targets,
/// shader encoding for 8/10-bit UNORM targets, as the model renderer does). <see cref="Map" /> is the same curve on the
/// CPU.
/// </summary>
/// <remarks>
/// <para>
/// When to use it: draw models and the skybox straight into the window or an sRGB target (the default LDR path) when
/// the lighting stays within about 0..1 - simple scenes, the default sky, UI-like content. Render into an RGBA16Float
/// target and tone map when the scene has a real dynamic range: HDRI environments with bright windows or a sun, strong
/// lights, emissive surfaces, or when you want filmic highlight roll-off instead of hard clipping. Draw 2D overlays
/// (sprite batch, text) AFTER tone mapping, into the final target.
/// </para>
/// <para>Several applications per frame keep their own uniforms; per-call cost is one bind-group lookup and one queue write. Not thread-safe.</para>
/// </remarks>
public sealed class ToneMapper : GraphicsResource
{
    private readonly Dictionary<(TextureFormat Color, TextureFormat Depth), GpuRenderPipeline> pipelines = new Dictionary<(TextureFormat, TextureFormat), GpuRenderPipeline>();
    private readonly Dictionary<GpuTexture, GpuBindGroup> groups = new Dictionary<GpuTexture, GpuBindGroup>();
    private readonly List<GpuTexture> stale = new List<GpuTexture>();
    private readonly GpuShader shader;
    private readonly GpuBindGroupLayout layout;
    private readonly int slotSize;
    private GpuBuffer uniforms;
    private int capacity;
    private int slot;
    private long frameId = -1;

    /// <summary>Creates a tone mapper.</summary>
    /// <param name="context">The context.</param>
    public ToneMapper(GraphicsContext context)
        : base(context, "tone mapper")
    {
        shader = context.CreateShader(EnvironmentShaders.ToneMap, "tone map");
        layout = context.CreateBindGroupLayout(
            "tone map",
            BindingLayoutEntry.Uniform(0, ShaderStage.Fragment, true, (ulong)Unsafe.SizeOf<ToneMapUniforms>()),
            BindingLayoutEntry.Texture(1, ShaderStage.Fragment),
            BindingLayoutEntry.Sampler(2, ShaderStage.Fragment));
        slotSize = Math.Max(256, (int)context.Limits.MinUniformBufferOffsetAlignment);
        CreateUniforms(4);
    }

    /// <summary>The curve. Default <see cref="ToneMapOperator.AcesFitted" />.</summary>
    public ToneMapOperator Operator { get; set; } = ToneMapOperator.AcesFitted;

    /// <summary>Multiplies the linear input before the curve (2 = one stop brighter). Default 1.</summary>
    public float Exposure { get; set; } = 1f;

    /// <summary>
    /// Tone maps <paramref name="source" /> into <paramref name="destination" /> in a render pass of its own (every
    /// destination pixel is written; the destination's depth view, if any, is not used).
    /// </summary>
    /// <param name="frame">The frame.</param>
    /// <param name="source">The linear HDR texture (a filterable float format such as RGBA16Float, or any sampled colour texture; scaled to the destination size with linear filtering when the sizes differ).</param>
    /// <param name="destination">The final target.</param>
    public void Apply(GraphicsFrame frame, GpuTexture source, RenderTargetView destination)
    {
        ArgumentNullException.ThrowIfNull(frame);
        RenderTargetView colorOnly = new RenderTargetView(destination.ColorView, destination.ColorFormat, destination.Width, destination.Height);
        using RenderPass pass = frame.BeginRenderPass(colorOnly, Color4.Black, null);
        Apply(pass, source);
    }

    /// <summary>Tone maps <paramref name="source" /> over the whole target of a pass you began (for example before drawing a HUD in the same pass).</summary>
    /// <param name="renderPass">The pass (its depth, if any, is neither tested nor written).</param>
    /// <param name="source">The linear HDR texture; it must not be the pass's own target.</param>
    /// <exception cref="InvalidOperationException">The pass has ended.</exception>
    public void Apply(RenderPass renderPass, GpuTexture source)
    {
        ArgumentNullException.ThrowIfNull(renderPass);
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();
        source.ThrowIfDisposed();
        if (renderPass.IsEnded) throw new InvalidOperationException("The render pass has ended.");
        if (renderPass.Frame.Id != frameId)
        {
            frameId = renderPass.Frame.Id;
            slot = 0;
        }

        if (slot >= capacity) CreateUniforms(capacity * 2);
        RenderTargetView target = renderPass.Target;
        ToneMapUniforms data = new ToneMapUniforms
        {
            Params = new Vector4(Exposure, (float)Operator, PbrModelRenderPath.NeedsShaderEncoding(target.ColorFormat) ? 1f : 0f, 0f),
            Source = new Vector4(source.Width, source.Height, 1f / target.Width, 1f / target.Height),
        };
        uniforms.WriteValue(in data, (ulong)(slot * slotSize));
        Span<uint> offset = stackalloc uint[1] { (uint)(slot * slotSize) };
        slot++;
        renderPass.SetPipeline(Pipeline(target.ColorFormat, target.DepthFormat));
        renderPass.SetBindGroup(0, Group(source), offset);
        renderPass.Draw(3);
    }

    /// <summary>
    /// The same curve on the CPU: exposure, then the operator, giving linear display values 0..1 (encode them to sRGB
    /// for an 8-bit image). Negative input is treated as 0.
    /// </summary>
    /// <param name="linear">The linear HDR colour.</param>
    /// <param name="op">The operator.</param>
    /// <param name="exposure">The exposure multiplier.</param>
    /// <returns>The tone-mapped linear colour, each channel 0..1.</returns>
    public static Vector3 Map(Vector3 linear, ToneMapOperator op, float exposure = 1f)
    {
        Vector3 x = Vector3.Max(linear * exposure, Vector3.Zero);
        Vector3 mapped;
        switch (op)
        {
            case ToneMapOperator.AcesFitted:
            {
                Vector3 v = new Vector3(
                    (0.59719f * x.X) + (0.35458f * x.Y) + (0.04823f * x.Z),
                    (0.07600f * x.X) + (0.90834f * x.Y) + (0.01566f * x.Z),
                    (0.02840f * x.X) + (0.13383f * x.Y) + (0.83777f * x.Z));
                Vector3 a = (v * (v + new Vector3(0.0245786f))) - new Vector3(0.000090537f);
                Vector3 b = (v * ((0.983729f * v) + new Vector3(0.4329510f))) + new Vector3(0.238081f);
                Vector3 f = a / b;
                mapped = new Vector3(
                    (1.60475f * f.X) - (0.53108f * f.Y) - (0.07367f * f.Z),
                    (-0.10208f * f.X) + (1.10813f * f.Y) - (0.00605f * f.Z),
                    (-0.00327f * f.X) - (0.07276f * f.Y) + (1.07602f * f.Z));
                break;
            }

            case ToneMapOperator.KhronosPbrNeutral:
            {
                const float StartCompression = 0.8f - 0.04f;
                const float Desaturation = 0.15f;
                float low = MathF.Min(x.X, MathF.Min(x.Y, x.Z));
                float offset = low < 0.08f ? low - (6.25f * low * low) : 0.04f;
                Vector3 color = x - new Vector3(offset);
                float peak = MathF.Max(color.X, MathF.Max(color.Y, color.Z));
                if (peak < StartCompression)
                {
                    mapped = color;
                    break;
                }

                const float D = 1f - StartCompression;
                float newPeak = 1f - (D * D / (peak + D - StartCompression));
                color *= newPeak / peak;
                float g = 1f - (1f / ((Desaturation * (peak - newPeak)) + 1f));
                mapped = Vector3.Lerp(color, new Vector3(newPeak), g);
                break;
            }

            case ToneMapOperator.Reinhard:
                mapped = x / (Vector3.One + x);
                break;
            case ToneMapOperator.AcesNarkowicz:
                mapped = x * ((2.51f * x) + new Vector3(0.03f)) / ((x * ((2.43f * x) + new Vector3(0.59f))) + new Vector3(0.14f));
                break;
            case ToneMapOperator.Clamp:
                mapped = x;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(op), op, "Unknown tone-map operator.");
        }

        return Vector3.Clamp(mapped, Vector3.Zero, Vector3.One);
    }

    private protected override void ReleaseNative()
    {
        foreach (GpuBindGroup group in groups.Values) group.Dispose();
        groups.Clear();
        foreach (GpuRenderPipeline pipeline in pipelines.Values) pipeline.Dispose();
        pipelines.Clear();
        uniforms?.Dispose();
        layout.Dispose();
        shader.Dispose();
    }

    private void CreateUniforms(int slots)
    {
        foreach (GpuBindGroup group in groups.Values) group.Dispose();
        groups.Clear();
        uniforms?.Dispose();
        uniforms = Context.CreateBuffer((ulong)(slots * slotSize), BufferUsage.Uniform | BufferUsage.CopyDst, "tone map");
        capacity = slots;
        slot = 0;
    }

    private GpuBindGroup Group(GpuTexture source)
    {
        if (groups.TryGetValue(source, out GpuBindGroup group)) return group;
        foreach (GpuTexture key in groups.Keys)
        {
            if (key.IsDisposed) stale.Add(key);
        }

        foreach (GpuTexture key in stale)
        {
            groups.Remove(key, out GpuBindGroup old);
            old.Dispose();
        }

        stale.Clear();
        group = Context.CreateBindGroup(
            layout,
            "tone map",
            BindingEntry.ForBuffer(0, uniforms, 0, (ulong)Unsafe.SizeOf<ToneMapUniforms>()),
            BindingEntry.ForTexture(1, source),
            BindingEntry.ForSampler(2, Context.LinearClampSampler));
        groups[source] = group;
        return group;
    }

    private GpuRenderPipeline Pipeline(TextureFormat color, TextureFormat depth)
    {
        if (pipelines.TryGetValue((color, depth), out GpuRenderPipeline pipeline)) return pipeline;
        RenderPipelineBuilder builder = Context.CreateRenderPipeline(shader)
            .WithLabel("tone map " + color)
            .WithColorTarget(color)
            .WithBindGroupLayouts(layout);
        if (depth != TextureFormat.Undefined) builder.WithDepth(depth, DepthPreset.None);
        pipeline = builder.Build();
        pipelines[(color, depth)] = pipeline;
        return pipeline;
    }
}

/// <summary>The tone-map uniforms (32 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ToneMapUniforms
{
    public Vector4 Params;
    public Vector4 Source;
}
