using System;
using System.Collections.Generic;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics.Images;

/// <summary>
/// Generates mip levels on the GPU: each level is rendered from the one above with a full-screen triangle and a linear
/// clamp sampler (a 2 x 2 box filter for even sizes). sRGB textures filter in linear light because sampling decodes
/// and the attachment encodes. One pipeline per format, built lazily; owned by the context.
/// </summary>
internal sealed unsafe class MipmapGenerator
{
    private const string Wgsl = @"
@group(0) @binding(0) var source_texture: texture_2d<f32>;
@group(0) @binding(1) var source_sampler: sampler;

struct VertexOutput {
    @builtin(position) clip: vec4<f32>,
    @location(0) uv: vec2<f32>,
};

@vertex
fn vs_main(@builtin(vertex_index) index: u32) -> VertexOutput {
    let uv = vec2<f32>(f32((index << 1u) & 2u), f32(index & 2u));
    var output: VertexOutput;
    output.clip = vec4<f32>(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
    output.uv = uv;
    return output;
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    return textureSample(source_texture, source_sampler, input.uv);
}
";

    private readonly GraphicsContext context;
    private readonly Dictionary<TextureFormat, GpuRenderPipeline> pipelines = new Dictionary<TextureFormat, GpuRenderPipeline>();
    private GpuShader shader;
    private GpuBindGroupLayout layout;

    /// <summary>Creates the generator (GPU objects are created on first use).</summary>
    /// <param name="context">The context.</param>
    public MipmapGenerator(GraphicsContext context)
    {
        this.context = context;
    }

    /// <summary>Fills mip levels 1..n of every layer of a texture from level 0, in one submitted frame.</summary>
    /// <param name="texture">The texture.</param>
    public void Generate(GpuTexture texture)
    {
        GpuRenderPipeline pipeline = Pipeline(texture.Format);
        int layers = texture.DepthOrArrayLayers;
        int levels = texture.MipLevelCount;
        List<TextureView> views = new List<TextureView>((levels * layers) + 1);
        List<GpuBindGroup> groups = new List<GpuBindGroup>(levels * layers);
        try
        {
            using GraphicsFrame frame = context.BeginFrame();
            for (int layer = 0; layer < layers; layer++)
            {
                TextureView source = CreateView(texture, 0, layer, views);
                for (int level = 1; level < levels; level++)
                {
                    TextureView target = CreateView(texture, level, layer, views);
                    GpuBindGroup group = context.CreateBindGroup(layout, "mipmap source", BindingEntry.ForTextureView(0, source), BindingEntry.ForSampler(1, context.LinearClampSampler));
                    groups.Add(group);
                    RenderTargetView view = new RenderTargetView(target, texture.Format, Math.Max(1, texture.Width >> level), Math.Max(1, texture.Height >> level));
                    using (RenderPass pass = frame.BeginRenderPass(view, Color4.Transparent, null))
                    {
                        pass.SetPipeline(pipeline);
                        pass.SetBindGroup(0, group);
                        pass.Draw(3);
                    }

                    source = target;
                }
            }

            frame.Submit();
        }
        finally
        {
            foreach (GpuBindGroup group in groups) group.Dispose();
            foreach (TextureView view in views) Native.WebGpu.TextureViewRelease(view);
        }
    }

    private TextureView CreateView(GpuTexture texture, int level, int layer, List<TextureView> views)
    {
        TextureViewDescriptor descriptor = new TextureViewDescriptor
        {
            Format = texture.Format,
            Dimension = TextureViewDimension.Dimension2D,
            BaseMipLevel = (uint)level,
            MipLevelCount = 1,
            BaseArrayLayer = (uint)layer,
            ArrayLayerCount = 1,
            Aspect = TextureAspect.All,
        };
        context.BeginGuard();
        TextureView view = Native.WebGpu.TextureCreateView(texture.Handle, &descriptor);
        string error = context.EndGuard();
        if (error != null || view.IsNull)
        {
            if (!view.IsNull) Native.WebGpu.TextureViewRelease(view);
            throw new GraphicsException("A mip level view could not be created: " + (error ?? "wgpuTextureCreateView returned null."));
        }

        views.Add(view);
        return view;
    }

    private GpuRenderPipeline Pipeline(TextureFormat format)
    {
        if (pipelines.TryGetValue(format, out GpuRenderPipeline pipeline) && !pipeline.IsDisposed) return pipeline;
        if (shader == null || shader.IsDisposed) shader = context.CreateShader(Wgsl, "mipmap generator");
        if (layout == null || layout.IsDisposed) layout = context.CreateBindGroupLayout("mipmap source", BindingLayoutEntry.Texture(0, ShaderStage.Fragment), BindingLayoutEntry.Sampler(1, ShaderStage.Fragment));
        pipeline = context.CreateRenderPipeline(shader)
            .WithLabel("mipmap " + format)
            .WithColorTarget(format)
            .WithBindGroupLayouts(layout)
            .Build();
        pipelines[format] = pipeline;
        return pipeline;
    }
}
