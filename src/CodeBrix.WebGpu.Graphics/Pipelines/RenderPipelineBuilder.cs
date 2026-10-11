using System;
using System.Collections.Generic;
using System.Linq;
using CodeBrix.WebGpu.Graphics.Interop;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// Builds a <see cref="GpuRenderPipeline" /> with sensible defaults: entry points <c>vs_main</c> / <c>fs_main</c>,
/// triangle lists, counter-clockwise front faces, no culling, no depth, one sample, and an automatic layout derived
/// from the shader unless bind-group layouts are given. Start one with
/// <see cref="GraphicsContext.CreateRenderPipeline(GpuShader)" />; every method returns the builder.
/// </summary>
public sealed unsafe class RenderPipelineBuilder
{
    private readonly GraphicsContext context;
    private readonly List<VertexLayout> vertexBuffers = new List<VertexLayout>();
    private readonly List<(TextureFormat Format, BlendPreset Blend, ColorWriteMask Mask)> colorTargets = new List<(TextureFormat, BlendPreset, ColorWriteMask)>();
    private readonly List<GpuBindGroupLayout> layouts = new List<GpuBindGroupLayout>();
    private GpuShader vertexShader;
    private GpuShader fragmentShader;
    private string vertexEntry = "vs_main";
    private string fragmentEntry = "fs_main";
    private bool hasFragment = true;
    private PrimitiveTopology topology = PrimitiveTopology.TriangleList;
    private FrontFace frontFace = FrontFace.CCW;
    private CullPreset cull = CullPreset.None;
    private TextureFormat depthFormat = TextureFormat.Undefined;
    private DepthPreset depth = DepthPreset.None;
    private int depthBias;
    private float depthBiasSlopeScale;
    private uint sampleCount = 1;
    private string label;

    internal RenderPipelineBuilder(GraphicsContext context, GpuShader shader)
    {
        this.context = context;
        vertexShader = shader;
        fragmentShader = shader;
    }

    /// <summary>The vertex stage: the module (null keeps the current one) and entry point.</summary>
    /// <param name="entryPoint">The entry point.</param>
    /// <param name="shader">The module, or null for the builder's shader.</param>
    /// <returns>This builder.</returns>
    public RenderPipelineBuilder WithVertexEntry(string entryPoint, GpuShader shader = null)
    {
        vertexEntry = entryPoint ?? throw new ArgumentNullException(nameof(entryPoint));
        if (shader != null) vertexShader = shader;
        return this;
    }

    /// <summary>The fragment stage: the module (null keeps the current one) and entry point.</summary>
    /// <param name="entryPoint">The entry point.</param>
    /// <param name="shader">The module, or null for the builder's shader.</param>
    /// <returns>This builder.</returns>
    public RenderPipelineBuilder WithFragmentEntry(string entryPoint, GpuShader shader = null)
    {
        fragmentEntry = entryPoint ?? throw new ArgumentNullException(nameof(entryPoint));
        if (shader != null) fragmentShader = shader;
        hasFragment = true;
        return this;
    }

    /// <summary>Builds a pipeline with no fragment stage (depth-only passes).</summary>
    /// <returns>This builder.</returns>
    public RenderPipelineBuilder WithoutFragment()
    {
        hasFragment = false;
        return this;
    }

    /// <summary>Adds a vertex buffer layout (slot = the order of the calls).</summary>
    /// <param name="layout">The layout.</param>
    /// <returns>This builder.</returns>
    public RenderPipelineBuilder WithVertexBuffer(VertexLayout layout)
    {
        vertexBuffers.Add(layout ?? throw new ArgumentNullException(nameof(layout)));
        return this;
    }

    /// <summary>Adds a vertex buffer whose layout is derived from a struct (<see cref="VertexLayout.FromStruct{T}" />).</summary>
    /// <typeparam name="T">The vertex (or instance) struct.</typeparam>
    /// <param name="stepMode">Per vertex (the default) or per instance.</param>
    /// <param name="firstLocation">Added to every location.</param>
    /// <returns>This builder.</returns>
    public RenderPipelineBuilder WithVertexBuffer<T>(VertexStepMode stepMode = VertexStepMode.Vertex, int firstLocation = 0)
        where T : unmanaged => WithVertexBuffer(VertexLayout.FromStruct<T>(stepMode, firstLocation));

