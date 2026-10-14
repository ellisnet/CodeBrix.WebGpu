using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CodeBrix.WebGpu.Graphics.Lighting;
using CodeBrix.WebGpu.Graphics.Models;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// Draws an <see cref="EnvironmentMap" /> as the background: one full-screen triangle at the far plane whose pixels
/// look the environment up along the camera's view rays (from the inverse view-projection matrix), turned, scaled and
/// exposed like the model lighting. Optional: a scene can just as well clear to a colour.
/// </summary>
/// <remarks>
/// <para>
/// Draw it in the same pass as the scene, after the opaque models (cheapest: only uncovered pixels are shaded) or
/// before them; it tests depth less-or-equal against the far plane and never writes depth, so the pass must clear
/// depth to 1 (the default). Without a depth view it covers the whole target, so draw it first.
/// </para>
/// <para>
/// Colour output matches <see cref="ModelRenderer" />: linear values into float targets (the HDR path, tone mapped
/// later by <see cref="ToneMapper" />), hardware encoding into sRGB targets, shader encoding into 8/10-bit UNORM
/// targets. Several draws per frame keep their own uniforms; nothing is allocated per draw once the environment and the
/// target formats have been seen. Not thread-safe.
/// </para>
/// </remarks>
public sealed class SkyboxRenderer : GraphicsResource
{
    private readonly Dictionary<(TextureFormat Color, TextureFormat Depth), GpuRenderPipeline> pipelines = new Dictionary<(TextureFormat, TextureFormat), GpuRenderPipeline>();
    private readonly Dictionary<EnvironmentMap, GpuBindGroup> groups = new Dictionary<EnvironmentMap, GpuBindGroup>();
    private readonly List<EnvironmentMap> stale = new List<EnvironmentMap>();
    private readonly GpuShader shader;
    private readonly GpuBindGroupLayout layout;
    private readonly int slotSize;
    private GpuBuffer uniforms;
    private int capacity;
    private int slot;
    private long frameId = -1;
    private float blur;

    /// <summary>Creates a skybox renderer (pipelines are built per target format combination on first use).</summary>
    /// <param name="context">The context.</param>
    public SkyboxRenderer(GraphicsContext context)
        : base(context, "skybox renderer")
    {
        shader = context.CreateShader(EnvironmentShaders.Skybox, "skybox");
        layout = context.CreateBindGroupLayout(
            "skybox",
            BindingLayoutEntry.Uniform(0, ShaderStage.Fragment, true, (ulong)Unsafe.SizeOf<SkyboxUniforms>()),
            BindingLayoutEntry.Texture(1, ShaderStage.Fragment, TextureSampleType.Float, TextureViewDimension.Cube),
            BindingLayoutEntry.Texture(2, ShaderStage.Fragment, TextureSampleType.Float, TextureViewDimension.Cube),
            BindingLayoutEntry.Sampler(3, ShaderStage.Fragment));
        slotSize = Math.Max(256, (int)context.Limits.MinUniformBufferOffsetAlignment);
        CreateUniforms(8);
    }

