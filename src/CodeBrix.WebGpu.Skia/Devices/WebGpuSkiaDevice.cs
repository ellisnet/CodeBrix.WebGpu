using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using CodeBrix.WebGpu.Native;
using CodeBrix.WebGpu.Skia.Backends;
using CodeBrix.WebGpu.Skia.Interop;
using SkiaSharp;

namespace CodeBrix.WebGpu.Skia;

/// <summary>
/// One GPU device shared by WebGPU and Skia. Either the library creates the device
/// (<see cref="Create()" />, <see cref="WebGpuSkiaDeviceRoute.Owned" />) and then the Skia
/// <see cref="GRContext" /> on it (<see cref="CreateGrContext" />), or it adopts the native device behind an
/// application's own <see cref="GRContext" /> (<see cref="AdoptVulkan" />, <see cref="AdoptMetal" />,
/// <see cref="AdoptDirect3D" />, <see cref="WebGpuSkiaDeviceRoute.Adopted" />). Never throws for a platform
/// that cannot do something: <see cref="IsAvailable" /> is false and <see cref="Reason" /> says why.
/// </summary>
/// <remarks>
/// Not thread-safe: use the device, its <see cref="GrContext" /> and its targets from one thread (WebGPU and
/// Skia submit to the same native queue). Dispose every <see cref="WebGpuSkiaTarget" /> before the device.
/// </remarks>
public sealed unsafe class WebGpuSkiaDevice : IDisposable
{
    private readonly ConcurrentQueue<string> uncapturedErrors = new ConcurrentQueue<string>();
    private readonly List<IntPtr> adoptAllocations = new List<IntPtr>();
    private readonly bool ownsGrContext;
    private SkiaBridge bridge;
    private GRContext grContext;
    private GCHandle self;
    private bool disposed;

    private WebGpuSkiaDevice(string reason)
    {
        Reason = reason;
        GrContextReason = reason;
    }

    private WebGpuSkiaDevice(WebGpuSkiaDeviceRoute route, WebGpuSkiaBackend backend, Instance instance, GRContext adoptedContext)
    {
        Route = route;
        Backend = backend;
        Instance = instance;
        self = GCHandle.Alloc(this, GCHandleType.Weak);
        bridge = SkiaBridge.For(backend);
        grContext = adoptedContext;
        ownsGrContext = adoptedContext == null;
    }

    /// <summary>True when the device exists and WebGPU work can be recorded on it.</summary>
    public bool IsAvailable => !Device.IsNull && !disposed;

    /// <summary>Why the device is unavailable, or null when it is available.</summary>
    public string Reason { get; private set; }

    /// <summary>Who owns the native device.</summary>
    public WebGpuSkiaDeviceRoute Route { get; }

    /// <summary>The native GPU API, or <see cref="WebGpuSkiaBackend.None" /> when unavailable.</summary>
    public WebGpuSkiaBackend Backend { get; }

    /// <summary>The adapter's description (GPU name and driver), or null.</summary>
    public string AdapterName { get; private set; }

    /// <summary>
    /// True when WebGPU and Skia submit to the same native queue, so submission order alone orders their work.
    /// False for an adopted Metal device and for an adopted Vulkan device on another queue: every hand-over
    /// then waits for the GPU.
    /// </summary>
    public bool SharesQueueWithSkia { get; private set; }

    /// <summary>The WebGPU instance. Owned by this object.</summary>
    public Instance Instance { get; }

    /// <summary>The WebGPU adapter. Owned by this object.</summary>
    public Adapter Adapter { get; private set; }

    /// <summary>The WebGPU device: create pipelines, buffers and textures on it as usual. Owned by this object.</summary>
    public Device Device { get; private set; }

    /// <summary>The device's queue. Owned by this object.</summary>
    public Queue Queue { get; private set; }

    /// <summary>
    /// The Skia context on this device: the one <see cref="CreateGrContext" /> made (owned route; disposed with
    /// the device) or the application's (adopted route; never disposed here). Null until then.
    /// </summary>
    public GRContext GrContext => grContext;

    /// <summary>Why <see cref="CreateGrContext" /> returned null, or null.</summary>
    public string GrContextReason { get; private set; }

    /// <summary>
    /// Creates a WebGPU device on the platform's backend (Direct3D 12 on Windows, then Vulkan; Metal on macOS;
    /// Vulkan on Linux and Android). Check <see cref="IsAvailable" />.
    /// </summary>
    /// <returns>The device.</returns>
    public static WebGpuSkiaDevice Create() => Create(null);

