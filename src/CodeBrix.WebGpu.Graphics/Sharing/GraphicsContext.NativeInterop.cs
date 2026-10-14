using System;
using System.Collections.Generic;
using CodeBrix.WebGpu.Graphics.Interop;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// The native-device interop of <see cref="GraphicsContext" />: the Vulkan / Direct3D 12 / Metal objects behind a
/// context, contexts over devices the application created, and textures over images the application owns.
/// </summary>
public sealed unsafe partial class GraphicsContext
{
    /// <summary>
    /// True when the context adopted a native device the application created
    /// (<see cref="AdoptVulkanDevice" />, <see cref="AdoptMetalDevice" />). Such a device has no uncaptured-error
    /// handler of this library: object creation is still checked, but an uncaptured error ends the process.
    /// </summary>
    public bool IsAdopted { get; private set; }

    /// <summary>The MTLDevice behind the context on the Metal backend (borrowed), or <see cref="IntPtr.Zero" />.</summary>
    public IntPtr MetalDevice
    {
        get
        {
            ThrowIfDisposed();
            return (IntPtr)Native.WebGpu.DeviceGetNativeMetalDevice(Device);
        }
    }

    /// <summary>
    /// The MTLCommandQueue behind the context on the Metal backend. Always <see cref="IntPtr.Zero" /> on this engine,
    /// whose Metal layer no longer exposes the queue.
    /// </summary>
    public IntPtr MetalCommandQueue
    {
        get
        {
            ThrowIfDisposed();
            return (IntPtr)Native.WebGpu.QueueGetNativeMetalCommandQueue(Queue);
        }
    }

    /// <summary>The Vulkan objects behind the context (instance, physical device, device, queue, extensions).</summary>
    /// <param name="info">The objects, or null.</param>
    /// <returns>False when the device is not on the Vulkan backend.</returns>
    public bool TryGetVulkanInfo(out VulkanDeviceInfo info)
    {
        ThrowIfDisposed();
        CodebrixVulkanDeviceInfo native = default;
        if (Native.WebGpu.CodebrixDeviceGetVulkanInfo(Device, &native) != Status.Success)
        {
            info = null;
            return false;
        }

        info = new VulkanDeviceInfo
        {
            Instance = (IntPtr)native.Instance,
            PhysicalDevice = (IntPtr)native.PhysicalDevice,
            Device = (IntPtr)native.Device,
            Queue = (IntPtr)native.Queue,
            QueueFamilyIndex = native.QueueFamilyIndex,
            QueueIndex = native.QueueIndex,
            InstanceApiVersion = native.InstanceApiVersion,
            GetInstanceProcAddr = (IntPtr)native.GetInstanceProcAddr,
            EnabledInstanceExtensions = Names(native.EnabledInstanceExtensions, native.EnabledInstanceExtensionCount),
            EnabledDeviceExtensions = Names(native.EnabledDeviceExtensions, native.EnabledDeviceExtensionCount),
        };
        return true;
    }

    /// <summary>The Direct3D 12 objects behind the context (adapter, device, queue).</summary>
    /// <param name="info">The objects, or null.</param>
    /// <returns>False when the device is not on the Direct3D 12 backend.</returns>
    public bool TryGetD3D12Info(out D3D12DeviceInfo info)
    {
        ThrowIfDisposed();
        CodebrixD3d12DeviceInfo native = default;
        if (Native.WebGpu.CodebrixDeviceGetD3d12Info(Device, &native) != Status.Success)
        {
            info = null;
            return false;
        }

        info = new D3D12DeviceInfo { Adapter = (IntPtr)native.Adapter, Device = (IntPtr)native.Device, Queue = (IntPtr)native.Queue };
        return true;
    }

