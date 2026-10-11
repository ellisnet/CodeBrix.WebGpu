using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using CodeBrix.WebGpu.Graphics.Interop;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// One GPU device and everything created on it: the WebGPU instance, adapter, device and queue - created by
/// <see cref="Create(GraphicsContextOptions)" /> or shared with another library through
/// <see cref="Wrap(Device, Queue, Instance, Adapter)" /> - plus the factory methods for every resource, pipeline,
/// frame and render target, and the lifetime tracking that releases whatever is still alive on
/// <see cref="Dispose" />.
/// </summary>
/// <remarks>
/// Not thread-safe: create and use the context, its resources and its frames from one thread. Errors that no
/// error scope catches are queued (<see cref="TakeErrors" />) and raised through <see cref="UncapturedError" />
/// from <see cref="Poll" /> and <see cref="GraphicsFrame.Submit" />, never from inside a native call.
/// </remarks>
public sealed unsafe partial class GraphicsContext : IDisposable
{
    private readonly Dictionary<GraphicsResource, long> resources = new Dictionary<GraphicsResource, long>();
    private readonly ConcurrentQueue<GraphicsError> pendingErrors = new ConcurrentQueue<GraphicsError>();
    private readonly List<GraphicsError> undeliveredErrors = new List<GraphicsError>();
    private readonly long registryId;
    private long nextSequence;
    private long nextFrameId;
    private volatile string lostMessage;
    private bool lostRaised;
    private Surface pendingSurface;
    private NativeWindowSource pendingSurfaceSource;
    private GpuTexture whiteTexture;
    private GpuSampler linearClampSampler;
    private Limits limits;

    private GraphicsContext(bool wrapped)
    {
        IsWrapped = wrapped;
        registryId = ContextRegistry.Register(this);
    }

    /// <summary>The WebGPU instance (null for a wrapped device given without one).</summary>
    public Instance Instance { get; private set; }

    /// <summary>The adapter (null for a wrapped device given without one).</summary>
    public Adapter Adapter { get; private set; }

    /// <summary>The device. Create raw WebGPU objects on it freely; the context does not track those.</summary>
    public Device Device { get; private set; }

    /// <summary>The device's queue.</summary>
    public Queue Queue { get; private set; }

    /// <summary>The native GPU API the device runs on.</summary>
    public BackendType Backend { get; private set; }

    /// <summary>The adapter's description (GPU name and driver), or null when unknown.</summary>
    public string AdapterName { get; private set; }

    /// <summary>
    /// True when the device belongs to someone else (<see cref="Wrap(Device, Queue, Instance, Adapter)" />): the
    /// context holds its own reference and releases only that.
    /// </summary>
    public bool IsWrapped { get; }

    /// <summary>True once the context has been disposed.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>True once the device has been lost (<see cref="LostReason" /> says why).</summary>
    public bool IsLost => lostMessage != null;

    /// <summary>Why the device was lost, or null.</summary>
    public string LostReason => lostMessage;

    /// <summary>The device's limits.</summary>
    public Limits Limits => limits;

    /// <summary>How many tracked resources are alive.</summary>
    public int LiveResourceCount => resources.Count;

    /// <summary>
    /// Raised (from <see cref="Poll" /> and <see cref="GraphicsFrame.Submit" />) for each WebGPU error no error scope
    /// caught. A context that wraps someone else's device receives none: the device's owner installed the callback.
    /// </summary>
    public event Action<GraphicsError> UncapturedError;

    /// <summary>Raised once (from <see cref="Poll" /> and <see cref="GraphicsFrame.Submit" />) when the device is lost.</summary>
    public event Action<string> DeviceLost;

    /// <summary>Creates a device on the platform's default backend.</summary>
    /// <returns>The context.</returns>
    /// <exception cref="GraphicsException">No adapter or device could be created (the message says why).</exception>
    public static GraphicsContext Create() => Create(null);

    /// <summary>Creates a device as the options say.</summary>
    /// <param name="options">The options (null for defaults).</param>
    /// <returns>The context.</returns>
    /// <exception cref="GraphicsException">No adapter or device could be created (the message says why).</exception>
    public static GraphicsContext Create(GraphicsContextOptions options)
    {
        if (!TryCreate(options, out GraphicsContext context, out string reason)) throw new GraphicsException(reason);
        return context;
    }