    /// <summary>Creates a WebGPU device as the options say. Check <see cref="IsAvailable" />.</summary>
    /// <param name="options">The options (null for defaults).</param>
    /// <returns>The device.</returns>
    public static WebGpuSkiaDevice Create(WebGpuSkiaDeviceOptions options)
    {
        options ??= new WebGpuSkiaDeviceOptions();
        if (!TryLoadNative(out string loadReason)) return new WebGpuSkiaDevice(loadReason);

        List<string> failures = new List<string>();
        foreach (WebGpuSkiaBackend backend in BackendOrder(options))
        {
            if (!PlatformCompiles(backend))
            {
                failures.Add(backend + ": not available in the native library for this platform");
                continue;
            }

            Instance instance = CreateInstance(backend);
            if (instance.IsNull)
            {
                failures.Add(backend + ": wgpuCreateInstance returned null");
                continue;
            }

            WebGpuSkiaDevice candidate = new WebGpuSkiaDevice(WebGpuSkiaDeviceRoute.Owned, backend, instance, null);
            Adapter adapter = WgpuRequests.RequestAdapter(instance, BackendType(backend), options.PowerPreference, options.ForceFallbackAdapter, out string adapterMessage);
            if (adapter.IsNull)
            {
                failures.Add(backend + ": no adapter (" + Describe(adapterMessage) + ")");
                candidate.Dispose();
                continue;
            }

            candidate.Adapter = adapter;
            Device device = WgpuRequests.RequestDevice(instance, adapter, GCHandle.ToIntPtr(candidate.self), out string deviceMessage);
            if (device.IsNull)
            {
                failures.Add(backend + ": the adapter refused a device (" + Describe(deviceMessage) + ")");
                candidate.Dispose();
                continue;
            }

            candidate.AttachDevice(device);
            candidate.SharesQueueWithSkia = true;
            return candidate;
        }

        return new WebGpuSkiaDevice("No WebGPU device could be created: " + string.Join("; ", failures) + ".");
    }

    /// <summary>
    /// Adopts the Vulkan device behind an application's <see cref="GRContext" /> as a WebGPU device (secondary
    /// route). Surfaces the library then creates on <paramref name="grContext" /> get
    /// <see cref="WebGpuSkiaTier.ZeroCopy" />; the application's own surfaces on it get
    /// <see cref="WebGpuSkiaTier.GpuCopy" />. The device must have been created with the extensions and
    /// features the WebGPU engine needs; otherwise the result is unavailable with the reason.
    /// </summary>
    /// <param name="grContext">The application's Skia context, created with <paramref name="backendContext" />. Not disposed by this object.</param>
    /// <param name="backendContext">The backend context the application created <paramref name="grContext" /> with.</param>
    /// <param name="enabledInstanceExtensions">The instance extensions the application enabled (SkiaSharp cannot list them).</param>
    /// <param name="enabledDeviceExtensions">The device extensions the application enabled (SkiaSharp cannot list them).</param>
    /// <param name="instanceApiVersion">
    /// The Vulkan API version the application created its <c>VkInstance</c> with (<c>VkApplicationInfo.apiVersion</c>).
    /// 0 (the default) uses the loader's version (<c>vkEnumerateInstanceVersion</c>). Not
    /// <c>backendContext.MaxAPIVersion</c>, which only caps what Skia uses.
    /// </param>
    /// <param name="queueIndex">The index of <c>backendContext.VkQueue</c> within its family (<c>GraphicsQueueIndex</c>). Default 0.</param>
    /// <returns>The device. Check <see cref="IsAvailable" />.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static WebGpuSkiaDevice AdoptVulkan(GRContext grContext, GRVkBackendContext backendContext, IReadOnlyList<string> enabledInstanceExtensions, IReadOnlyList<string> enabledDeviceExtensions, uint instanceApiVersion = 0, uint queueIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(grContext);
        ArgumentNullException.ThrowIfNull(backendContext);
        ArgumentNullException.ThrowIfNull(enabledInstanceExtensions);
        ArgumentNullException.ThrowIfNull(enabledDeviceExtensions);
        if (grContext.Backend != GRBackend.Vulkan) return new WebGpuSkiaDevice("The GRContext is on " + grContext.Backend + ", not Vulkan.");
        if (!TryLoadNative(out string loadReason)) return new WebGpuSkiaDevice(loadReason);
        if (!PlatformCompiles(WebGpuSkiaBackend.Vulkan)) return new WebGpuSkiaDevice("The native library for this platform has no Vulkan backend.");
        if (backendContext.GetProcedureAddress == null) return new WebGpuSkiaDevice("The GRVkBackendContext has no GetProcedureAddress delegate.");