    /// <summary>
    /// Wraps a VkImage the application owns as a texture of this context (Vulkan backend). The image must have been
    /// created on this context's VkDevice with the usages <paramref name="usage" /> implies; releasing the texture never
    /// frees the image. Its contents are treated as undefined at first use on this engine.
    /// </summary>
    /// <param name="image">The VkImage handle.</param>
    /// <param name="width">The width in texels.</param>
    /// <param name="height">The height in texels.</param>
    /// <param name="format">The WebGPU format matching the image's VkFormat.</param>
    /// <param name="usage">The WebGPU usages the image supports.</param>
    /// <param name="mipLevelCount">The mip levels.</param>
    /// <param name="sampleCount">The sample count.</param>
    /// <param name="initialState">The image's current state (only <see cref="CodebrixTextureState.Undefined" /> is exact on this engine).</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The texture (<see cref="GpuTexture.IsExternal" /> is true).</returns>
    /// <exception cref="GraphicsException">The context is not on Vulkan, or the engine rejected the image.</exception>
    public GpuTexture CreateTextureFromVulkanImage(IntPtr image, int width, int height, TextureFormat format, TextureUsage usage, int mipLevelCount = 1, int sampleCount = 1, CodebrixTextureState initialState = CodebrixTextureState.Undefined, string label = null) =>
        CreateExternalTexture(ExternalKind.Vulkan, image, width, height, format, usage, mipLevelCount, sampleCount, initialState, label);

    /// <summary>
    /// Wraps an ID3D12Resource the application owns as a texture of this context (Direct3D 12 backend); see
    /// <see cref="CreateTextureFromVulkanImage" /> for the rules.
    /// </summary>
    /// <param name="resource">The ID3D12Resource.</param>
    /// <param name="width">The width in texels.</param>
    /// <param name="height">The height in texels.</param>
    /// <param name="format">The WebGPU format matching the resource's DXGI format.</param>
    /// <param name="usage">The WebGPU usages the resource supports.</param>
    /// <param name="mipLevelCount">The mip levels.</param>
    /// <param name="sampleCount">The sample count.</param>
    /// <param name="initialState">The resource's current state (only <see cref="CodebrixTextureState.Undefined" /> is exact on this engine).</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The texture.</returns>
    /// <exception cref="GraphicsException">The context is not on Direct3D 12, or the engine rejected the resource.</exception>
    public GpuTexture CreateTextureFromD3D12Resource(IntPtr resource, int width, int height, TextureFormat format, TextureUsage usage, int mipLevelCount = 1, int sampleCount = 1, CodebrixTextureState initialState = CodebrixTextureState.Undefined, string label = null) =>
        CreateExternalTexture(ExternalKind.D3D12, resource, width, height, format, usage, mipLevelCount, sampleCount, initialState, label);

    /// <summary>
    /// Wraps an MTLTexture the application owns as a texture of this context (Metal backend; Metal has no explicit
    /// states, so none is passed); see <see cref="CreateTextureFromVulkanImage" /> for the rules.
    /// </summary>
    /// <param name="texture">The MTLTexture.</param>
    /// <param name="width">The width in texels.</param>
    /// <param name="height">The height in texels.</param>
    /// <param name="format">The WebGPU format matching the texture's pixel format.</param>
    /// <param name="usage">The WebGPU usages the texture supports.</param>
    /// <param name="mipLevelCount">The mip levels.</param>
    /// <param name="sampleCount">The sample count.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The texture.</returns>
    /// <exception cref="GraphicsException">The context is not on Metal, or the engine rejected the texture.</exception>
    public GpuTexture CreateTextureFromMetalTexture(IntPtr texture, int width, int height, TextureFormat format, TextureUsage usage, int mipLevelCount = 1, int sampleCount = 1, string label = null) =>
        CreateExternalTexture(ExternalKind.Metal, texture, width, height, format, usage, mipLevelCount, sampleCount, CodebrixTextureState.Undefined, label);

    /// <summary>
    /// Creates a context over a Vulkan device the application created (for example a game engine's or a UI toolkit's),
    /// so both draw with the same VkDevice. The application keeps ownership of every Vulkan object; disposing the
    /// context releases only the WebGPU objects. The device must have the extensions and features the engine needs
    /// (the reason says which are missing).
    /// </summary>
    /// <param name="device">The Vulkan objects (from the application, or from another context's <see cref="TryGetVulkanInfo" />).</param>
    /// <param name="requiredFeatures">Standard and native features to enable (as <see cref="FeatureName" /> values), or null.</param>
    /// <returns>The context (<see cref="IsAdopted" /> is true).</returns>
    /// <exception cref="GraphicsException">The engine refused the device (the message says why).</exception>
    public static GraphicsContext AdoptVulkanDevice(VulkanDeviceInfo device, IReadOnlyList<FeatureName> requiredFeatures = null)
    {
        if (!TryAdoptVulkanDevice(device, requiredFeatures, out GraphicsContext context, out string reason)) throw new GraphicsException(reason);
        return context;
    }

