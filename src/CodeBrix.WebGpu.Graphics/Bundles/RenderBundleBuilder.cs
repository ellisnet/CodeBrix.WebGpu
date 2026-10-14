using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CodeBrix.WebGpu.Graphics.Interop;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// Records a <see cref="GpuRenderBundle" /> with the same calls a <see cref="RenderPass" /> has (pipeline, bind
/// groups, vertex and index buffers, draws, indirect draws, immediates). Start one with
/// <see cref="GraphicsContext.CreateRenderBundle(RenderTargetView, int, string)" /> (or the pipeline / formats
/// overloads), record, then <see cref="Finish" />.
/// </summary>
/// <remarks>
/// The native engine ends the process when it finishes a bundle whose commands are invalid. <see cref="Finish" />
/// therefore first replays the commands into a throwaway render pass with the bundle's formats inside an error scope,
/// and throws a <see cref="GraphicsException" /> carrying WebGPU's validation message instead of finishing an invalid
/// bundle. Building a bundle is meant to happen once, not per frame. Debug groups and markers are accepted for
/// symmetry with passes, but this engine drops them inside bundles.
/// </remarks>
public sealed unsafe class RenderBundleBuilder : IDisposable
{
    private readonly List<Command> commands = new List<Command>();
    private readonly List<uint> dynamicOffsets = new List<uint>();
    private readonly List<byte> immediates = new List<byte>();
    private readonly TextureFormat[] colorFormats;
    private readonly string label;
    private RenderBundleEncoder wrappedEncoder;

