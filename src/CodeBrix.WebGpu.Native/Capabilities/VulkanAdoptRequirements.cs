using System;
using System.Collections.Generic;

namespace CodeBrix.WebGpu.Native;

/// <summary>
/// What an application must create its own <c>VkDevice</c> with so that
/// <see cref="WebGpu.CodebrixInstanceAdoptVulkanDevice" /> accepts it (<c>wgpuCodebrixVulkanGetAdoptRequirements</c>):
/// the device extensions wgpu requires for the physical device and feature set, and a ready-to-chain
/// <c>VkPhysicalDeviceFeatures2</c> with the feature structures wgpu enables. Owns that chain until disposed.
/// </summary>
public sealed unsafe class VulkanAdoptRequirements : IDisposable
{
    private CodebrixVulkanAdoptRequirements raw;

    private VulkanAdoptRequirements(CodebrixVulkanAdoptRequirements raw, List<string> extensions)
    {
        this.raw = raw;
        RequiredDeviceExtensions = extensions;
    }

    /// <summary>The device extensions to enable (you may enable more).</summary>
    public IReadOnlyList<string> RequiredDeviceExtensions { get; }

    /// <summary>
    /// The <c>VkPhysicalDeviceFeatures2*</c> (with its <c>pNext</c> chain) wgpu enables: put it into
    /// <c>VkDeviceCreateInfo::pNext</c> with <c>pEnabledFeatures</c> NULL, or copy it and add features of your own -
    /// never enable fewer, never write into it. Valid until this object is disposed; zero afterwards.
    /// </summary>
    public nint PhysicalDeviceFeatures2 => (nint)raw.PhysicalDeviceFeatures2;

    /// <summary>The physical device's <c>VkPhysicalDeviceProperties::apiVersion</c>.</summary>
    public uint PhysicalDeviceApiVersion => raw.PhysicalDeviceApiVersion;

    /// <summary>
    /// Asks the library. Fill <paramref name="descriptor" /> exactly as for the adopt call (instance, physical device,
    /// instance API version, <c>vkGetInstanceProcAddr</c>, enabled instance extensions, required features); the device,
    /// queue indices and device extensions are ignored here.
    /// </summary>
    /// <param name="descriptor">The Vulkan instance, physical device and requested features.</param>
    /// <returns>The requirements; dispose them when the device has been created.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="descriptor" /> is null.</exception>
    /// <exception cref="WgpuException">
    /// The library has no Vulkan backend, a handle is missing, a feature is unknown or unavailable, or wgpu cannot use the
    /// physical device (the reason goes to the log callback).
    /// </exception>
    public static VulkanAdoptRequirements Query(CodebrixVulkanAdoptDescriptor* descriptor)
    {
        if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
        CodeBrixWebGpuNative.EnsureLoaded();
        CodebrixVulkanAdoptRequirements found = default;
        if (WebGpu.CodebrixVulkanGetAdoptRequirements(descriptor, &found) != Status.Success)
        {
            throw new WgpuException("wgpuCodebrixVulkanGetAdoptRequirements failed (see the log callback for the reason).");
        }

        List<string> extensions = new List<string>((int)found.RequiredDeviceExtensionCount);
        for (nuint index = 0; index < found.RequiredDeviceExtensionCount; index++)
        {
            extensions.Add(WgpuString.ToManaged(found.RequiredDeviceExtensions[index].Name));
        }

        return new VulkanAdoptRequirements(found, extensions);
    }

    /// <summary>Releases the feature chain (<c>wgpuCodebrixVulkanAdoptRequirementsFreeMembers</c>).</summary>
    public void Dispose()
    {
        if (raw.PhysicalDeviceFeatures2 == null) return;
        WebGpu.CodebrixVulkanAdoptRequirementsFreeMembers(raw);
        raw = default;
    }
}