    /// <summary>Creates a device as the options say, reporting failure instead of throwing.</summary>
    /// <param name="options">The options (null for defaults).</param>
    /// <param name="context">The context, or null.</param>
    /// <param name="reason">Why no context could be created, or null.</param>
    /// <returns>True when the context was created.</returns>
    public static bool TryCreate(GraphicsContextOptions options, out GraphicsContext context, out string reason)
    {
        options ??= new GraphicsContextOptions();
        context = null;
        if (!TryLoadNative(out reason)) return false;

        InstanceExtras extras = new InstanceExtras { Backends = InstanceBackends(options.Backend) };
        extras.Chain.SType = InstanceExtras.StructType;
        InstanceDescriptor descriptor = new InstanceDescriptor { NextInChain = &extras.Chain };
        Instance instance = Native.WebGpu.CreateInstance(&descriptor);
        if (instance.IsNull)
        {
            reason = "wgpuCreateInstance returned null for the " + options.Backend + " backend.";
            return false;
        }

        GraphicsContext candidate = new GraphicsContext(false) { Instance = instance };
        if (options.CompatibleWindow != null)
        {
            candidate.pendingSurface = NativeWindowSource.CreateSurface(instance, options.CompatibleWindow);
            if (candidate.pendingSurface.IsNull)
            {
                candidate.Dispose();
                reason = "No WebGPU surface could be created for the compatible window (" + options.CompatibleWindow + ").";
                return false;
            }

            candidate.pendingSurfaceSource = options.CompatibleWindow;
        }

        Adapter adapter = GpuRequests.RequestAdapter(instance, BackendTypeFor(options.Backend), options.PowerPreference, options.ForceFallbackAdapter, candidate.pendingSurface, out string adapterMessage);
        if (adapter.IsNull)
        {
            candidate.Dispose();
            reason = "No WebGPU adapter for the " + options.Backend + " backend (" + Describe(adapterMessage) + ").";
            return false;
        }

        candidate.Adapter = adapter;
        Device device = GpuRequests.RequestDevice(instance, adapter, candidate.registryId, options.Label, out string deviceMessage);
        if (device.IsNull)
        {
            candidate.Dispose();
            reason = "The adapter refused a device (" + Describe(deviceMessage) + ").";
            return false;
        }

        candidate.Attach(device, Native.WebGpu.DeviceGetQueue(device));
        context = candidate;
        reason = null;
        return true;
    }

    /// <summary>
    /// Shares a device another library created - for example the <c>Device</c>, <c>Queue</c>, <c>Instance</c> and
    /// <c>Adapter</c> of a <c>WebGpuSkiaDevice</c>. The context takes its own reference on each handle (so it may be
    /// disposed before or after the owner) and installs no callbacks: errors go to the owner's handlers.
    /// </summary>
    /// <param name="device">The device.</param>
    /// <param name="queue">The device's queue.</param>
    /// <param name="instance">The instance that owns the adapter (needed for window surfaces; optional otherwise).</param>
    /// <param name="adapter">The adapter (needed to query window-surface capabilities; optional otherwise).</param>
    /// <returns>The context.</returns>
    /// <exception cref="ArgumentException"><paramref name="device" /> or <paramref name="queue" /> is null.</exception>
    /// <exception cref="GraphicsException">The native library cannot be loaded.</exception>
    public static GraphicsContext Wrap(Device device, Queue queue, Instance instance = default, Adapter adapter = default)
    {
        if (device.IsNull) throw new ArgumentException("The device handle is null.", nameof(device));
        if (queue.IsNull) throw new ArgumentException("The queue handle is null.", nameof(queue));
        if (!TryLoadNative(out string reason)) throw new GraphicsException(reason);

        Native.WebGpu.DeviceAddRef(device);
        Native.WebGpu.QueueAddRef(queue);
        if (!instance.IsNull) Native.WebGpu.InstanceAddRef(instance);
        if (!adapter.IsNull) Native.WebGpu.AdapterAddRef(adapter);
        GraphicsContext context = new GraphicsContext(true) { Instance = instance, Adapter = adapter };
        context.Attach(device, queue);
        return context;
    }

    /// <summary>
    /// Processes pending WebGPU callbacks, then raises <see cref="UncapturedError" /> and <see cref="DeviceLost" />
    /// for anything that arrived.
    /// </summary>
    /// <param name="wait">True to wait for all submitted GPU work to finish first.</param>
    public void Poll(bool wait = false)
    {
        ThrowIfDisposed();
        Native.WebGpu.DevicePoll(Device, wait ? 1u : 0u, null);
        if (!Instance.IsNull) Native.WebGpu.InstanceProcessEvents(Instance);
        DispatchEvents();
    }

    /// <summary>Waits until the GPU has finished all submitted work.</summary>
    public void WaitIdle() => Poll(true);

    /// <summary>Returns and clears the WebGPU errors no error scope caught (oldest first).</summary>
    /// <returns>The errors.</returns>
    public GraphicsError[] TakeErrors()
    {
        DrainPendingErrors();
        GraphicsError[] errors = undeliveredErrors.ToArray();
        undeliveredErrors.Clear();
        return errors;
    }