    internal RenderBundleBuilder(GraphicsContext context, ReadOnlySpan<TextureFormat> colorFormats, TextureFormat depthFormat, int sampleCount, bool depthReadOnly, bool stencilReadOnly, string label, RenderBundleEncoder wrapped)
    {
        if (colorFormats.Length > 8) throw new ArgumentOutOfRangeException(nameof(colorFormats), "A render bundle has at most 8 colour formats.");
        if (sampleCount < 1 || sampleCount > 32 || (sampleCount & (sampleCount - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(sampleCount), "The sample count must be a power of two from 1 to 32.");
        if (depthFormat != TextureFormat.Undefined && !TextureFormats.IsDepth(depthFormat) && depthFormat != TextureFormat.Stencil8) throw new ArgumentException(depthFormat + " is not a depth or stencil format.", nameof(depthFormat));
        if (colorFormats.Length == 0 && depthFormat == TextureFormat.Undefined) throw new ArgumentException("A render bundle needs at least one colour format or a depth format.", nameof(colorFormats));
        Context = context;
        this.colorFormats = colorFormats.ToArray();
        DepthFormat = depthFormat;
        SampleCount = sampleCount;
        DepthReadOnly = depthReadOnly;
        StencilReadOnly = stencilReadOnly;
        this.label = label;
        wrappedEncoder = wrapped;
        if (!wrapped.IsNull) Native.WebGpu.RenderBundleEncoderAddRef(wrapped);
    }

    private enum CommandKind
    {
        SetPipeline,
        SetBindGroup,
        SetVertexBuffer,
        SetIndexBuffer,
        Draw,
        DrawIndexed,
        DrawIndirect,
        DrawIndexedIndirect,
        SetImmediates,
        PushDebugGroup,
        PopDebugGroup,
        InsertDebugMarker,
    }

    /// <summary>The context.</summary>
    public GraphicsContext Context { get; }

    /// <summary>The colour formats of the passes the bundle will run in.</summary>
    public IReadOnlyList<TextureFormat> ColorFormats => colorFormats;

    /// <summary>The depth format, or <see cref="TextureFormat.Undefined" />.</summary>
    public TextureFormat DepthFormat { get; }

    /// <summary>The sample count.</summary>
    public int SampleCount { get; }

    /// <summary>True when the bundle never writes depth (its pipelines must not either).</summary>
    public bool DepthReadOnly { get; }

    /// <summary>True when the bundle never writes stencil.</summary>
    public bool StencilReadOnly { get; }

    /// <summary>True for a builder over an encoder someone else created (<see cref="GraphicsContext.WrapRenderBundleEncoder" />).</summary>
    public bool IsWrapped => !wrappedEncoder.IsNull;

    /// <summary>The number of commands recorded so far.</summary>
    public int CommandCount => commands.Count;

    /// <summary>True once <see cref="Finish" /> has run (or the builder was disposed).</summary>
    public bool IsFinished { get; private set; }

    /// <summary>Sets the pipeline (its target formats must match the bundle's).</summary>
    /// <param name="pipeline">The pipeline.</param>
    public void SetPipeline(GpuRenderPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        Record(new Command { Kind = CommandKind.SetPipeline, Resource = pipeline });
    }

    /// <summary>Binds a bind group to <c>@group(index)</c>.</summary>
    /// <param name="index">The group index.</param>
    /// <param name="group">The bind group.</param>
    public void SetBindGroup(uint index, GpuBindGroup group) => SetBindGroup(index, group, ReadOnlySpan<uint>.Empty);

    /// <summary>Binds a bind group with dynamic offsets.</summary>
    /// <param name="index">The group index.</param>
    /// <param name="group">The bind group.</param>
    /// <param name="offsets">The dynamic offsets.</param>
    public void SetBindGroup(uint index, GpuBindGroup group, ReadOnlySpan<uint> offsets)
    {
        ArgumentNullException.ThrowIfNull(group);
        ThrowIfFinished();
        int start = dynamicOffsets.Count;
        foreach (uint offset in offsets) dynamicOffsets.Add(offset);
        Record(new Command { Kind = CommandKind.SetBindGroup, Resource = group, A = index, PayloadStart = start, PayloadLength = offsets.Length });
    }

    /// <summary>Binds a vertex buffer to a slot.</summary>
    /// <param name="slot">The slot.</param>
    /// <param name="buffer">The buffer.</param>
    /// <param name="offset">The byte offset.</param>
    /// <param name="size">The byte size (default: the rest of the buffer).</param>
    public void SetVertexBuffer(uint slot, GpuBuffer buffer, ulong offset = 0, ulong size = Constants.WholeSize)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Record(new Command { Kind = CommandKind.SetVertexBuffer, Resource = buffer, A = slot, Offset = offset, Size = size });
    }

    /// <summary>Binds the index buffer.</summary>
    /// <param name="buffer">The buffer.</param>
    /// <param name="format">16- or 32-bit indices.</param>
    /// <param name="offset">The byte offset.</param>
    /// <param name="size">The byte size (default: the rest of the buffer).</param>
    public void SetIndexBuffer(GpuBuffer buffer, IndexFormat format, ulong offset = 0, ulong size = Constants.WholeSize)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (format != IndexFormat.Uint16 && format != IndexFormat.Uint32) throw new ArgumentOutOfRangeException(nameof(format), "Indices are Uint16 or Uint32.");
        Record(new Command { Kind = CommandKind.SetIndexBuffer, Resource = buffer, Format = format, Offset = offset, Size = size });
    }

    /// <summary>Draws non-indexed vertices.</summary>
    /// <param name="vertexCount">The vertex count.</param>
    /// <param name="instanceCount">The instance count.</param>
    /// <param name="firstVertex">The first vertex.</param>
    /// <param name="firstInstance">The first instance.</param>
    public void Draw(uint vertexCount, uint instanceCount = 1, uint firstVertex = 0, uint firstInstance = 0) =>
        Record(new Command { Kind = CommandKind.Draw, A = vertexCount, B = instanceCount, C = firstVertex, D = firstInstance });

    /// <summary>Draws indexed vertices.</summary>
    /// <param name="indexCount">The index count.</param>
    /// <param name="instanceCount">The instance count.</param>
    /// <param name="firstIndex">The first index.</param>
    /// <param name="baseVertex">Added to every index.</param>
    /// <param name="firstInstance">The first instance.</param>
    public void DrawIndexed(uint indexCount, uint instanceCount = 1, uint firstIndex = 0, int baseVertex = 0, uint firstInstance = 0) =>
        Record(new Command { Kind = CommandKind.DrawIndexed, A = indexCount, B = instanceCount, C = firstIndex, E = baseVertex, D = firstInstance });