        IntPtr getInstanceProcAddr = backendContext.GetProcedureAddress("vkGetInstanceProcAddr", backendContext.VkInstance, IntPtr.Zero);
        if (getInstanceProcAddr == IntPtr.Zero) return new WebGpuSkiaDevice("The GRVkBackendContext's GetProcedureAddress cannot resolve vkGetInstanceProcAddr.");

        VulkanProcedures procedures = new VulkanProcedures(getInstanceProcAddr, backendContext.VkInstance);
        uint apiVersion = instanceApiVersion != 0 ? instanceApiVersion : LoaderApiVersion(procedures);
        Instance instance = CreateInstance(WebGpuSkiaBackend.Vulkan);
        if (instance.IsNull) return new WebGpuSkiaDevice("wgpuCreateInstance returned null.");

        WebGpuSkiaDevice result = new WebGpuSkiaDevice(WebGpuSkiaDeviceRoute.Adopted, WebGpuSkiaBackend.Vulkan, instance, grContext);
        CodebrixVulkanAdoptDescriptor descriptor = new CodebrixVulkanAdoptDescriptor
        {
            Instance = (void*)backendContext.VkInstance,
            PhysicalDevice = (void*)backendContext.VkPhysicalDevice,
            Device = (void*)backendContext.VkDevice,
            QueueFamilyIndex = backendContext.GraphicsQueueIndex,
            QueueIndex = queueIndex,
            InstanceApiVersion = apiVersion,
            GetInstanceProcAddr = (void*)getInstanceProcAddr,
            EnabledInstanceExtensionCount = (nuint)enabledInstanceExtensions.Count,
            EnabledInstanceExtensions = result.AllocateNames(enabledInstanceExtensions),
            EnabledDeviceExtensionCount = (nuint)enabledDeviceExtensions.Count,
            EnabledDeviceExtensions = result.AllocateNames(enabledDeviceExtensions),
        };

        Adapter adapter = default;
        Device device = default;
        Status status = Native.WebGpu.CodebrixInstanceAdoptVulkanDevice(instance, &descriptor, &adapter, &device);
        if (status != Status.Success || device.IsNull)
        {
            result.Adapter = adapter;
            result.Dispose();
            return new WebGpuSkiaDevice("wgpuCodebrixInstanceAdoptVulkanDevice reported " + status + ": the VkDevice lacks extensions or features the WebGPU engine requires, or the objects do not belong together (a log callback set with wgpuSetLogCallback receives the details).");
        }