    /// <summary>The tracked resources still alive, oldest first.</summary>
    /// <returns>A snapshot.</returns>
    public IReadOnlyList<GraphicsResource> GetLiveResources() => resources.OrderBy(pair => pair.Value).Select(pair => pair.Key).ToList();

    /// <summary>
    /// Starts a frame with its own command encoder: record passes, then <see cref="GraphicsFrame.Submit" /> finishes
    /// and submits it.
    /// </summary>
    /// <returns>The frame.</returns>
    public GraphicsFrame BeginFrame()
    {
        ThrowIfDisposed();
        CommandEncoder encoder = Native.WebGpu.DeviceCreateCommandEncoder(Device, null);
        if (encoder.IsNull) throw new GraphicsException("wgpuDeviceCreateCommandEncoder returned null" + (IsLost ? " (the device is lost: " + LostReason + ")." : "."));
        return new GraphicsFrame(this, encoder, true);
    }

    /// <summary>
    /// Starts a frame that records into an encoder someone else owns - for example the one
    /// <c>WebGpuSkiaTarget.BeginWebGpuPass</c> returns. <see cref="GraphicsFrame.Submit" /> then only closes the
    /// frame; the encoder's owner finishes and submits it.
    /// </summary>
    /// <param name="encoder">The encoder.</param>
    /// <returns>The frame.</returns>
    /// <exception cref="ArgumentException"><paramref name="encoder" /> is null.</exception>
    public GraphicsFrame BeginFrame(CommandEncoder encoder)
    {
        ThrowIfDisposed();
        if (encoder.IsNull) throw new ArgumentException("The encoder handle is null.", nameof(encoder));
        return new GraphicsFrame(this, encoder, false);
    }

    /// <summary>
    /// Releases every tracked resource still alive (newest first), then the queue, device, adapter and instance (for
    /// a wrapped device: only this context's references).
    /// </summary>
    public void Dispose()
    {
        if (IsDisposed) return;
        List<GraphicsResource> alive = resources.OrderByDescending(pair => pair.Value).Select(pair => pair.Key).ToList();
        foreach (GraphicsResource resource in alive) resource.ReleaseFromContext();
        resources.Clear();
        IsDisposed = true;
        if (!pendingSurface.IsNull) Native.WebGpu.SurfaceRelease(pendingSurface);
        pendingSurface = Surface.Null;
        if (!Queue.IsNull) Native.WebGpu.QueueRelease(Queue);
        if (!Device.IsNull) Native.WebGpu.DeviceRelease(Device);
        if (!Adapter.IsNull) Native.WebGpu.AdapterRelease(Adapter);
        if (!Instance.IsNull) Native.WebGpu.InstanceRelease(Instance);
        Queue = Queue.Null;
        Device = Device.Null;
        Adapter = Adapter.Null;
        Instance = Instance.Null;
        ContextRegistry.Unregister(registryId);
    }

    /// <summary>Describes the context.</summary>
    /// <returns>For example <c>GraphicsContext Vulkan Intel(R) UHD Graphics (owned)</c>.</returns>
    public override string ToString() => "GraphicsContext " + Backend + " " + (AdapterName ?? "unknown adapter") + (IsWrapped ? " (wrapped)" : " (owned)") + (IsDisposed ? " (disposed)" : string.Empty);

    /// <summary>Queues an uncaptured error (native callback; no WebGPU calls allowed here).</summary>
    /// <param name="type">The type.</param>
    /// <param name="message">The message.</param>
    internal void ReportUncapturedError(ErrorType type, string message) => pendingErrors.Enqueue(new GraphicsError(type, message));

    /// <summary>Records the device loss (native callback; no WebGPU calls allowed here).</summary>
    /// <param name="reason">The reason.</param>
    /// <param name="message">The message.</param>
    internal void ReportDeviceLost(DeviceLostReason reason, string message) => lostMessage = reason + ": " + message;

    /// <summary>Raises the queued events.</summary>
    internal void DispatchEvents()
    {
        DrainPendingErrors();
        if (UncapturedError != null && undeliveredErrors.Count > 0)
        {
            GraphicsError[] errors = undeliveredErrors.ToArray();
            undeliveredErrors.Clear();
            foreach (GraphicsError error in errors) UncapturedError(error);
        }

        if (lostMessage != null && !lostRaised)
        {
            lostRaised = true;
            DeviceLost?.Invoke(lostMessage);
        }
    }

    /// <summary>A new, unique frame id.</summary>
    /// <returns>The id.</returns>
    internal long NextFrameId() => ++nextFrameId;