    /// <summary>Draws with arguments read from a buffer (one <see cref="DrawIndirectArgs" />).</summary>
    /// <param name="indirectBuffer">The buffer (needs <see cref="BufferUsage.Indirect" />).</param>
    /// <param name="offset">The byte offset.</param>
    public void DrawIndirect(GpuBuffer indirectBuffer, ulong offset = 0)
    {
        IndirectChecks.Check(indirectBuffer, offset, DrawIndirectArgs.Size, 1);
        Record(new Command { Kind = CommandKind.DrawIndirect, Resource = indirectBuffer, Offset = offset });
    }

    /// <summary>Draws indexed vertices with arguments read from a buffer (one <see cref="DrawIndexedIndirectArgs" />).</summary>
    /// <param name="indirectBuffer">The buffer.</param>
    /// <param name="offset">The byte offset.</param>
    public void DrawIndexedIndirect(GpuBuffer indirectBuffer, ulong offset = 0)
    {
        IndirectChecks.Check(indirectBuffer, offset, DrawIndexedIndirectArgs.Size, 1);
        Record(new Command { Kind = CommandKind.DrawIndexedIndirect, Resource = indirectBuffer, Offset = offset });
    }

    /// <summary>Sets immediate data (push constants) for the following draws (see <see cref="RenderPass.SetImmediates(ReadOnlySpan{byte}, uint)" />).</summary>
    /// <param name="data">The bytes (multiple of 4).</param>
    /// <param name="offset">The byte offset in the immediate block (multiple of 4).</param>
    public void SetImmediates(ReadOnlySpan<byte> data, uint offset = 0)
    {
        ThrowIfFinished();
        IndirectChecks.CheckImmediates(offset, data.Length);
        int start = immediates.Count;
        foreach (byte value in data) immediates.Add(value);
        Record(new Command { Kind = CommandKind.SetImmediates, A = offset, PayloadStart = start, PayloadLength = data.Length });
    }

