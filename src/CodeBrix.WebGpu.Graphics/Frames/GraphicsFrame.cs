using System;
using System.Collections.Generic;
using CodeBrix.WebGpu.Graphics.Interop;
using CodeBrix.WebGpu.Native;
using Buffer = CodeBrix.WebGpu.Native.Buffer;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// One command encoder's worth of work: render and compute passes recorded in order, then
/// <see cref="Submit" />. Created by <see cref="GraphicsContext.BeginFrame()" /> (the frame owns its encoder and
/// submits it) or <see cref="GraphicsContext.BeginFrame(CommandEncoder)" /> (someone else's encoder, for example a
/// <c>WebGpuSkiaTarget</c>'s; <see cref="Submit" /> then only closes the frame) or
/// <see cref="GraphicsContext.BeginFrame(IWebGpuTextureTarget, GpuTexture)" /> (a shared texture target's pass:
/// <see cref="Submit" /> ends that pass, which submits the commands). Every frame has a unique
/// <see cref="Id" />; the sprite and mesh renderers use it to keep the data of different draws in one frame apart.
/// </summary>
public sealed unsafe partial class GraphicsFrame : IDisposable
{
    private RenderPass openRenderPass;
    private ComputePass openComputePass;
    private List<GraphicsResource> transientResources;

    /// <summary>Creates a frame on a shared texture target's pass encoder.</summary>
    /// <param name="context">The context.</param>
    /// <param name="encoder">The encoder the target's pass handed out.</param>
    /// <param name="textureTarget">The target whose pass the frame ends.</param>
    /// <param name="target">The target's view (with depth, if any).</param>
    internal GraphicsFrame(GraphicsContext context, CommandEncoder encoder, IWebGpuTextureTarget textureTarget, RenderTargetView target)
        : this(context, encoder, false)
    {
        TextureTarget = textureTarget;
        Target = target;
    }

    internal GraphicsFrame(GraphicsContext context, CommandEncoder encoder, bool ownsEncoder)
    {
        Context = context;
        Encoder = encoder;
        OwnsEncoder = ownsEncoder;
        Id = context.NextFrameId();

        // A borrowed encoder gets this frame's own reference, released when the frame closes.
        if (!ownsEncoder) Native.WebGpu.CommandEncoderAddRef(encoder);
    }

    /// <summary>The context.</summary>
    public GraphicsContext Context { get; }

    /// <summary>The command encoder passes are recorded into.</summary>
    public CommandEncoder Encoder { get; private set; }

    /// <summary>True when the frame created the encoder and submits it.</summary>
    public bool OwnsEncoder { get; }

    /// <summary>
    /// The shared texture target whose WebGPU pass this frame records into
    /// (<see cref="GraphicsContext.BeginFrame(IWebGpuTextureTarget, GpuTexture)" />), or null.
    /// </summary>
    public IWebGpuTextureTarget TextureTarget { get; }

    /// <summary>
    /// The view of <see cref="TextureTarget" /> with the frame's depth texture attached - what
    /// <see cref="BeginRenderPass(Color4?, float?)" /> renders into. Default (no colour view) for a frame without a
    /// texture target.
    /// </summary>
    public RenderTargetView Target { get; }

    /// <summary>The frame's unique id.</summary>
    public long Id { get; }

    /// <summary>True once <see cref="Submit" /> (or <see cref="Finish" />) has run.</summary>
    public bool IsSubmitted { get; private set; }

