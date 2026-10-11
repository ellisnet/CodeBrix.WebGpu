namespace CodeBrix.WebGpu.Skia.Interop;

/// <summary>The few Vulkan, Direct3D 12 and Metal constants the library passes to SkiaSharp.</summary>
internal static class NativeConstants
{
    /// <summary><c>VK_API_VERSION_1_0</c>.</summary>
    public const uint VkApiVersion10 = 1u << 22;

    /// <summary><c>VK_API_VERSION_1_1</c>.</summary>
    public const uint VkApiVersion11 = (1u << 22) | (1u << 12);

    /// <summary><c>VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL</c>.</summary>
    public const uint VkImageLayoutColorAttachmentOptimal = 2;

    /// <summary><c>VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL</c>.</summary>
    public const uint VkImageLayoutShaderReadOnlyOptimal = 5;

    /// <summary><c>VK_IMAGE_TILING_OPTIMAL</c>.</summary>
    public const uint VkImageTilingOptimal = 0;

    /// <summary><c>VK_SHARING_MODE_EXCLUSIVE</c>.</summary>
    public const uint VkSharingModeExclusive = 0;

    /// <summary><c>VK_QUEUE_FAMILY_IGNORED</c>.</summary>
    public const uint VkQueueFamilyIgnored = 0xFFFFFFFF;

    /// <summary><c>VK_IMAGE_USAGE_TRANSFER_SRC_BIT</c>.</summary>
    public const uint VkImageUsageTransferSrc = 0x1;

    /// <summary><c>VK_IMAGE_USAGE_TRANSFER_DST_BIT</c>.</summary>
    public const uint VkImageUsageTransferDst = 0x2;

    /// <summary><c>VK_IMAGE_USAGE_SAMPLED_BIT</c>.</summary>
    public const uint VkImageUsageSampled = 0x4;

    /// <summary><c>VK_IMAGE_USAGE_STORAGE_BIT</c>.</summary>
    public const uint VkImageUsageStorage = 0x8;

    /// <summary><c>VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT</c>.</summary>
    public const uint VkImageUsageColorAttachment = 0x10;

    /// <summary><c>sizeof(VkPhysicalDeviceFeatures)</c>: 55 <c>VkBool32</c> members.</summary>
    public const int VkPhysicalDeviceFeaturesSize = 55 * 4;

    /// <summary><c>D3D12_RESOURCE_STATE_RENDER_TARGET</c>.</summary>
    public const uint D3d12ResourceStateRenderTarget = 0x4;

    /// <summary><c>D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE</c>.</summary>
    public const uint D3d12ResourceStatePixelShaderResource = 0x80;
}