    /// <summary>Sets immediate data from one blittable struct.</summary>
    /// <typeparam name="T">A blittable struct whose size is a multiple of 4.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="offset">The byte offset in the immediate block.</param>
    public void SetImmediates<T>(in T value, uint offset = 0)
        where T : unmanaged
    {
        SetImmediates(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)), offset);
    }

    /// <summary>Opens a debug group (accepted for symmetry with passes; this engine drops debug labels inside bundles).</summary>
    /// <param name="groupLabel">The group name.</param>
    public void PushDebugGroup(string groupLabel)
    {
        ArgumentNullException.ThrowIfNull(groupLabel);
        Record(new Command { Kind = CommandKind.PushDebugGroup, Resource = groupLabel });
    }

    /// <summary>Closes a debug group (dropped by this engine inside bundles).</summary>
    public void PopDebugGroup() => Record(new Command { Kind = CommandKind.PopDebugGroup });

    /// <summary>Inserts a debug marker (dropped by this engine inside bundles).</summary>
    /// <param name="markerLabel">The marker text.</param>
    public void InsertDebugMarker(string markerLabel)
    {
        ArgumentNullException.ThrowIfNull(markerLabel);
        Record(new Command { Kind = CommandKind.InsertDebugMarker, Resource = markerLabel });
    }

    /// <summary>
    /// Validates the recorded commands (by replaying them into a throwaway render pass inside an error scope), then
    /// records and finishes the native bundle.
    /// </summary>
    /// <param name="bundleLabel">A label for the bundle, or null for the builder's.</param>
    /// <returns>The bundle.</returns>
    /// <exception cref="GraphicsException">The commands failed WebGPU validation (nothing is finished; the builder is closed).</exception>
    public GpuRenderBundle Finish(string bundleLabel = null)
    {
        ThrowIfFinished();
        Context.ThrowIfDisposed();
        IsFinished = true;
        bundleLabel ??= label;
        try
        {
            string error = Rehearse();
            if (error != null) throw new GraphicsException("The render bundle" + (bundleLabel == null ? string.Empty : " '" + bundleLabel + "'") + " failed validation: " + error);

            using Utf8Text text = new Utf8Text();
            RenderBundleEncoder encoder = wrappedEncoder;
            if (encoder.IsNull)
            {
                TextureFormat* formats = (TextureFormat*)text.Allocate(sizeof(TextureFormat) * Math.Max(colorFormats.Length, 1));
                for (int index = 0; index < colorFormats.Length; index++) formats[index] = colorFormats[index];
                RenderBundleEncoderDescriptor descriptor = new RenderBundleEncoderDescriptor
                {
                    Label = text.View(bundleLabel),
                    ColorFormatCount = (nuint)colorFormats.Length,
                    ColorFormats = colorFormats.Length == 0 ? null : formats,
                    DepthStencilFormat = DepthFormat,
                    SampleCount = (uint)SampleCount,
                    DepthReadOnly = DepthReadOnly ? 1u : 0u,
                    StencilReadOnly = StencilReadOnly ? 1u : 0u,
                };
                encoder = Native.WebGpu.DeviceCreateRenderBundleEncoder(Context.Device, &descriptor);
            }

            try
            {
                ReplayIntoBundle(encoder);
                RenderBundleDescriptor finish = new RenderBundleDescriptor { Label = text.View(bundleLabel) };
                RenderBundle bundle = Native.WebGpu.RenderBundleEncoderFinish(encoder, &finish);
                return new GpuRenderBundle(Context, bundle, colorFormats, DepthFormat, SampleCount, false, bundleLabel);
            }
            finally
            {
                Native.WebGpu.RenderBundleEncoderRelease(encoder);
                wrappedEncoder = RenderBundleEncoder.Null;
            }
        }
        finally
        {
            ReleaseWrapped();
        }
    }

    /// <summary>Abandons an unfinished builder.</summary>
    public void Dispose()
    {
        IsFinished = true;
        ReleaseWrapped();
    }

    private void ReleaseWrapped()
    {
        if (!wrappedEncoder.IsNull) Native.WebGpu.RenderBundleEncoderRelease(wrappedEncoder);
        wrappedEncoder = RenderBundleEncoder.Null;
    }

    private void Record(Command command)
    {
        ThrowIfFinished();
        if (command.Resource is GraphicsResource resource)
        {
            resource.ThrowIfDisposed();
            if (!ReferenceEquals(resource.Context, Context)) throw new ArgumentException("The " + resource.GetType().Name + " belongs to another context.");
        }

        commands.Add(command);
    }

    private void ThrowIfFinished()
    {
        if (IsFinished) throw new InvalidOperationException("The render bundle has been finished.");
    }

    // Replays the commands into a throwaway render pass whose attachments have the bundle's formats, inside an error
    // scope: render-pass validation is reported (not fatal), and it checks what bundle finishing would.
    private string Rehearse()
    {
        foreach (Command command in commands)
        {
            if (command.Resource is GraphicsResource resource) resource.ThrowIfDisposed();
        }

        List<Texture> textures = new List<Texture>();
        List<TextureView> views = new List<TextureView>();
        Context.BeginGuard();
        CommandEncoder encoder = CommandEncoder.Null;
        try
        {
            RenderPassColorAttachment* colors = stackalloc RenderPassColorAttachment[Math.Max(colorFormats.Length, 1)];
            for (int index = 0; index < colorFormats.Length; index++)
            {
                TextureView view = colorFormats[index] == TextureFormat.Undefined ? TextureView.Null : DummyView(colorFormats[index], textures, views);
                colors[index] = new RenderPassColorAttachment
                {
                    View = view,
                    DepthSlice = Constants.DepthSliceUndefined,
                    LoadOp = LoadOp.Clear,
                    StoreOp = StoreOp.Discard,
                };
            }

            RenderPassDepthStencilAttachment depth = default;
            if (DepthFormat != TextureFormat.Undefined)
            {
                depth.View = DummyView(DepthFormat, textures, views);
                bool hasDepth = DepthFormat != TextureFormat.Stencil8;
                bool hasStencil = DepthFormat == TextureFormat.Stencil8 || DepthFormat == TextureFormat.Depth24PlusStencil8 || DepthFormat == TextureFormat.Depth32FloatStencil8;
                if (hasDepth)
                {
                    if (DepthReadOnly) depth.DepthReadOnly = 1;
                    else
                    {
                        depth.DepthLoadOp = LoadOp.Clear;
                        depth.DepthStoreOp = StoreOp.Discard;
                        depth.DepthClearValue = 1f;
                    }
                }

                if (hasStencil)
                {
                    if (StencilReadOnly) depth.StencilReadOnly = 1;
                    else
                    {
                        depth.StencilLoadOp = LoadOp.Clear;
                        depth.StencilStoreOp = StoreOp.Discard;
                    }
                }
            }

            encoder = Native.WebGpu.DeviceCreateCommandEncoder(Context.Device, null);
            RenderPassDescriptor descriptor = new RenderPassDescriptor
            {
                ColorAttachmentCount = (nuint)colorFormats.Length,
                ColorAttachments = colorFormats.Length == 0 ? null : colors,
                DepthStencilAttachment = DepthFormat == TextureFormat.Undefined ? null : &depth,
            };
            RenderPassEncoder pass = Native.WebGpu.CommandEncoderBeginRenderPass(encoder, &descriptor);
            ReplayIntoPass(pass);
            Native.WebGpu.RenderPassEncoderEnd(pass);
            Native.WebGpu.RenderPassEncoderRelease(pass);
            CommandBuffer rehearsal = Native.WebGpu.CommandEncoderFinish(encoder, null);
            Native.WebGpu.CommandBufferRelease(rehearsal);
        }
        finally
        {
            if (!encoder.IsNull) Native.WebGpu.CommandEncoderRelease(encoder);
            foreach (TextureView view in views) Native.WebGpu.TextureViewRelease(view);
            foreach (Texture texture in textures) Native.WebGpu.TextureRelease(texture);
        }

        return Context.EndGuard();
    }

    private TextureView DummyView(TextureFormat format, List<Texture> textures, List<TextureView> views)
    {
        TextureDescriptor descriptor = new TextureDescriptor
        {
            Usage = TextureUsage.RenderAttachment,
            Dimension = TextureDimension.Dimension2D,
            Size = new Extent3D { Width = 1, Height = 1, DepthOrArrayLayers = 1 },
            Format = format,
            MipLevelCount = 1,
            SampleCount = (uint)SampleCount,
        };
        Texture texture = Native.WebGpu.DeviceCreateTexture(Context.Device, &descriptor);
        textures.Add(texture);
        TextureView view = Native.WebGpu.TextureCreateView(texture, null);
        views.Add(view);
        return view;
    }

    private void ReplayIntoPass(RenderPassEncoder pass)
    {
        Span<uint> offsets = dynamicOffsets.Count == 0 ? Span<uint>.Empty : CollectionsMarshal.AsSpan(dynamicOffsets);
        Span<byte> data = immediates.Count == 0 ? Span<byte>.Empty : CollectionsMarshal.AsSpan(immediates);
        fixed (uint* offsetBase = offsets)
        fixed (byte* dataBase = data)
        {
            foreach (Command command in commands)
            {
                switch (command.Kind)
                {
                    case CommandKind.SetPipeline:
                        Native.WebGpu.RenderPassEncoderSetPipeline(pass, ((GpuRenderPipeline)command.Resource).Handle);
                        break;
                    case CommandKind.SetBindGroup:
                        Native.WebGpu.RenderPassEncoderSetBindGroup(pass, command.A, ((GpuBindGroup)command.Resource).Handle, (nuint)command.PayloadLength, command.PayloadLength == 0 ? null : offsetBase + command.PayloadStart);
                        break;
                    case CommandKind.SetVertexBuffer:
                        Native.WebGpu.RenderPassEncoderSetVertexBuffer(pass, command.A, ((GpuBuffer)command.Resource).Handle, command.Offset, command.Size);
                        break;
                    case CommandKind.SetIndexBuffer:
                        Native.WebGpu.RenderPassEncoderSetIndexBuffer(pass, ((GpuBuffer)command.Resource).Handle, command.Format, command.Offset, command.Size);
                        break;
                    case CommandKind.Draw:
                        Native.WebGpu.RenderPassEncoderDraw(pass, command.A, command.B, command.C, command.D);
                        break;
                    case CommandKind.DrawIndexed:
                        Native.WebGpu.RenderPassEncoderDrawIndexed(pass, command.A, command.B, command.C, command.E, command.D);
                        break;
                    case CommandKind.DrawIndirect:
                        Native.WebGpu.RenderPassEncoderDrawIndirect(pass, ((GpuBuffer)command.Resource).Handle, command.Offset);
                        break;
                    case CommandKind.DrawIndexedIndirect:
                        Native.WebGpu.RenderPassEncoderDrawIndexedIndirect(pass, ((GpuBuffer)command.Resource).Handle, command.Offset);
                        break;
                    case CommandKind.SetImmediates:
                        Native.WebGpu.RenderPassEncoderSetImmediates(pass, command.A, dataBase + command.PayloadStart, (nuint)command.PayloadLength);
                        break;
                }
            }
        }
    }

    private void ReplayIntoBundle(RenderBundleEncoder encoder)
    {
        Span<uint> offsets = dynamicOffsets.Count == 0 ? Span<uint>.Empty : CollectionsMarshal.AsSpan(dynamicOffsets);
        Span<byte> data = immediates.Count == 0 ? Span<byte>.Empty : CollectionsMarshal.AsSpan(immediates);
        byte* scratch = stackalloc byte[NativeUtf8.StackBytes];
        fixed (uint* offsetBase = offsets)
        fixed (byte* dataBase = data)
        {
            foreach (Command command in commands)
            {
                switch (command.Kind)
                {
                    case CommandKind.SetPipeline:
                        Native.WebGpu.RenderBundleEncoderSetPipeline(encoder, ((GpuRenderPipeline)command.Resource).Handle);
                        break;
                    case CommandKind.SetBindGroup:
                        Native.WebGpu.RenderBundleEncoderSetBindGroup(encoder, command.A, ((GpuBindGroup)command.Resource).Handle, (nuint)command.PayloadLength, command.PayloadLength == 0 ? null : offsetBase + command.PayloadStart);
                        break;
                    case CommandKind.SetVertexBuffer:
                        Native.WebGpu.RenderBundleEncoderSetVertexBuffer(encoder, command.A, ((GpuBuffer)command.Resource).Handle, command.Offset, command.Size);
                        break;
                    case CommandKind.SetIndexBuffer:
                        Native.WebGpu.RenderBundleEncoderSetIndexBuffer(encoder, ((GpuBuffer)command.Resource).Handle, command.Format, command.Offset, command.Size);
                        break;
                    case CommandKind.Draw:
                        Native.WebGpu.RenderBundleEncoderDraw(encoder, command.A, command.B, command.C, command.D);
                        break;
                    case CommandKind.DrawIndexed:
                        Native.WebGpu.RenderBundleEncoderDrawIndexed(encoder, command.A, command.B, command.C, command.E, command.D);
                        break;
                    case CommandKind.DrawIndirect:
                        Native.WebGpu.RenderBundleEncoderDrawIndirect(encoder, ((GpuBuffer)command.Resource).Handle, command.Offset);
                        break;
                    case CommandKind.DrawIndexedIndirect:
                        Native.WebGpu.RenderBundleEncoderDrawIndexedIndirect(encoder, ((GpuBuffer)command.Resource).Handle, command.Offset);
                        break;
                    case CommandKind.SetImmediates:
                        Native.WebGpu.RenderBundleEncoderSetImmediates(encoder, command.A, dataBase + command.PayloadStart, (nuint)command.PayloadLength);
                        break;
                    case CommandKind.PushDebugGroup:
                        using (NativeUtf8 text = new NativeUtf8((string)command.Resource, scratch, NativeUtf8.StackBytes))
                        {
                            Native.WebGpu.RenderBundleEncoderPushDebugGroup(encoder, text.View);
                        }

                        break;
                    case CommandKind.PopDebugGroup:
                        Native.WebGpu.RenderBundleEncoderPopDebugGroup(encoder);
                        break;
                    case CommandKind.InsertDebugMarker:
                        using (NativeUtf8 text = new NativeUtf8((string)command.Resource, scratch, NativeUtf8.StackBytes))
                        {
                            Native.WebGpu.RenderBundleEncoderInsertDebugMarker(encoder, text.View);
                        }

                        break;
                }
            }
        }
    }

    private struct Command
    {
        public CommandKind Kind;
        public object Resource;
        public uint A;
        public uint B;
        public uint C;
        public uint D;
        public int E;
        public ulong Offset;
        public ulong Size;
        public IndexFormat Format;
        public int PayloadStart;
        public int PayloadLength;
    }
}