    /// <summary>
    /// The queue submission index of this frame once <see cref="Submit" /> submitted it (0 before, and for a frame on
    /// a borrowed encoder). Wait for exactly this work with <see cref="GraphicsContext.WaitForSubmission" />.
    /// </summary>
    public ulong SubmissionIndex { get; private set; }

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
        RenderPassOptions options = default;
        return BeginRenderPass(target, clearColor, clearDepth, in options);
    }

    /// <summary>
    /// Begins a render pass into the frame's own <see cref="Target" /> (a frame started on a shared texture target).
    /// </summary>
    /// <param name="clearColor">The clear colour, or null to load.</param>
    /// <param name="clearDepth">The depth clear value (default 1, the far plane), or null to load.</param>
    /// <returns>The pass; dispose it (or call <see cref="RenderPass.End" />) before the next pass or <see cref="Submit" />.</returns>
    /// <exception cref="InvalidOperationException">The frame has no texture target, another pass is open or the frame was submitted.</exception>
    public RenderPass BeginRenderPass(Color4? clearColor, float? clearDepth = 1f)
    {
        RenderPassOptions options = default;
        return BeginRenderPass(clearColor, clearDepth, in options);
    }

    /// <summary>
    /// Begins a render pass into the frame's own <see cref="Target" />, with the less common settings too (see
    /// <see cref="RenderPassOptions" />).
    /// </summary>
    /// <param name="clearColor">The clear colour, or null to load.</param>
    /// <param name="clearDepth">The depth clear value, or null to load.</param>
    /// <param name="options">The settings.</param>
    /// <returns>The pass.</returns>
    /// <exception cref="InvalidOperationException">The frame has no texture target, another pass is open or the frame was submitted.</exception>
    public RenderPass BeginRenderPass(Color4? clearColor, float? clearDepth, in RenderPassOptions options)
    {
        if (TextureTarget == null) throw new InvalidOperationException("The frame has no texture target; pass a RenderTargetView (or start the frame with GraphicsContext.BeginFrame(IWebGpuTextureTarget, GpuTexture)).");
        return BeginRenderPass(Target, clearColor, clearDepth, in options);
    }

    /// <summary>
    /// Begins a render pass with the less common settings too: a label, timestamp writes, an occlusion query set,
    /// stencil clear / load, read-only depth or stencil, a multisample resolve target (see <see cref="RenderPassOptions" />).
    /// </summary>
    /// <param name="target">The target.</param>
    /// <param name="clearColor">The clear colour, or null to load.</param>
    /// <param name="clearDepth">The depth clear value, or null to load.</param>
    /// <param name="options">The settings.</param>
    /// <returns>The pass.</returns>
    /// <exception cref="InvalidOperationException">Another pass is open or the frame was submitted.</exception>
    public RenderPass BeginRenderPass(RenderTargetView target, Color4? clearColor, float? clearDepth, in RenderPassOptions options)
    {
        ThrowIfClosed();
        if (target.ColorView.IsNull) throw new ArgumentException("The target has no colour view.", nameof(target));
        Color4 clear = clearColor ?? Color4.Transparent;
        RenderPassColorAttachment color = new RenderPassColorAttachment
        {
            View = target.ColorView,
            DepthSlice = Constants.DepthSliceUndefined,
            ResolveTarget = options.ResolveTarget,
            LoadOp = clearColor.HasValue ? LoadOp.Clear : LoadOp.Load,
            StoreOp = options.DiscardColor ? StoreOp.Discard : StoreOp.Store,
            ClearValue = new Color { R = clear.R, G = clear.G, B = clear.B, A = clear.A },
        };
        RenderPassDepthStencilAttachment depth = new RenderPassDepthStencilAttachment
        {
            View = target.DepthView,
            DepthLoadOp = clearDepth.HasValue ? LoadOp.Clear : LoadOp.Load,
            DepthStoreOp = StoreOp.Store,
            DepthClearValue = clearDepth ?? 1f,
        };
        if (options.DepthReadOnly)
        {
            depth.DepthReadOnly = 1;
            depth.DepthLoadOp = LoadOp.Undefined;
            depth.DepthStoreOp = StoreOp.Undefined;
        }

        bool hasStencil = target.DepthFormat == TextureFormat.Depth24PlusStencil8 || target.DepthFormat == TextureFormat.Depth32FloatStencil8 || target.DepthFormat == TextureFormat.Stencil8;
        if (hasStencil)
        {
            if (options.StencilReadOnly)
            {
                depth.StencilReadOnly = 1;
            }
            else
            {
                depth.StencilLoadOp = options.LoadStencil ? LoadOp.Load : LoadOp.Clear;
                depth.StencilStoreOp = StoreOp.Store;
                depth.StencilClearValue = options.StencilClearValue;
            }
        }

        PassTimestamps timestamps = options.Timestamps;
        PassTimestampWrites writes = default;
        if (!timestamps.IsEmpty)
        {
            timestamps.QuerySet.ThrowIfDisposed();
            writes = new PassTimestampWrites { QuerySet = timestamps.QuerySet.Handle, BeginningOfPassWriteIndex = timestamps.BeginningIndex, EndOfPassWriteIndex = timestamps.EndIndex };
        }

        options.OcclusionQuerySet?.ThrowIfDisposed();
        byte* scratch = stackalloc byte[NativeUtf8.StackBytes];
        using NativeUtf8 label = new NativeUtf8(options.Label, scratch, NativeUtf8.StackBytes);
        RenderPassDescriptor descriptor = new RenderPassDescriptor
        {
            Label = label.View,
            ColorAttachmentCount = 1,
            ColorAttachments = &color,
            DepthStencilAttachment = target.HasDepth ? &depth : null,
            OcclusionQuerySet = options.OcclusionQuerySet == null ? QuerySet.Null : options.OcclusionQuerySet.Handle,
            TimestampWrites = timestamps.IsEmpty ? null : &writes,
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

    /// <summary>Begins a compute pass with a label and timestamp writes.</summary>
    /// <param name="options">The settings.</param>
    /// <returns>The pass.</returns>
    /// <exception cref="InvalidOperationException">Another pass is open or the frame was submitted.</exception>
    public ComputePass BeginComputePass(in ComputePassOptions options)
    {
        ThrowIfClosed();
        PassTimestamps timestamps = options.Timestamps;
        PassTimestampWrites writes = default;
        if (!timestamps.IsEmpty)
        {
            timestamps.QuerySet.ThrowIfDisposed();
            writes = new PassTimestampWrites { QuerySet = timestamps.QuerySet.Handle, BeginningOfPassWriteIndex = timestamps.BeginningIndex, EndOfPassWriteIndex = timestamps.EndIndex };
        }

        byte* scratch = stackalloc byte[NativeUtf8.StackBytes];
        using NativeUtf8 label = new NativeUtf8(options.Label, scratch, NativeUtf8.StackBytes);
        ComputePassDescriptor descriptor = new ComputePassDescriptor { Label = label.View, TimestampWrites = timestamps.IsEmpty ? null : &writes };
        ComputePassEncoder encoder = Native.WebGpu.CommandEncoderBeginComputePass(Encoder, &descriptor);
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
    /// Finishes the frame: an owned encoder is finished and submitted to the queue; a borrowed one is left for its owner;
    /// a shared texture target's pass is ended (<see cref="IWebGpuTextureTarget.EndWebGpuPass" />: the target finishes
    /// and submits the commands and hands the texture back). Then raises the context's pending error and device-lost events.
    /// </summary>
    /// <exception cref="InvalidOperationException">A pass is still open, or the frame was already submitted.</exception>
    /// <exception cref="GraphicsException">
    /// The recorded commands failed WebGPU validation (the message carries the reason). Nothing is submitted: the native
    /// engine would end the process on an invalid submission, so the frame checks first. On a shared texture target
    /// the target finishes and submits the commands, so the target checks them: an implementation that validates
    /// throws its own exception from <see cref="IWebGpuTextureTarget.EndWebGpuPass" />, which propagates from here
    /// (the frame is closed either way).
    /// </exception>
    public void Submit()
    {
        ThrowIfClosed();
        IsSubmitted = true;
        if (OwnsEncoder)
        {
            CommandBuffer commands = FinishEncoder(null, out string error);
            Native.WebGpu.CommandEncoderRelease(Encoder);
            Encoder = CommandEncoder.Null;
            if (error != null)
            {
                Native.WebGpu.CommandBufferRelease(commands);
                ReleaseTransients();
                throw new GraphicsException("The frame's commands failed validation and were not submitted: " + error);
            }

            SubmissionIndex = Native.WebGpu.QueueSubmitForIndex(Context.Queue, 1, &commands);
            Native.WebGpu.CommandBufferRelease(commands);
        }
        else
        {
            Native.WebGpu.CommandEncoderRelease(Encoder);
        }

        Encoder = CommandEncoder.Null;
        ReleaseTransients();
        EndTargetPass();
        Context.DispatchEvents();
    }

    /// <summary>
    /// Finishes an owned encoder WITHOUT submitting it, so several frames can be submitted together with
    /// <see cref="GraphicsContext.Submit(GpuCommandBuffer[])" />. The frame is closed afterwards.
    /// </summary>
    /// <param name="label">A label for the command buffer, or null.</param>
    /// <returns>The command buffer.</returns>
    /// <exception cref="InvalidOperationException">The frame's encoder is borrowed, a pass is open, or the frame is closed.</exception>
    /// <exception cref="GraphicsException">The recorded commands failed WebGPU validation.</exception>
    public GpuCommandBuffer Finish(string label = null)
    {
        ThrowIfClosed();
        if (!OwnsEncoder) throw new InvalidOperationException("A frame on a borrowed encoder cannot be finished; its owner finishes the encoder.");
        IsSubmitted = true;
        CommandBuffer commands = FinishEncoder(label, out string error);
        Native.WebGpu.CommandEncoderRelease(Encoder);
        Encoder = CommandEncoder.Null;
        if (error != null)
        {
            Native.WebGpu.CommandBufferRelease(commands);
            ReleaseTransients();
            throw new GraphicsException("The frame's commands failed validation: " + error);
        }

        // Staging buffers of the frame live as long as the command buffer that uses them.
        GpuCommandBuffer result = new GpuCommandBuffer(Context, commands, label, transientResources);
        transientResources = null;
        return result;
    }

    /// <summary>
    /// Abandons an unsubmitted owned encoder (its commands never run). For an unsubmitted frame on a shared texture
    /// target, ends any open pass and the target's WebGPU pass, so the target is usable again (the target submits what
    /// was recorded). Safe after <see cref="Submit" />.
    /// </summary>
    public void Dispose()
    {
        bool wasOpen = !IsSubmitted;
        openRenderPass?.End();
        openComputePass?.End();
        if (wasOpen && !Encoder.IsNull) Native.WebGpu.CommandEncoderRelease(Encoder);
        Encoder = CommandEncoder.Null;
        IsSubmitted = true;
        ReleaseTransients();
        if (wasOpen) EndTargetPass();
    }

    /// <summary>Clears the open-pass marker when a pass ends.</summary>
    /// <param name="pass">The pass.</param>
    internal void PassEnded(object pass)
    {
        if (ReferenceEquals(pass, openRenderPass)) openRenderPass = null;
        if (ReferenceEquals(pass, openComputePass)) openComputePass = null;
    }

    /// <summary>Keeps a resource the frame created for its own commands (a staging buffer) until the frame is submitted.</summary>
    /// <param name="resource">The resource.</param>
    internal void KeepUntilSubmitted(GraphicsResource resource)
    {
        transientResources ??= new List<GraphicsResource>();
        transientResources.Add(resource);
    }

    private CommandBuffer FinishEncoder(string label, out string error)
    {
        byte* scratch = stackalloc byte[NativeUtf8.StackBytes];
        using NativeUtf8 text = new NativeUtf8(label, scratch, NativeUtf8.StackBytes);
        CommandBufferDescriptor descriptor = new CommandBufferDescriptor { Label = text.View };

        // Pass and encoder validation errors surface here; catching them in a scope keeps an invalid command buffer
        // away from the queue (an invalid submission ends the process in the native engine).
        Context.BeginGuard();
        CommandBuffer commands = Native.WebGpu.CommandEncoderFinish(Encoder, &descriptor);
        error = Context.EndGuard();
        return commands;
    }

    private void EndTargetPass()
    {
        if (TextureTarget != null && TextureTarget.IsInWebGpuPass) TextureTarget.EndWebGpuPass();
    }

    private void ReleaseTransients()
    {
        if (transientResources == null) return;
        foreach (GraphicsResource resource in transientResources) resource.Dispose();
        transientResources = null;
    }

    private void ThrowIfClosed()
    {
        if (IsSubmitted) throw new InvalidOperationException("The frame has been submitted.");
        if (openRenderPass != null || openComputePass != null) throw new InvalidOperationException("A pass is still open; end it first.");
    }
}