    /// <summary>Creates a context over an application's Vulkan device (see <see cref="AdoptVulkanDevice" />), reporting failure instead of throwing.</summary>
    /// <param name="device">The Vulkan objects.</param>
    /// <param name="requiredFeatures">Features to enable, or null.</param>
    /// <param name="context">The context, or null.</param>
    /// <param name="reason">Why the device was refused, or null.</param>
    /// <returns>True when the context was created.</returns>
    public static bool TryAdoptVulkanDevice(VulkanDeviceInfo device, IReadOnlyList<FeatureName> requiredFeatures, out GraphicsContext context, out string reason)
    {
        ArgumentNullException.ThrowIfNull(device);
        context = null;
        if (!TryLoadNative(out reason)) return false;
        if (device.Instance == IntPtr.Zero || device.PhysicalDevice == IntPtr.Zero || device.Device == IntPtr.Zero || device.GetInstanceProcAddr == IntPtr.Zero)
        {
            reason = "Instance, PhysicalDevice, Device and GetInstanceProcAddr are required.";
            return false;
        }

        using Utf8Text text = new Utf8Text();
        CodebrixName* instanceExtensions = NamesToNative(text, device.EnabledInstanceExtensions, out int instanceExtensionCount);
        CodebrixName* deviceExtensions = NamesToNative(text, device.EnabledDeviceExtensions, out int deviceExtensionCount);
        FeatureName* features = FeaturesToNative(text, requiredFeatures, out int featureCount);
        CodebrixVulkanAdoptDescriptor descriptor = new CodebrixVulkanAdoptDescriptor
        {
            Instance = (void*)device.Instance,
            PhysicalDevice = (void*)device.PhysicalDevice,
            Device = (void*)device.Device,
            QueueFamilyIndex = device.QueueFamilyIndex,
            QueueIndex = device.QueueIndex,
            InstanceApiVersion = device.InstanceApiVersion,
            GetInstanceProcAddr = (void*)device.GetInstanceProcAddr,
            EnabledInstanceExtensionCount = (nuint)instanceExtensionCount,
            EnabledInstanceExtensions = instanceExtensions,
            EnabledDeviceExtensionCount = (nuint)deviceExtensionCount,
            EnabledDeviceExtensions = deviceExtensions,
            RequiredFeatureCount = (nuint)featureCount,
            RequiredFeatures = features,
        };

        if (!CreateAdoptInstance(GraphicsBackend.Vulkan, "Vulkan", out Instance instance, out reason)) return false;
        Adapter adapter = Adapter.Null;
        Device created = Device.Null;
        Status status = Native.WebGpu.CodebrixInstanceAdoptVulkanDevice(instance, &descriptor, &adapter, &created);
        return FinishAdopt(instance, adapter, created, status, "Vulkan", out context, out reason);
    }

    /// <summary>
    /// Creates a context over a Metal device the application created (it gets its own command queue on that device).
    /// See <see cref="AdoptVulkanDevice" /> for the ownership rules.
    /// </summary>
    /// <param name="metalDevice">The MTLDevice.</param>
    /// <param name="requiredFeatures">Features to enable, or null.</param>
    /// <returns>The context.</returns>
    /// <exception cref="GraphicsException">The engine refused the device, or the platform has no Metal.</exception>
    public static GraphicsContext AdoptMetalDevice(IntPtr metalDevice, IReadOnlyList<FeatureName> requiredFeatures = null)
    {
        if (!TryAdoptMetalDevice(metalDevice, requiredFeatures, out GraphicsContext context, out string reason)) throw new GraphicsException(reason);
        return context;
    }

