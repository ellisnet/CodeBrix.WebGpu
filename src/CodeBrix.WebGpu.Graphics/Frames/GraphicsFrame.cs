using System;
using CodeBrix.WebGpu.Native;
using Buffer = CodeBrix.WebGpu.Native.Buffer;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// One command encoder's worth of work: render and compute passes recorded in order, then
/// <see cref="Submit" />. Created by <see cref="GraphicsContext.BeginFrame()" /> (the frame owns its encoder and
/// submits it) or <see cref="GraphicsContext.BeginFrame(CommandEncoder)" /> (someone else's encoder, for example a
/// <c>WebGpuSkiaTarget</c>'s; <see cref="Submit" /> then only closes the frame). Every frame has a unique
/// <see cref="Id" />; the sprite and mesh renderers use it to keep the data of different draws in one frame apart.
/// </summary>
public sealed unsafe class GraphicsFrame : IDisposable
{
    private RenderPass openRenderPass;
    private ComputePass openComputePass;

    internal GraphicsFrame(GraphicsContext context, CommandEncoder encoder, bool ownsEncoder)
    {
        Context = context;
        Encoder = encoder;
        OwnsEncoder = ownsEncoder;
        Id = context.NextFrameId();
    }

    /// <summary>The context.</summary>
    public GraphicsContext Context { get; }

    /// <summary>The command encoder passes are recorded into.</summary>
    public CommandEncoder Encoder { get; private set; }

    /// <summary>True when the frame created the encoder and submits it.</summary>
    public bool OwnsEncoder { get; }

    /// <summary>The frame's unique id.</summary>
    public long Id { get; }

    /// <summary>True once <see cref="Submit" /> has run.</summary>
    public bool IsSubmitted { get; private set; }

    /// <summary>
    /// Begins a render pass into a target. Pass a <paramref name="clearColor" /> to clear the colour attachment, or null
    /// to keep (load) its content; the depth attachment (if the target has one) is cleared to
    /// <paramref name="clearDepth" />, or loaded when that is null.
    /// </summary>
    /// <param name="target">The target.</param>
    /// <param name="clearColor">The clear colour, or null to load.</param>
    /// <param name="clearDepth">The depth clear value (default 1, the far plane), or null to load.</param>
    /// <returns>The pass; dispose it (or call <see cref="RenderPass.End" />) before the next pass or <see cref="Submit" />.</returns>
    /// <exception cref="InvalidOperationException">Another pass is open or the frame was submitted.</exception>
    public RenderPass BeginRenderPass(RenderTargetView target, Color4? clearColor, float? clearDepth = 1f)
    {
        ThrowIfClosed();
        if (target.ColorView.IsNull) throw new ArgumentException("The target has no colour view.", nameof(target));
        Color4 clear = clearColor ?? Color4.Transparent;
        RenderPassColorAttachment color = new RenderPassColorAttachment
        {
            View = target.ColorView,
            DepthSlice = Constants.DepthSliceUndefined,
            LoadOp = clearColor.HasValue ? LoadOp.Clear : LoadOp.Load,
            StoreOp = StoreOp.Store,
            ClearValue = new Color { R = clear.R, G = clear.G, B = clear.B, A = clear.A },
        };
        RenderPassDepthStencilAttachment depth = new RenderPassDepthStencilAttachment
        {
            View = target.DepthView,
            DepthLoadOp = clearDepth.HasValue ? LoadOp.Clear : LoadOp.Load,
            DepthStoreOp = StoreOp.Store,
            DepthClearValue = clearDepth ?? 1f,
        };
        bool hasStencil = target.DepthFormat == TextureFormat.Depth24PlusStencil8 || target.DepthFormat == TextureFormat.Depth32FloatStencil8;
        if (hasStencil)
        {
            depth.StencilLoadOp = LoadOp.Clear;
            depth.StencilStoreOp = StoreOp.Store;
        }

        RenderPassDescriptor descriptor = new RenderPassDescriptor
        {
            ColorAttachmentCount = 1,
            ColorAttachments = &color,
            DepthStencilAttachment = target.HasDepth ? &depth : null,
        };
        RenderPassEncoder encoder = Native.WebGpu.CommandEncoderBeginRenderPass(Encoder, &descriptor);
        openRenderPass = new RenderPass(this, encoder, target);
        return openRenderPass;
    }

    /// <summary>Begins a compute pass.</summary>
    /// <returns>The pass; dispose it (or call <see cref="ComputePass.End" />) before the next pass or <see cref="Submit" />.</returns>
    /// <exception cref="InvalidOperationException">Another pass is open or the frame was submitted.</exception>
    public ComputePass BeginComputePass()
    {
        ThrowIfClosed();
        ComputePassEncoder encoder = Native.WebGpu.CommandEncoderBeginComputePass(Encoder, null);
        openComputePass = new ComputePass(this, encoder);
        return openComputePass;
    }

    /// <summary>Records a buffer-to-buffer copy.</summary>
    /// <param name="source">The source.</param>
    /// <param name="sourceOffset">The source byte offset (multiple of 4).</param>
    /// <param name="destination">The destination.</param>
    /// <param name="destinationOffset">The destination byte offset (multiple of 4).</param>
    /// <param name="size">The bytes to copy (multiple of 4).</param>
    public void CopyBuffer(GpuBuffer source, ulong sourceOffset, GpuBuffer destination, ulong destinationOffset, ulong size)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ThrowIfClosed();
        Native.WebGpu.CommandEncoderCopyBufferToBuffer(Encoder, source.Handle, sourceOffset, destination.Handle, destinationOffset, size);
    }

    /// <summary>
    /// Finishes the frame: an owned encoder is finished and submitted to the queue; a borrowed one is left for its owner.
    /// Then raises the context's pending error and device-lost events.
    /// </summary>
    /// <exception cref="InvalidOperationException">A pass is still open, or the frame was already submitted.</exception>
    public void Submit()
    {
        ThrowIfClosed();
        IsSubmitted = true;
        if (OwnsEncoder)
        {
            CommandBuffer commands = Native.WebGpu.CommandEncoderFinish(Encoder, null);
            Native.WebGpu.QueueSubmit(Context.Queue, 1, &commands);
            Native.WebGpu.CommandBufferRelease(commands);
            Native.WebGpu.CommandEncoderRelease(Encoder);
        }

        Encoder = CommandEncoder.Null;
        Context.DispatchEvents();
    }

    /// <summary>Abandons an unsubmitted owned encoder (its commands never run). Safe after <see cref="Submit" />.</summary>
    public void Dispose()
    {
        openRenderPass?.End();
        openComputePass?.End();
        if (!IsSubmitted && OwnsEncoder && !Encoder.IsNull) Native.WebGpu.CommandEncoderRelease(Encoder);
        Encoder = CommandEncoder.Null;
        IsSubmitted = true;
    }

    /// <summary>Clears the open-pass marker when a pass ends.</summary>
    /// <param name="pass">The pass.</param>
    internal void PassEnded(object pass)
    {
        if (ReferenceEquals(pass, openRenderPass)) openRenderPass = null;
        if (ReferenceEquals(pass, openComputePass)) openComputePass = null;
    }

    private void ThrowIfClosed()
    {
        if (IsSubmitted) throw new InvalidOperationException("The frame has been submitted.");
        if (openRenderPass != null || openComputePass != null) throw new InvalidOperationException("A pass is still open; end it first.");
    }
}