    /// <summary>Adds a colour target (attachment index = the order of the calls).</summary>
    /// <param name="format">The target's format.</param>
    /// <param name="blend">The blend preset (default opaque).</param>
    /// <param name="writeMask">The channels written (default all).</param>
    /// <returns>This builder.</returns>
    public RenderPipelineBuilder WithColorTarget(TextureFormat format, BlendPreset blend = BlendPreset.Opaque, ColorWriteMask writeMask = ColorWriteMask.All)
    {
        colorTargets.Add((format, blend, writeMask));
        return this;
    }

    /// <summary>Declares the pass's depth attachment and how this pipeline uses it.</summary>
    /// <param name="format">The depth format.</param>
    /// <param name="preset">The depth test (default <see cref="DepthPreset.ReadWrite" />).</param>
    /// <param name="bias">The constant depth bias.</param>
    /// <param name="slopeScale">The slope-scaled depth bias.</param>
    /// <returns>This builder.</returns>
    public RenderPipelineBuilder WithDepth(TextureFormat format, DepthPreset preset = DepthPreset.ReadWrite, int bias = 0, float slopeScale = 0)
    {
        depthFormat = format;
        depth = preset;
        depthBias = bias;
        depthBiasSlopeScale = slopeScale;
        return this;
    }

    /// <summary>The face culling (default none) and which winding is the front face (default counter-clockwise).</summary>
    /// <param name="preset">The culling.</param>
    /// <param name="front">The front face winding.</param>
    /// <returns>This builder.</returns>
    public RenderPipelineBuilder WithCulling(CullPreset preset, FrontFace front = FrontFace.CCW)
    {
        cull = preset;
        frontFace = front;
        return this;
    }

    /// <summary>The primitive topology (default triangle list).</summary>
    /// <param name="value">The topology.</param>
    /// <returns>This builder.</returns>
    public RenderPipelineBuilder WithTopology(PrimitiveTopology value)
    {
        topology = value;
        return this;
    }

    /// <summary>The sample count of the targets (default 1).</summary>
    /// <param name="count">1 or 4.</param>
    /// <returns>This builder.</returns>
    public RenderPipelineBuilder WithSampleCount(int count)
    {
        sampleCount = (uint)count;
        return this;
    }

    /// <summary>Explicit bind-group layouts for groups 0, 1, ... (instead of the automatic layout).</summary>
    /// <param name="bindGroupLayouts">The layouts.</param>
    /// <returns>This builder.</returns>
    public RenderPipelineBuilder WithBindGroupLayouts(params GpuBindGroupLayout[] bindGroupLayouts)
    {
        layouts.Clear();
        layouts.AddRange(bindGroupLayouts ?? Array.Empty<GpuBindGroupLayout>());
        return this;
    }

    /// <summary>A label for the pipeline.</summary>
    /// <param name="value">The label.</param>
    /// <returns>This builder.</returns>
    public RenderPipelineBuilder WithLabel(string value)
    {
        label = value;
        return this;
    }