    /// <summary>Creates a context over an application's Metal device, reporting failure instead of throwing.</summary>
    /// <param name="metalDevice">The MTLDevice.</param>
    /// <param name="requiredFeatures">Features to enable, or null.</param>
    /// <param name="context">The context, or null.</param>
    /// <param name="reason">Why the device was refused, or null.</param>
    /// <returns>True when the context was created.</returns>
    public static bool TryAdoptMetalDevice(IntPtr metalDevice, IReadOnlyList<FeatureName> requiredFeatures, out GraphicsContext context, out string reason)
    {
        context = null;
        if (!TryLoadNative(out reason)) return false;
        if (metalDevice == IntPtr.Zero)
        {
            reason = "The MTLDevice is null.";
            return false;
        }

        using Utf8Text text = new Utf8Text();
        FeatureName* features = FeaturesToNative(text, requiredFeatures, out int featureCount);
        CodebrixMetalAdoptDescriptor descriptor = new CodebrixMetalAdoptDescriptor { Device = (void*)metalDevice, RequiredFeatureCount = (nuint)featureCount, RequiredFeatures = features };
        if (!CreateAdoptInstance(GraphicsBackend.Metal, "Metal", out Instance instance, out reason)) return false;
        Adapter adapter = Adapter.Null;
        Device created = Device.Null;
        Status status = Native.WebGpu.CodebrixInstanceAdoptMetalDevice(instance, &descriptor, &adapter, &created);
        return FinishAdopt(instance, adapter, created, status, "Metal", out context, out reason);
    }

    /// <summary>
    /// Asks the engine to adopt a Direct3D 12 device the application created. ALWAYS returns false on this engine,
    /// whose Direct3D 12 layer cannot wrap an existing ID3D12Device; the reason says so. Create the context with
    /// <see cref="Create(GraphicsContextOptions)" /> on <see cref="GraphicsBackend.Direct3D12" /> instead and share its
    /// device the other way round (<see cref="TryGetD3D12Info" />).
    /// </summary>
    /// <param name="device">The Direct3D 12 objects (adapter, device, queue).</param>
    /// <param name="requiredFeatures">Features to enable, or null.</param>
    /// <param name="context">Always null on this engine.</param>
    /// <param name="reason">Why the device was refused.</param>
    /// <returns>False on this engine.</returns>
    public static bool TryAdoptD3D12Device(D3D12DeviceInfo device, IReadOnlyList<FeatureName> requiredFeatures, out GraphicsContext context, out string reason)
    {
        ArgumentNullException.ThrowIfNull(device);
        context = null;
        if (!TryLoadNative(out reason)) return false;
        using Utf8Text text = new Utf8Text();
        FeatureName* features = FeaturesToNative(text, requiredFeatures, out int featureCount);
        CodebrixD3d12AdoptDescriptor descriptor = new CodebrixD3d12AdoptDescriptor
        {
            Adapter = (void*)device.Adapter,
            Device = (void*)device.Device,
            Queue = (void*)device.Queue,
            RequiredFeatureCount = (nuint)featureCount,
            RequiredFeatures = features,
        };
        if (!CreateAdoptInstance(GraphicsBackend.Direct3D12, "Direct3D 12", out Instance instance, out reason)) return false;
        Adapter adapter = Adapter.Null;
        Device created = Device.Null;
        Status status = Native.WebGpu.CodebrixInstanceAdoptD3d12Device(instance, &descriptor, &adapter, &created);
        bool adopted = FinishAdopt(instance, adapter, created, status, "Direct3D 12", out context, out reason);
        if (!adopted) reason = "This engine cannot adopt an existing Direct3D 12 device (its Direct3D 12 layer has no way to wrap one); create the context on GraphicsBackend.Direct3D12 and share it with TryGetD3D12Info instead. (" + reason + ")";
        return adopted;
    }

    private enum ExternalKind
    {
        Vulkan,
        D3D12,
        Metal,
    }

    private static bool CreateAdoptInstance(GraphicsBackend backend, string name, out Instance instance, out string reason)
    {
        InstanceExtras extras = new InstanceExtras { Backends = InstanceBackends(backend) };
        extras.Chain.SType = InstanceExtras.StructType;
        InstanceDescriptor instanceDescriptor = new InstanceDescriptor { NextInChain = &extras.Chain };
        instance = Native.WebGpu.CreateInstance(&instanceDescriptor);
        reason = instance.IsNull ? "wgpuCreateInstance returned null for the " + name + " backend." : null;
        return !instance.IsNull;
    }