        result.Adapter = adapter;
        result.AttachDevice(device);
        result.SharesQueueWithSkia = procedures.GetDeviceQueue(backendContext.VkDevice, backendContext.GraphicsQueueIndex, queueIndex) == backendContext.VkQueue;
        return result;
    }

    /// <summary>
    /// Adopts the Metal device behind an application's <see cref="GRContext" /> as a WebGPU device (secondary
    /// route). WebGPU gets its own command queue on that device, so every hand-over between WebGPU and Skia
    /// waits for the GPU (<see cref="SharesQueueWithSkia" /> is false).
    /// </summary>
    /// <param name="grContext">The application's Skia context, created with <paramref name="backendContext" />. Not disposed by this object.</param>
    /// <param name="backendContext">The backend context the application created <paramref name="grContext" /> with.</param>
    /// <returns>The device. Check <see cref="IsAvailable" />.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static WebGpuSkiaDevice AdoptMetal(GRContext grContext, GRMtlBackendContext backendContext)
    {
        ArgumentNullException.ThrowIfNull(grContext);
        ArgumentNullException.ThrowIfNull(backendContext);
        if (grContext.Backend != GRBackend.Metal) return new WebGpuSkiaDevice("The GRContext is on " + grContext.Backend + ", not Metal.");
        if (!TryLoadNative(out string loadReason)) return new WebGpuSkiaDevice(loadReason);
        if (!PlatformCompiles(WebGpuSkiaBackend.Metal)) return new WebGpuSkiaDevice("The native library for this platform has no Metal backend.");

        Instance instance = CreateInstance(WebGpuSkiaBackend.Metal);
        if (instance.IsNull) return new WebGpuSkiaDevice("wgpuCreateInstance returned null.");

        WebGpuSkiaDevice result = new WebGpuSkiaDevice(WebGpuSkiaDeviceRoute.Adopted, WebGpuSkiaBackend.Metal, instance, grContext);
        CodebrixMetalAdoptDescriptor descriptor = new CodebrixMetalAdoptDescriptor { Device = (void*)backendContext.DeviceHandle };
        Adapter adapter = default;
        Device device = default;
        Status status = Native.WebGpu.CodebrixInstanceAdoptMetalDevice(instance, &descriptor, &adapter, &device);
        if (status != Status.Success || device.IsNull)
        {
            result.Adapter = adapter;
            result.Dispose();
            return new WebGpuSkiaDevice("wgpuCodebrixInstanceAdoptMetalDevice reported " + status + ".");
        }

        result.Adapter = adapter;
        result.AttachDevice(device);
        result.SharesQueueWithSkia = false;
        return result;
    }

    /// <summary>
    /// Tries to adopt the Direct3D 12 device behind an application's <see cref="GRContext" />. The native library
    /// shipped today cannot adopt a Direct3D 12 device, so this reports unavailable with the reason; for zero
    /// copy on Direct3D 12 let the library own the device (<see cref="Create()" /> then
    /// <see cref="CreateGrContext" />). The application's surfaces still reach
    /// <see cref="WebGpuSkiaTier.Readback" /> through a separately created device.
    /// </summary>
    /// <param name="grContext">The application's Skia context. Not disposed by this object.</param>
    /// <param name="backendContext">The backend context the application created <paramref name="grContext" /> with.</param>
    /// <returns>The device. Check <see cref="IsAvailable" />.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static WebGpuSkiaDevice AdoptDirect3D(GRContext grContext, GRD3DBackendContext backendContext)
    {
        ArgumentNullException.ThrowIfNull(grContext);
        ArgumentNullException.ThrowIfNull(backendContext);
        const string Limitation = "Adopting an application's Direct3D 12 device is not supported by this native library (wgpuCodebrixInstanceAdoptD3d12Device reports Error); let the library own the device - WebGpuSkiaDevice.Create() then CreateGrContext() - for zero copy on Direct3D 12.";
        if (grContext.Backend != GRBackend.Direct3D) return new WebGpuSkiaDevice("The GRContext is on " + grContext.Backend + ", not Direct3D.");
        if (!TryLoadNative(out string loadReason)) return new WebGpuSkiaDevice(loadReason);
        if (!PlatformCompiles(WebGpuSkiaBackend.Direct3D12)) return new WebGpuSkiaDevice("The native library for this platform has no Direct3D 12 backend. " + Limitation);

        Instance instance = CreateInstance(WebGpuSkiaBackend.Direct3D12);
        if (instance.IsNull) return new WebGpuSkiaDevice("wgpuCreateInstance returned null.");

        WebGpuSkiaDevice result = new WebGpuSkiaDevice(WebGpuSkiaDeviceRoute.Adopted, WebGpuSkiaBackend.Direct3D12, instance, grContext);
        CodebrixD3d12AdoptDescriptor descriptor = new CodebrixD3d12AdoptDescriptor
        {
            Adapter = (void*)backendContext.Adapter,
            Device = (void*)backendContext.Device,
            Queue = (void*)backendContext.Queue,
        };
        Adapter adapter = default;
        Device device = default;
        Status status = Native.WebGpu.CodebrixInstanceAdoptD3d12Device(instance, &descriptor, &adapter, &device);
        if (status != Status.Success || device.IsNull)
        {
            result.Adapter = adapter;
            result.Dispose();
            return new WebGpuSkiaDevice(Limitation);
        }

        result.Adapter = adapter;
        result.AttachDevice(device);
        result.SharesQueueWithSkia = true;
        return result;
    }

    /// <summary>
    /// Creates the Skia <see cref="GRContext" /> on this device (owned route), or returns the one it already has.
    /// The context belongs to the device: do not dispose it; the device does. Every surface the library creates on
    /// it is <see cref="WebGpuSkiaTier.ZeroCopy" />.
    /// </summary>
    /// <returns>The context, or null with <see cref="GrContextReason" /> set.</returns>
    public GRContext CreateGrContext()
    {
        if (grContext != null) return grContext;
        if (!IsAvailable)
        {
            GrContextReason = Reason ?? "The device has been disposed.";
            return null;
        }

        grContext = bridge.CreateGrContext(Device, Queue, out string reason);
        GrContextReason = reason;
        return grContext;
    }

    /// <summary>True when <paramref name="context" /> is this device's Skia context (so its surfaces share the GPU device).</summary>
    /// <param name="context">A Skia context (for example <c>SKSurface.Context</c>), or null.</param>
    /// <returns>True when it is this device's context.</returns>
    public bool SharesDeviceWith(GRRecordingContext context) =>
        context != null && grContext != null && IsAvailable && context.Handle == grContext.Handle;

    /// <summary>
    /// Returns and clears the WebGPU validation errors reported through the device's uncaptured-error callback
    /// (owned route only; an adopted device has no callback).
    /// </summary>
    /// <returns>The messages, oldest first.</returns>
    public string[] TakeUncapturedErrors()
    {
        List<string> messages = new List<string>();
        while (uncapturedErrors.TryDequeue(out string message)) messages.Add(message);
        return messages.ToArray();
    }

    /// <summary>
    /// Releases the Skia context (owned route), then the WebGPU device, adapter and instance. Dispose every
    /// target first.
    /// </summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (grContext != null)
        {
            if (!Device.IsNull)
            {
                grContext.Flush();
                grContext.Submit(true);
            }

            if (ownsGrContext) grContext.Dispose();
            grContext = null;
        }

        bridge?.Dispose();
        bridge = null;
        if (!Queue.IsNull) Native.WebGpu.QueueRelease(Queue);
        if (!Device.IsNull) Native.WebGpu.DeviceRelease(Device);
        if (!Adapter.IsNull) Native.WebGpu.AdapterRelease(Adapter);
        if (!Instance.IsNull) Native.WebGpu.InstanceRelease(Instance);
        Queue = Queue.Null;
        Device = Device.Null;
        Adapter = Adapter.Null;
        foreach (IntPtr allocation in adoptAllocations) NativeMemory.Free((void*)allocation);
        adoptAllocations.Clear();
        if (self.IsAllocated) self.Free();
        Reason ??= "The device has been disposed.";
    }

    /// <summary>Queues a message from the uncaptured-error callback.</summary>
    /// <param name="message">The message.</param>
    internal void ReportUncapturedError(string message) => uncapturedErrors.Enqueue(message);

    /// <summary>The backend bridge (null when unavailable).</summary>
    internal SkiaBridge Bridge => bridge;

    /// <summary>
    /// Hands the GPU from Skia to WebGPU: flushes and submits everything Skia recorded, waiting for the GPU when the
    /// two do not share a queue.
    /// </summary>
    internal void FlushSkia()
    {
        if (grContext == null || grContext.IsAbandoned) return;
        grContext.Flush();
        grContext.Submit(!SharesQueueWithSkia);
    }

    /// <summary>Submits a WebGPU command buffer, waiting for the GPU when WebGPU and Skia do not share a queue.</summary>
    /// <param name="commands">The command buffer (released here).</param>
    internal void Submit(CommandBuffer commands)
    {
        Native.WebGpu.QueueSubmit(Queue, 1, &commands);
        Native.WebGpu.CommandBufferRelease(commands);
        if (!SharesQueueWithSkia) Native.WebGpu.DevicePoll(Device, 1, null);
    }

    /// <summary>The backend the native library compiles for this operating system.</summary>
    /// <param name="backend">The backend.</param>
    /// <returns>True when it can exist here.</returns>
    internal static bool PlatformCompiles(WebGpuSkiaBackend backend) => backend switch
    {
        WebGpuSkiaBackend.Vulkan => OperatingSystem.IsLinux() || OperatingSystem.IsAndroid() || OperatingSystem.IsWindows(),
        WebGpuSkiaBackend.Direct3D12 => OperatingSystem.IsWindows(),
        WebGpuSkiaBackend.Metal => OperatingSystem.IsMacOS(),
        _ => false,
    };

    /// <summary>The backends <see cref="Create(WebGpuSkiaDeviceOptions)" /> tries, in order.</summary>
    /// <param name="options">The options.</param>
    /// <returns>The backends.</returns>
    internal static IReadOnlyList<WebGpuSkiaBackend> BackendOrder(WebGpuSkiaDeviceOptions options)
    {
        List<WebGpuSkiaBackend> platform = new List<WebGpuSkiaBackend>();
        if (OperatingSystem.IsWindows())
        {
            platform.Add(WebGpuSkiaBackend.Direct3D12);
            platform.Add(WebGpuSkiaBackend.Vulkan);
        }
        else if (OperatingSystem.IsMacOS())
        {
            platform.Add(WebGpuSkiaBackend.Metal);
        }
        else
        {
            platform.Add(WebGpuSkiaBackend.Vulkan);
        }

        if (options.PreferredBackend == WebGpuSkiaBackend.None) return options.AllowFallbackBackend ? platform : platform.GetRange(0, 1);

        List<WebGpuSkiaBackend> order = new List<WebGpuSkiaBackend> { options.PreferredBackend };
        if (options.AllowFallbackBackend)
        {
            foreach (WebGpuSkiaBackend backend in platform)
            {
                if (backend != options.PreferredBackend) order.Add(backend);
            }
        }

        return order;
    }

    private void AttachDevice(Device device)
    {
        Device = device;
        Queue = Native.WebGpu.DeviceGetQueue(device);
        AdapterInfo info = default;
        if (!Adapter.IsNull && Native.WebGpu.AdapterGetInfo(Adapter, &info) == Status.Success)
        {
            AdapterName = WgpuString.ToManaged(info.Device);
            string driver = WgpuString.ToManaged(info.Description);
            if (!string.IsNullOrEmpty(driver)) AdapterName += " (" + driver + ")";
            Native.WebGpu.AdapterInfoFreeMembers(info);
        }

        Reason = null;
        GrContextReason = null;
    }

    private CodebrixName* AllocateNames(IReadOnlyList<string> names)
    {
        if (names.Count == 0) return null;
        CodebrixName* array = (CodebrixName*)NativeMemory.AllocZeroed((nuint)(sizeof(CodebrixName) * names.Count));
        adoptAllocations.Add((IntPtr)array);
        for (int index = 0; index < names.Count; index++)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(names[index] ?? string.Empty);
            byte* text = (byte*)NativeMemory.Alloc((nuint)utf8.Length + 1);
            adoptAllocations.Add((IntPtr)text);
            Marshal.Copy(utf8, 0, (IntPtr)text, utf8.Length);
            text[utf8.Length] = 0;
            array[index].Name = WgpuString.FromPinned(text, utf8.Length);
        }

        return array;
    }

    private static uint LoaderApiVersion(VulkanProcedures procedures)
    {
        IntPtr entry = procedures.Lookup(IntPtr.Zero, "vkEnumerateInstanceVersion");
        if (entry == IntPtr.Zero) return NativeConstants.VkApiVersion10;
        uint version = 0;
        return ((delegate* unmanaged<uint*, int>)entry)(&version) == 0 ? version : NativeConstants.VkApiVersion10;
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

    private static Instance CreateInstance(WebGpuSkiaBackend backend)
    {
        InstanceExtras extras = new InstanceExtras { Backends = InstanceBackends(backend) };
        extras.Chain.SType = InstanceExtras.StructType;
        InstanceDescriptor descriptor = new InstanceDescriptor { NextInChain = &extras.Chain };
        return Native.WebGpu.CreateInstance(&descriptor);
    }

    private static InstanceBackend InstanceBackends(WebGpuSkiaBackend backend) => backend switch
    {
        WebGpuSkiaBackend.Vulkan => InstanceBackend.Vulkan,
        WebGpuSkiaBackend.Direct3D12 => InstanceBackend.DX12,
        WebGpuSkiaBackend.Metal => InstanceBackend.Metal,
        _ => InstanceBackend.All,
    };

    private static BackendType BackendType(WebGpuSkiaBackend backend) => backend switch
    {
        WebGpuSkiaBackend.Vulkan => Native.BackendType.Vulkan,
        WebGpuSkiaBackend.Direct3D12 => Native.BackendType.D3D12,
        WebGpuSkiaBackend.Metal => Native.BackendType.Metal,
        _ => Native.BackendType.Undefined,
    };

    private static string Describe(string message) => string.IsNullOrEmpty(message) ? "no message" : message;
}