    /// <summary>Creates the pipeline.</summary>
    /// <returns>The pipeline.</returns>
    /// <exception cref="InvalidOperationException">A fragment stage has no colour target.</exception>
    /// <exception cref="GraphicsException">WebGPU rejected the pipeline (the message carries its validation error).</exception>
    public GpuRenderPipeline Build()
    {
        context.ThrowIfDisposed();
        vertexShader.ThrowIfDisposed();
        if (hasFragment && colorTargets.Count == 0) throw new InvalidOperationException("A pipeline with a fragment stage needs at least one colour target (WithColorTarget).");

        using Utf8Text text = new Utf8Text();
        PipelineLayout pipelineLayout = CreateLayout(text);

        VertexBufferLayout* buffers = (VertexBufferLayout*)text.Allocate(sizeof(VertexBufferLayout) * vertexBuffers.Count);
        for (int slot = 0; slot < vertexBuffers.Count; slot++)
        {
            VertexLayout layout = vertexBuffers[slot];
            VertexAttribute* attributes = (VertexAttribute*)text.Allocate(sizeof(VertexAttribute) * layout.Elements.Count);
            for (int index = 0; index < layout.Elements.Count; index++)
            {
                VertexElement element = layout.Elements[index];
                attributes[index] = new VertexAttribute { Format = element.Format, Offset = element.Offset, ShaderLocation = (uint)element.Location };
            }

            buffers[slot] = new VertexBufferLayout { StepMode = layout.StepMode, ArrayStride = layout.ArrayStride, AttributeCount = (nuint)layout.Elements.Count, Attributes = attributes };
        }

        ColorTargetState* targets = (ColorTargetState*)text.Allocate(sizeof(ColorTargetState) * colorTargets.Count);
        for (int index = 0; index < colorTargets.Count; index++)
        {
            BlendState? blend = Presets.Blend(colorTargets[index].Blend);
            BlendState* blendState = null;
            if (blend.HasValue)
            {
                blendState = (BlendState*)text.Allocate(sizeof(BlendState));
                *blendState = blend.Value;
            }

            targets[index] = new ColorTargetState { Format = colorTargets[index].Format, Blend = blendState, WriteMask = colorTargets[index].Mask };
        }

        FragmentState fragment = default;
        if (hasFragment)
        {
            fragmentShader.ThrowIfDisposed();
            fragment = new FragmentState { Module = fragmentShader.Handle, EntryPoint = text.View(fragmentEntry), TargetCount = (nuint)colorTargets.Count, Targets = targets };
        }

        DepthStencilState depthState = default;
        if (depthFormat != TextureFormat.Undefined)
        {
            Presets.Depth(depth, out CompareFunction compare, out bool write);
            StencilFaceState stencil = new StencilFaceState { Compare = CompareFunction.Always, FailOp = StencilOperation.Keep, DepthFailOp = StencilOperation.Keep, PassOp = StencilOperation.Keep };
            depthState = new DepthStencilState
            {
                Format = depthFormat,
                DepthWriteEnabled = write ? OptionalBool.True : OptionalBool.False,
                DepthCompare = compare,
                StencilFront = stencil,
                StencilBack = stencil,
                StencilReadMask = 0xFFFFFFFF,
                StencilWriteMask = 0xFFFFFFFF,
                DepthBias = depthBias,
                DepthBiasSlopeScale = depthBiasSlopeScale,
            };
        }

        RenderPipelineDescriptor descriptor = new RenderPipelineDescriptor
        {
            Label = text.View(label),
            Layout = pipelineLayout,
            Vertex = new VertexState { Module = vertexShader.Handle, EntryPoint = text.View(vertexEntry), BufferCount = (nuint)vertexBuffers.Count, Buffers = buffers },
            Primitive = new PrimitiveState
            {
                Topology = topology,
                StripIndexFormat = IndexFormat.Undefined,
                FrontFace = frontFace,
                CullMode = Presets.Cull(cull),
            },
            DepthStencil = depthFormat != TextureFormat.Undefined ? &depthState : null,
            Multisample = new MultisampleState { Count = sampleCount, Mask = 0xFFFFFFFF },
            Fragment = hasFragment ? &fragment : null,
        };

        context.BeginGuard();
        RenderPipeline handle = Native.WebGpu.DeviceCreateRenderPipeline(context.Device, &descriptor);
        string error = context.EndGuard();
        if (error != null || handle.IsNull)
        {
            if (!handle.IsNull) Native.WebGpu.RenderPipelineRelease(handle);
            if (!pipelineLayout.IsNull) Native.WebGpu.PipelineLayoutRelease(pipelineLayout);
            throw new GraphicsException("The render pipeline" + (label == null ? string.Empty : " '" + label + "'") + " could not be created: " + (error ?? "wgpuDeviceCreateRenderPipeline returned null."));
        }

        return new GpuRenderPipeline(context, handle, pipelineLayout, colorTargets.Select(target => target.Format).ToArray(), depthFormat, label);
    }

