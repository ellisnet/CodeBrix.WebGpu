using System;
using System.Text;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Skia.Tests;

/// <summary>
/// The WebGPU commands the tests record into a target's encoder: a clearing render pass, and a solid-colour
/// triangle pass that LOADS the existing content and draws only inside a scissor rectangle (so it proves the
/// pixels Skia drew before it survived).
/// </summary>
internal sealed unsafe class WebGpuWork : IDisposable
{
    private const string SolidColorWgsl =
        "struct Paint { color: vec4<f32> };\n"
        + "@group(0) @binding(0) var<uniform> paint: Paint;\n"
        + "@vertex fn vs(@builtin(vertex_index) index: u32) -> @builtin(position) vec4<f32> {\n"
        + "    var corners = array<vec2<f32>, 3>(vec2<f32>(-1.0, -1.0), vec2<f32>(3.0, -1.0), vec2<f32>(-1.0, 3.0));\n"
        + "    return vec4<f32>(corners[index], 0.0, 1.0);\n"
        + "}\n"
        + "@fragment fn fs() -> @location(0) vec4<f32> { return paint.color; }\n";

    private readonly Device device;
    private readonly Queue queue;
    private readonly ShaderModule module;
    private readonly RenderPipeline pipeline;
    private readonly BindGroupLayout layout;
    private readonly Native.Buffer uniform;
    private readonly BindGroup group;

    /// <summary>Builds the solid-colour pipeline for one format.</summary>
    /// <param name="device">The device.</param>
    /// <param name="format">The target format.</param>
    public WebGpuWork(WebGpuSkiaDevice device, TextureFormat format)
    {
        this.device = device.Device;
        queue = device.Queue;
        byte[] code = Encoding.UTF8.GetBytes(SolidColorWgsl);
        fixed (byte* text = code)
        {
            ShaderSourceWGSL wgsl = new ShaderSourceWGSL { Code = WgpuString.FromPinned(text, code.Length) };
            wgsl.Chain.SType = ShaderSourceWGSL.StructType;
            ShaderModuleDescriptor descriptor = new ShaderModuleDescriptor { NextInChain = &wgsl.Chain };
            module = Native.WebGpu.DeviceCreateShaderModule(this.device, &descriptor);
        }

        byte[] vs = "vs"u8.ToArray();
        byte[] fs = "fs"u8.ToArray();
        fixed (byte* vsName = vs)
        fixed (byte* fsName = fs)
        {
            ColorTargetState target = new ColorTargetState { Format = format, WriteMask = ColorWriteMask.All };
            FragmentState fragment = new FragmentState
            {
                Module = module,
                EntryPoint = WgpuString.FromPinned(fsName, fs.Length),
                TargetCount = 1,
                Targets = &target,
            };
            RenderPipelineDescriptor descriptor = new RenderPipelineDescriptor
            {
                Vertex = new VertexState { Module = module, EntryPoint = WgpuString.FromPinned(vsName, vs.Length) },
                Primitive = new PrimitiveState { Topology = PrimitiveTopology.TriangleList, FrontFace = FrontFace.CCW, CullMode = CullMode.None },
                Multisample = new MultisampleState { Count = 1, Mask = 0xFFFFFFFF },
                Fragment = &fragment,
            };
            pipeline = Native.WebGpu.DeviceCreateRenderPipeline(this.device, &descriptor);
        }

        layout = Native.WebGpu.RenderPipelineGetBindGroupLayout(pipeline, 0);
        BufferDescriptor bufferDescriptor = new BufferDescriptor { Usage = BufferUsage.Uniform | BufferUsage.CopyDst, Size = 16 };
        uniform = Native.WebGpu.DeviceCreateBuffer(this.device, &bufferDescriptor);
        BindGroupEntry entry = new BindGroupEntry { Binding = 0, Buffer = uniform, Offset = 0, Size = 16 };
        BindGroupDescriptor groupDescriptor = new BindGroupDescriptor { Layout = layout, EntryCount = 1, Entries = &entry };
        group = Native.WebGpu.DeviceCreateBindGroup(this.device, &groupDescriptor);
    }

    /// <summary>Records a render pass that clears the whole view.</summary>
    /// <param name="encoder">The target's encoder.</param>
    /// <param name="view">The target's view.</param>
    /// <param name="r">Red, 0..1.</param>
    /// <param name="g">Green, 0..1.</param>
    /// <param name="b">Blue, 0..1.</param>
    public static void Clear(CommandEncoder encoder, TextureView view, double r, double g, double b)
    {
        RenderPassColorAttachment attachment = new RenderPassColorAttachment
        {
            View = view,
            DepthSlice = Constants.DepthSliceUndefined,
            LoadOp = LoadOp.Clear,
            StoreOp = StoreOp.Store,
            ClearValue = new Color { R = r, G = g, B = b, A = 1.0 },
        };
        RenderPassDescriptor descriptor = new RenderPassDescriptor { ColorAttachmentCount = 1, ColorAttachments = &attachment };
        RenderPassEncoder pass = Native.WebGpu.CommandEncoderBeginRenderPass(encoder, &descriptor);
        Native.WebGpu.RenderPassEncoderEnd(pass);
        Native.WebGpu.RenderPassEncoderRelease(pass);
    }

    /// <summary>
    /// Records a render pass that keeps the view's content and fills only the scissor rectangle with an opaque colour.
    /// The colour goes in through a queue write, which runs before the encoder's commands at the next submit.
    /// </summary>
    /// <param name="encoder">The target's encoder.</param>
    /// <param name="view">The target's view.</param>
    /// <param name="x">Scissor left.</param>
    /// <param name="y">Scissor top.</param>
    /// <param name="width">Scissor width.</param>
    /// <param name="height">Scissor height.</param>
    /// <param name="r">Red, 0..1.</param>
    /// <param name="g">Green, 0..1.</param>
    /// <param name="b">Blue, 0..1.</param>
    public void FillRectangle(CommandEncoder encoder, TextureView view, uint x, uint y, uint width, uint height, float r, float g, float b)
    {
        float* color = stackalloc float[4] { r, g, b, 1.0f };
        Native.WebGpu.QueueWriteBuffer(queue, uniform, 0, color, 16);
        RenderPassColorAttachment attachment = new RenderPassColorAttachment
        {
            View = view,
            DepthSlice = Constants.DepthSliceUndefined,
            LoadOp = LoadOp.Load,
            StoreOp = StoreOp.Store,
        };
        RenderPassDescriptor descriptor = new RenderPassDescriptor { ColorAttachmentCount = 1, ColorAttachments = &attachment };
        RenderPassEncoder pass = Native.WebGpu.CommandEncoderBeginRenderPass(encoder, &descriptor);
        Native.WebGpu.RenderPassEncoderSetPipeline(pass, pipeline);
        Native.WebGpu.RenderPassEncoderSetBindGroup(pass, 0, group, 0, null);
        Native.WebGpu.RenderPassEncoderSetScissorRect(pass, x, y, width, height);
        Native.WebGpu.RenderPassEncoderDraw(pass, 3, 1, 0, 0);
        Native.WebGpu.RenderPassEncoderEnd(pass);
        Native.WebGpu.RenderPassEncoderRelease(pass);
    }

    /// <summary>Releases the pipeline objects.</summary>
    public void Dispose()
    {
        Native.WebGpu.BindGroupRelease(group);
        Native.WebGpu.BufferRelease(uniform);
        Native.WebGpu.BindGroupLayoutRelease(layout);
        Native.WebGpu.RenderPipelineRelease(pipeline);
        Native.WebGpu.ShaderModuleRelease(module);
    }
}