    private static bool FinishAdopt(Instance instance, Adapter adapter, Device device, Status status, string name, out GraphicsContext context, out string reason)
    {
        context = null;
        GraphicsContext candidate = new GraphicsContext(false) { Instance = instance, IsAdopted = true, Adapter = adapter };
        if (status != Status.Success || device.IsNull)
        {
            if (!device.IsNull) Native.WebGpu.DeviceRelease(device);
            candidate.Dispose();
            reason = "The engine refused the " + name + " device (" + status + "): the device lacks extensions or features the engine requires, the objects do not belong together, or the platform has no " + name + " backend. GraphicsDiagnostics.SetLogCallback receives the details.";
            return false;
        }

        candidate.Attach(device, Native.WebGpu.DeviceGetQueue(device));
        context = candidate;
        reason = null;
        return true;
    }

    private GpuTexture CreateExternalTexture(ExternalKind kind, IntPtr image, int width, int height, TextureFormat format, TextureUsage usage, int mipLevelCount, int sampleCount, CodebrixTextureState initialState, string label)
    {
        ThrowIfDisposed();
        if (image == IntPtr.Zero) throw new ArgumentException("The native image handle is null.", nameof(image));
        if (width < 1) throw new ArgumentOutOfRangeException(nameof(width));
        if (height < 1) throw new ArgumentOutOfRangeException(nameof(height));
        if (mipLevelCount < 1) throw new ArgumentOutOfRangeException(nameof(mipLevelCount));
        if (sampleCount < 1) throw new ArgumentOutOfRangeException(nameof(sampleCount));
        using Utf8Text text = new Utf8Text();
        TextureDescriptor descriptor = new TextureDescriptor
        {
            Label = text.View(label),
            Usage = usage,
            Dimension = TextureDimension.Dimension2D,
            Size = new Extent3D { Width = (uint)width, Height = (uint)height, DepthOrArrayLayers = 1 },
            Format = format,
            MipLevelCount = (uint)mipLevelCount,
            SampleCount = (uint)sampleCount,
        };
        BeginGuard();
        Texture texture = kind switch
        {
            ExternalKind.Vulkan => Native.WebGpu.CodebrixDeviceCreateTextureFromVulkanImage(Device, (void*)image, &descriptor, initialState),
            ExternalKind.D3D12 => Native.WebGpu.CodebrixDeviceCreateTextureFromD3d12Resource(Device, (void*)image, &descriptor, initialState),
            _ => Native.WebGpu.CodebrixDeviceCreateTextureFromMetalTexture(Device, (void*)image, &descriptor, initialState),
        };
        TextureView view = texture.IsNull ? TextureView.Null : Native.WebGpu.TextureCreateView(texture, null);
        string error = EndGuard();
        if (error != null || texture.IsNull || view.IsNull)
        {
            if (!view.IsNull) Native.WebGpu.TextureViewRelease(view);
            if (!texture.IsNull) Native.WebGpu.TextureRelease(texture);
            throw new GraphicsException("The " + kind + " image could not be wrapped as a texture" + LabelText(label) + ": " + (error ?? "the engine returned no texture (wrong backend, or an invalid descriptor; GraphicsDiagnostics.SetLogCallback receives the details)."));
        }

        return new GpuTexture(this, texture, view, width, height, format, mipLevelCount, usage, false, label) { IsExternal = true };
    }

    private static IReadOnlyList<string> Names(CodebrixName* names, nuint count)
    {
        string[] result = new string[(int)count];
        for (int index = 0; index < result.Length; index++) result[index] = WgpuString.ToManaged(names[index].Name) ?? string.Empty;
        return result;
    }

    private static CodebrixName* NamesToNative(Utf8Text text, IReadOnlyList<string> names, out int count)
    {
        count = names?.Count ?? 0;
        if (count == 0) return null;
        CodebrixName* result = (CodebrixName*)text.Allocate(sizeof(CodebrixName) * count);
        for (int index = 0; index < count; index++) result[index] = new CodebrixName { Name = text.View(names[index] ?? string.Empty) };
        return result;
    }

    private static FeatureName* FeaturesToNative(Utf8Text text, IReadOnlyList<FeatureName> features, out int count)
    {
        count = features?.Count ?? 0;
        if (count == 0) return null;
        FeatureName* result = (FeatureName*)text.Allocate(sizeof(FeatureName) * count);
        for (int index = 0; index < count; index++) result[index] = features[index];
        return result;
    }
}
