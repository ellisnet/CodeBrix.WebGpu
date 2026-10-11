using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CodeBrix.WebGpu.Native;
using CodeBrix.WebGpu.Skia.Interop;
using SkiaSharp;

namespace CodeBrix.WebGpu.Skia.Backends;

/// <summary>Vulkan: Skia gets the <c>VkInstance</c>/<c>VkDevice</c>/<c>VkQueue</c> behind the WebGPU device and the <c>VkImage</c> behind each texture.</summary>
internal sealed unsafe class VulkanSkiaBridge : SkiaBridge
{
    private VulkanProcedures procedures;
    private GRVkExtensions extensions;
    private GRVkBackendContext backendContext;
    private void* features;

    /// <inheritdoc />
    public override WebGpuSkiaBackend Backend => WebGpuSkiaBackend.Vulkan;

    /// <inheritdoc />
    public override GRBackend SkiaBackend => GRBackend.Vulkan;

    /// <inheritdoc />
    public override GRContext CreateGrContext(Device device, Queue queue, out string reason)
    {
        CodebrixVulkanDeviceInfo info = default;
        if (Native.WebGpu.CodebrixDeviceGetVulkanInfo(device, &info) != Status.Success)
        {
            reason = "wgpuCodebrixDeviceGetVulkanInfo refused the device.";
            return null;
        }

        procedures = new VulkanProcedures((IntPtr)info.GetInstanceProcAddr, (IntPtr)info.Instance);
        string[] instanceExtensions = Names(info.EnabledInstanceExtensions, info.EnabledInstanceExtensionCount);
        string[] deviceExtensions = Names(info.EnabledDeviceExtensions, info.EnabledDeviceExtensionCount);
        extensions = GRVkExtensions.Create(procedures.Delegate, (IntPtr)info.Instance, (IntPtr)info.PhysicalDevice, instanceExtensions, deviceExtensions);

        // Skia must only use device features that are ENABLED on the device. The WebGPU engine does not report
        // which core features it enabled, so Skia is told "none": it then avoids every optional feature.
        features = NativeMemory.AllocZeroed(NativeConstants.VkPhysicalDeviceFeaturesSize);

        backendContext = new GRVkBackendContext
        {
            VkInstance = (IntPtr)info.Instance,
            VkPhysicalDevice = (IntPtr)info.PhysicalDevice,
            VkDevice = (IntPtr)info.Device,
            VkQueue = (IntPtr)info.Queue,
            GraphicsQueueIndex = info.QueueFamilyIndex,
            MaxAPIVersion = SkiaApiVersion(info.InstanceApiVersion),
            Extensions = extensions,
            VkPhysicalDeviceFeatures = (IntPtr)features,
            GetProcedureAddress = procedures.Delegate,
            ProtectedContext = false,
        };

        GRContext context = GRContext.CreateVulkan(backendContext);
        reason = context == null ? "GRContext.CreateVulkan returned null for the WebGPU device's Vulkan objects (the SkiaSharp native may lack Vulkan support)." : null;
        return context;
    }

    /// <inheritdoc />
    public override GRBackendTexture WrapTexture(Texture texture, int width, int height, TextureUsage usage, SkiaTextureRole role, out string reason)
    {
        CodebrixVulkanTextureInfo info = default;
        if (Native.WebGpu.CodebrixTextureGetVulkanInfo(texture, &info) != Status.Success)
        {
            reason = "wgpuCodebrixTextureGetVulkanInfo refused the texture.";
            return null;
        }

        GRVkImageInfo image = new GRVkImageInfo
        {
            Image = (ulong)(IntPtr)info.Image,
            ImageTiling = NativeConstants.VkImageTilingOptimal,
            ImageLayout = role == SkiaTextureRole.RenderTarget ? NativeConstants.VkImageLayoutColorAttachmentOptimal : NativeConstants.VkImageLayoutShaderReadOnlyOptimal,
            Format = info.Format,
            ImageUsageFlags = VulkanUsage(usage),
            SampleCount = Math.Max(1u, info.SampleCount),
            LevelCount = 1,
            CurrentQueueFamily = NativeConstants.VkQueueFamilyIgnored,
            SharingMode = NativeConstants.VkSharingModeExclusive,
            Protected = false,
        };
        GRBackendTexture backendTexture = new GRBackendTexture(width, height, image);
        reason = backendTexture.IsValid ? null : "SkiaSharp rejected the Vulkan image description.";
        return backendTexture.IsValid ? backendTexture : null;
    }

    /// <inheritdoc />
    public override Texture ImportTexture(Device device, IntPtr nativeTexture, TextureDescriptor* descriptor, out string reason)
    {
        Texture texture = Native.WebGpu.CodebrixDeviceCreateTextureFromVulkanImage(device, (void*)nativeTexture, descriptor, CodebrixTextureState.Undefined);
        reason = texture.IsNull ? "wgpuCodebrixDeviceCreateTextureFromVulkanImage refused the VkImage (it must be on this device and match the size, format and usage given)." : null;
        return texture;
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        backendContext?.Dispose();
        backendContext = null;
        extensions?.Dispose();
        extensions = null;
        if (features != null) NativeMemory.Free(features);
        features = null;
        procedures = null;
    }

    /// <summary>Maps WebGPU texture usages to <c>VkImageUsageFlags</c> the way the WebGPU engine creates images.</summary>
    /// <param name="usage">The WebGPU usages.</param>
    /// <returns>The Vulkan usage flags.</returns>
    internal static uint VulkanUsage(TextureUsage usage)
    {
        uint flags = 0;
        if ((usage & TextureUsage.CopySrc) != 0) flags |= NativeConstants.VkImageUsageTransferSrc;
        if ((usage & TextureUsage.CopyDst) != 0) flags |= NativeConstants.VkImageUsageTransferDst;
        if ((usage & TextureUsage.TextureBinding) != 0) flags |= NativeConstants.VkImageUsageSampled;
        if ((usage & TextureUsage.StorageBinding) != 0) flags |= NativeConstants.VkImageUsageStorage;
        if ((usage & TextureUsage.RenderAttachment) != 0) flags |= NativeConstants.VkImageUsageColorAttachment;
        return flags;
    }

    /// <summary>
    /// The highest Vulkan version Skia is allowed to use: 1.1 (Skia's minimum), never more. The version the native
    /// reports for the instance can be newer than the version the device was created for, and Skia then asks for
    /// core entry points the device does not have (seen on Mesa: 1.4 reported, vkTransitionImageLayout missing), and
    /// refuses to create the context.
    /// </summary>
    /// <param name="reported">The instance API version the native reports.</param>
    /// <returns>The version to give Skia.</returns>
    internal static uint SkiaApiVersion(uint reported) => Math.Min(reported, NativeConstants.VkApiVersion11);

    private static string[] Names(CodebrixName* names, nuint count)
    {
        List<string> result = new List<string>();
        for (nuint index = 0; index < count; index++) result.Add(WgpuString.ToManaged(names[index].Name));
        return result.ToArray();
    }
}
