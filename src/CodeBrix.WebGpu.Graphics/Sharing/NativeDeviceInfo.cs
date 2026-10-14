using System;
using System.Collections.Generic;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// The Vulkan objects behind a device: what <see cref="GraphicsContext.TryGetVulkanInfo" /> reports for a context on
/// the Vulkan backend, and what <see cref="GraphicsContext.AdoptVulkanDevice" /> needs to wrap a Vulkan device the
/// application created. Handles are raw Vulkan handles (dispatchable ones as pointers, VkImage-style ones as 64-bit
/// values in an <see cref="IntPtr" />).
/// </summary>
public sealed class VulkanDeviceInfo
{
    /// <summary>The VkInstance.</summary>
    public IntPtr Instance { get; init; }

    /// <summary>The VkPhysicalDevice.</summary>
    public IntPtr PhysicalDevice { get; init; }

    /// <summary>The VkDevice.</summary>
    public IntPtr Device { get; init; }

    /// <summary>The VkQueue WebGPU submits to (reported only; adoption picks the queue by family and index).</summary>
    public IntPtr Queue { get; init; }

    /// <summary>The queue family index.</summary>
    public uint QueueFamilyIndex { get; init; }

    /// <summary>The queue index inside the family.</summary>
    public uint QueueIndex { get; init; }

    /// <summary>The Vulkan API version the instance was created with (VK_MAKE_API_VERSION encoding).</summary>
    public uint InstanceApiVersion { get; init; }

    /// <summary>The application's <c>vkGetInstanceProcAddr</c> (adoption loads every Vulkan function through it).</summary>
    public IntPtr GetInstanceProcAddr { get; init; }

    /// <summary>The instance extensions the VkInstance was created with.</summary>
    public IReadOnlyList<string> EnabledInstanceExtensions { get; init; } = Array.Empty<string>();

    /// <summary>The device extensions the VkDevice was created with.</summary>
    public IReadOnlyList<string> EnabledDeviceExtensions { get; init; } = Array.Empty<string>();

    /// <summary>Describes the handles.</summary>
    /// <returns>For example <c>VkDevice 0x5581... family 0 queue 0</c>.</returns>
    public override string ToString() => "VkDevice 0x" + Device.ToString("X") + " family " + QueueFamilyIndex + " queue " + QueueIndex;
}

/// <summary>The Direct3D 12 objects behind a device (<see cref="GraphicsContext.TryGetD3D12Info" />).</summary>
public sealed class D3D12DeviceInfo
{
    /// <summary>The IDXGIAdapter (borrowed).</summary>
    public IntPtr Adapter { get; init; }

    /// <summary>The ID3D12Device (borrowed).</summary>
    public IntPtr Device { get; init; }

    /// <summary>The ID3D12CommandQueue (borrowed).</summary>
    public IntPtr Queue { get; init; }

    /// <summary>Describes the handles.</summary>
    /// <returns>For example <c>ID3D12Device 0x...</c>.</returns>
    public override string ToString() => "ID3D12Device 0x" + Device.ToString("X");
}

/// <summary>A texture's Vulkan image (<see cref="GpuTexture.TryGetVulkanInfo" />).</summary>
public readonly struct VulkanTextureInfo
{
    /// <summary>Creates the info.</summary>
    /// <param name="image">The VkImage.</param>
    /// <param name="format">The VkFormat value.</param>
    /// <param name="sampleCount">The sample count.</param>
    public VulkanTextureInfo(IntPtr image, uint format, uint sampleCount)
    {
        Image = image;
        Format = format;
        SampleCount = sampleCount;
    }

    /// <summary>The VkImage (owned by the texture).</summary>
    public IntPtr Image { get; }

    /// <summary>The VkFormat value.</summary>
    public uint Format { get; }

    /// <summary>The sample count.</summary>
    public uint SampleCount { get; }
}

/// <summary>A texture's Direct3D 12 resource (<see cref="GpuTexture.TryGetD3D12Info" />).</summary>
public readonly struct D3D12TextureInfo
{
    /// <summary>Creates the info.</summary>
    /// <param name="resource">The ID3D12Resource.</param>
    /// <param name="format">The DXGI_FORMAT value.</param>
    /// <param name="sampleCount">The sample count.</param>
    public D3D12TextureInfo(IntPtr resource, uint format, uint sampleCount)
    {
        Resource = resource;
        Format = format;
        SampleCount = sampleCount;
    }

    /// <summary>The ID3D12Resource (borrowed).</summary>
    public IntPtr Resource { get; }

    /// <summary>The DXGI_FORMAT value.</summary>
    public uint Format { get; }

    /// <summary>The sample count.</summary>
    public uint SampleCount { get; }
}