    private PipelineLayout CreateLayout(Utf8Text text)
    {
        if (layouts.Count == 0) return PipelineLayout.Null;
        BindGroupLayout* handles = (BindGroupLayout*)text.Allocate(sizeof(BindGroupLayout) * layouts.Count);
        for (int index = 0; index < layouts.Count; index++)
        {
            layouts[index].ThrowIfDisposed();
            handles[index] = layouts[index].Handle;
        }

        PipelineLayoutDescriptor descriptor = new PipelineLayoutDescriptor { BindGroupLayoutCount = (nuint)layouts.Count, BindGroupLayouts = handles };
        return Native.WebGpu.DeviceCreatePipelineLayout(context.Device, &descriptor);
    }
}

/// <summary>
/// Builds a <see cref="GpuComputePipeline" />: entry point <c>main</c> by default and an automatic layout unless
/// bind-group layouts are given. Start one with <see cref="GraphicsContext.CreateComputePipeline(GpuShader)" />.
/// </summary>
public sealed unsafe class ComputePipelineBuilder
{
    private readonly GraphicsContext context;
    private readonly GpuShader shader;
    private readonly List<GpuBindGroupLayout> layouts = new List<GpuBindGroupLayout>();
    private string entry = "main";
    private string label;

    internal ComputePipelineBuilder(GraphicsContext context, GpuShader shader)
    {
        this.context = context;
        this.shader = shader;
    }

    /// <summary>The entry point (default <c>main</c>).</summary>
    /// <param name="entryPoint">The entry point.</param>
    /// <returns>This builder.</returns>
    public ComputePipelineBuilder WithEntry(string entryPoint)
    {
        entry = entryPoint ?? throw new ArgumentNullException(nameof(entryPoint));
        return this;
    }

    /// <summary>Explicit bind-group layouts for groups 0, 1, ... (instead of the automatic layout).</summary>
    /// <param name="bindGroupLayouts">The layouts.</param>
    /// <returns>This builder.</returns>
    public ComputePipelineBuilder WithBindGroupLayouts(params GpuBindGroupLayout[] bindGroupLayouts)
    {
        layouts.Clear();
        layouts.AddRange(bindGroupLayouts ?? Array.Empty<GpuBindGroupLayout>());
        return this;
    }

    /// <summary>A label for the pipeline.</summary>
    /// <param name="value">The label.</param>
    /// <returns>This builder.</returns>
    public ComputePipelineBuilder WithLabel(string value)
    {
        label = value;
        return this;
    }

    /// <summary>Creates the pipeline.</summary>
    /// <returns>The pipeline.</returns>
    /// <exception cref="GraphicsException">WebGPU rejected the pipeline.</exception>
    public GpuComputePipeline Build()
    {
        context.ThrowIfDisposed();
        shader.ThrowIfDisposed();
        using Utf8Text text = new Utf8Text();
        PipelineLayout pipelineLayout = PipelineLayout.Null;
        if (layouts.Count > 0)
        {
            BindGroupLayout* handles = (BindGroupLayout*)text.Allocate(sizeof(BindGroupLayout) * layouts.Count);
            for (int index = 0; index < layouts.Count; index++) handles[index] = layouts[index].Handle;
            PipelineLayoutDescriptor layoutDescriptor = new PipelineLayoutDescriptor { BindGroupLayoutCount = (nuint)layouts.Count, BindGroupLayouts = handles };
            pipelineLayout = Native.WebGpu.DeviceCreatePipelineLayout(context.Device, &layoutDescriptor);
        }

        ComputePipelineDescriptor descriptor = new ComputePipelineDescriptor
        {
            Label = text.View(label),
            Layout = pipelineLayout,
            Compute = new ComputeState { Module = shader.Handle, EntryPoint = text.View(entry) },
        };
        context.BeginGuard();
        ComputePipeline handle = Native.WebGpu.DeviceCreateComputePipeline(context.Device, &descriptor);
        string error = context.EndGuard();
        if (error != null || handle.IsNull)
        {
            if (!handle.IsNull) Native.WebGpu.ComputePipelineRelease(handle);
            if (!pipelineLayout.IsNull) Native.WebGpu.PipelineLayoutRelease(pipelineLayout);
            throw new GraphicsException("The compute pipeline" + (label == null ? string.Empty : " '" + label + "'") + " could not be created: " + (error ?? "wgpuDeviceCreateComputePipeline returned null."));
        }

        return new GpuComputePipeline(context, handle, pipelineLayout, label);
    }
}