    /// <summary>
    /// Blurs the background, 0 (sharp, the radiance cube) to 1 (the roughest prefiltered mip); in between the
    /// GGX-prefiltered cube at that roughness (a soft, out-of-focus backdrop). Default 0.
    /// </summary>
    public float Blur
    {
        get => blur;
        set => blur = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>How many skybox draws the renderer has recorded (all frames).</summary>
    public long DrawCount { get; private set; }

    /// <summary>
    /// Draws the lighting's environment (<see cref="SceneLighting.Environment" />, or the context's
    /// <see cref="GraphicsContext.DefaultEnvironment" />) with its intensity, rotation and exposure - the same look the
    /// model renderer lights with.
    /// </summary>
    /// <param name="renderPass">The pass.</param>
    /// <param name="camera">The camera.</param>
    /// <param name="lighting">The lighting, or null for the defaults.</param>
    public void Draw(RenderPass renderPass, Camera camera, SceneLighting lighting = null)
    {
        EnvironmentMap environment = lighting?.Environment ?? Context.DefaultEnvironment;
        if (lighting == null)
        {
            Draw(renderPass, camera, environment);
            return;
        }

        Draw(renderPass, camera, environment, lighting.EnvironmentIntensity, lighting.EnvironmentRotation, lighting.Exposure);
    }

    /// <summary>Draws an environment as the background.</summary>
    /// <param name="renderPass">The pass.</param>
    /// <param name="camera">The camera (perspective or orthographic).</param>
    /// <param name="environment">The environment.</param>
    /// <param name="intensity">Scales the environment's radiance. Default 1.</param>
    /// <param name="rotation">Turns the environment about +Y, radians (as <see cref="SceneLighting.EnvironmentRotation" />). Default 0.</param>
    /// <param name="exposure">Scales the output (as <see cref="SceneLighting.Exposure" />). Default 1.</param>
    /// <exception cref="InvalidOperationException">The pass has ended.</exception>
    /// <exception cref="ObjectDisposedException">The environment has been disposed.</exception>
    public void Draw(RenderPass renderPass, Camera camera, EnvironmentMap environment, float intensity = 1f, float rotation = 0f, float exposure = 1f)
    {
        ArgumentNullException.ThrowIfNull(renderPass);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(environment);
        ThrowIfDisposed();
        environment.ThrowIfDisposed();
        if (renderPass.IsEnded) throw new InvalidOperationException("The render pass has ended.");
        if (renderPass.Frame.Id != frameId)
        {
            frameId = renderPass.Frame.Id;
            slot = 0;
        }

        if (slot >= capacity) CreateUniforms(capacity * 2);
        RenderTargetView target = renderPass.Target;
        Matrix4x4.Invert(camera.GetViewProjectionMatrix(target.AspectRatio), out Matrix4x4 inverse);
        float levels = environment.PrefilteredMipLevels - 1;
        SkyboxUniforms data = new SkyboxUniforms
        {
            InverseViewProjection = inverse,
            Params = new Vector4(intensity, rotation, exposure, PbrModelRenderPath.NeedsShaderEncoding(target.ColorFormat) ? 1f : 0f),
            Lods = new Vector4(0f, blur * levels, blur > 0f ? 1f : 0f, 0f),
        };
        uniforms.WriteValue(in data, (ulong)(slot * slotSize));
        Span<uint> offset = stackalloc uint[1] { (uint)(slot * slotSize) };
        slot++;
        renderPass.SetPipeline(Pipeline(target.ColorFormat, target.DepthFormat));
        renderPass.SetBindGroup(0, Group(environment), offset);
        renderPass.Draw(3);
        DrawCount++;
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
        uniforms = Context.CreateBuffer((ulong)(slots * slotSize), BufferUsage.Uniform | BufferUsage.CopyDst, "skybox");
        capacity = slots;
        slot = 0;
    }

    private GpuBindGroup Group(EnvironmentMap environment)
    {
        if (groups.TryGetValue(environment, out GpuBindGroup group)) return group;
        foreach (EnvironmentMap key in groups.Keys)
        {
            if (key.IsDisposed) stale.Add(key);
        }

        foreach (EnvironmentMap key in stale)
        {
            groups.Remove(key, out GpuBindGroup old);
            old.Dispose();
        }

        stale.Clear();
        group = Context.CreateBindGroup(
            layout,
            "skybox",
            BindingEntry.ForBuffer(0, uniforms, 0, (ulong)Unsafe.SizeOf<SkyboxUniforms>()),
            BindingEntry.ForTexture(1, environment.Radiance),
            BindingEntry.ForTexture(2, environment.Prefiltered),
            BindingEntry.ForSampler(3, Context.LinearClampSampler));
        groups[environment] = group;
        return group;
    }

    private GpuRenderPipeline Pipeline(TextureFormat color, TextureFormat depth)
    {
        if (pipelines.TryGetValue((color, depth), out GpuRenderPipeline pipeline)) return pipeline;
        RenderPipelineBuilder builder = Context.CreateRenderPipeline(shader)
            .WithLabel("skybox " + color)
            .WithColorTarget(color)
            .WithBindGroupLayouts(layout);
        if (depth != TextureFormat.Undefined) builder.WithDepth(depth, DepthPreset.ReadOnly);
        pipeline = builder.Build();
        pipelines[(color, depth)] = pipeline;
        return pipeline;
    }
}

/// <summary>The skybox uniforms (96 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SkyboxUniforms
{
    public Matrix4x4 InverseViewProjection;
    public Vector4 Params;
    public Vector4 Lods;
}