    /// <summary>Starts tracking a resource.</summary>
    /// <param name="resource">The resource.</param>
    internal void Track(GraphicsResource resource) => resources[resource] = ++nextSequence;

    /// <summary>Stops tracking a resource.</summary>
    /// <param name="resource">The resource.</param>
    internal void Untrack(GraphicsResource resource) => resources.Remove(resource);

    /// <summary>Throws when the context has been disposed.</summary>
    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);

    /// <summary>Opens a validation error scope around an object creation.</summary>
    internal void BeginGuard() => GpuRequests.PushValidationScope(Device);

    /// <summary>Closes the scope opened by <see cref="BeginGuard" />.</summary>
    /// <returns>The validation message, or null.</returns>
    internal string EndGuard() => GpuRequests.PopErrorScope(Instance, Device);

    /// <summary>Takes the surface created for <see cref="GraphicsContextOptions.CompatibleWindow" /> when <paramref name="source" /> is that window.</summary>
    /// <param name="source">The window.</param>
    /// <returns>The surface (ownership passes to the caller), or null.</returns>
    internal Surface TakePendingSurface(NativeWindowSource source)
    {
        if (pendingSurface.IsNull || !ReferenceEquals(source, pendingSurfaceSource)) return Surface.Null;
        Surface surface = pendingSurface;
        pendingSurface = Surface.Null;
        pendingSurfaceSource = null;
        return surface;
    }

    /// <summary>A shared 1 x 1 opaque white RGBA texture (for untextured draws). Owned by the context.</summary>
    internal GpuTexture WhiteTexture
    {
        get
        {
            if (whiteTexture == null || whiteTexture.IsDisposed)
            {
                whiteTexture = CreateTexture2D(1, 1, TextureFormat.RGBA8Unorm, TextureUsage.TextureBinding | TextureUsage.CopyDst, 1, "white");
                whiteTexture.Write(new byte[] { 255, 255, 255, 255 });
            }

            return whiteTexture;
        }
    }

    /// <summary>A shared linear, clamp-to-edge sampler. Owned by the context.</summary>
    internal GpuSampler LinearClampSampler
    {
        get
        {
            if (linearClampSampler == null || linearClampSampler.IsDisposed) linearClampSampler = CreateSampler(SamplerDescription.LinearClamp);
            return linearClampSampler;
        }
    }

    private void Attach(Device device, Queue queue)
    {
        Device = device;
        Queue = queue;
        Backend = Native.WebGpu.CodebrixDeviceGetBackend(device);
        Limits deviceLimits = default;
        if (Native.WebGpu.DeviceGetLimits(device, &deviceLimits) == Status.Success) limits = deviceLimits;
        limits.NextInChain = null;
        AdapterInfo info = default;
        if (!Adapter.IsNull && Native.WebGpu.AdapterGetInfo(Adapter, &info) == Status.Success)
        {
            AdapterName = WgpuString.ToManaged(info.Device);
            string driver = WgpuString.ToManaged(info.Description);
            if (!string.IsNullOrEmpty(driver)) AdapterName += " (" + driver + ")";
            Native.WebGpu.AdapterInfoFreeMembers(info);
        }
    }

    private void DrainPendingErrors()
    {
        while (pendingErrors.TryDequeue(out GraphicsError error)) undeliveredErrors.Add(error);
    }

    private static bool TryLoadNative(out string reason)
    {
        try
        {
            CodeBrixWebGpuNative.EnsureLoaded();
            reason = null;
            return true;
        }
        catch (Exception exception) when (exception is WgpuException || exception is DllNotFoundException || exception is EntryPointNotFoundException || exception is BadImageFormatException)
        {
            reason = "The native WebGPU library cannot be used on this platform: " + exception.Message;
            return false;
        }
    }

    private static InstanceBackend InstanceBackends(GraphicsBackend backend) => backend switch
    {
        GraphicsBackend.Vulkan => InstanceBackend.Vulkan,
        GraphicsBackend.Direct3D12 => InstanceBackend.DX12,
        GraphicsBackend.Metal => InstanceBackend.Metal,
        GraphicsBackend.OpenGL => InstanceBackend.GL,
        _ => InstanceBackend.Primary,
    };

    private static BackendType BackendTypeFor(GraphicsBackend backend) => backend switch
    {
        GraphicsBackend.Vulkan => BackendType.Vulkan,
        GraphicsBackend.Direct3D12 => BackendType.D3D12,
        GraphicsBackend.Metal => BackendType.Metal,
        GraphicsBackend.OpenGL => BackendType.Undefined,
        _ => BackendType.Undefined,
    };

    private static string Describe(string message) => string.IsNullOrEmpty(message) ? "no message" : message;
}
