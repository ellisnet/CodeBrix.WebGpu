using System;
using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;

namespace CodeBrix.WebGpu.Skia.Interop;

/// <summary>
/// Vulkan entry-point lookup over a raw <c>vkGetInstanceProcAddr</c> function pointer: the
/// <see cref="GRVkGetProcedureAddressDelegate" /> Skia loads Vulkan through, and the few direct calls the
/// library makes itself (<c>vkGetDeviceQueue</c>). Keep the instance alive as long as anything uses
/// <see cref="Delegate" />.
/// </summary>
internal sealed unsafe class VulkanProcedures
{
    private readonly delegate* unmanaged<IntPtr, byte*, IntPtr> getInstanceProcAddr;
    private readonly delegate* unmanaged<IntPtr, byte*, IntPtr> getDeviceProcAddr;

    /// <summary>Creates the lookup.</summary>
    /// <param name="getInstanceProcAddr">The <c>vkGetInstanceProcAddr</c> function pointer.</param>
    /// <param name="instance">The <c>VkInstance</c> the device belongs to.</param>
    public VulkanProcedures(IntPtr getInstanceProcAddr, IntPtr instance)
    {
        this.getInstanceProcAddr = (delegate* unmanaged<IntPtr, byte*, IntPtr>)getInstanceProcAddr;
        Instance = instance;
        getDeviceProcAddr = (delegate* unmanaged<IntPtr, byte*, IntPtr>)Lookup(instance, "vkGetDeviceProcAddr");
        Delegate = GetProcedureAddress;
    }

    /// <summary>The <c>VkInstance</c>.</summary>
    public IntPtr Instance { get; }

    /// <summary>The lookup as the delegate SkiaSharp's <see cref="GRVkBackendContext" /> takes. Rooted by this object.</summary>
    public GRVkGetProcedureAddressDelegate Delegate { get; }

    /// <summary>Looks an instance-level entry point up.</summary>
    /// <param name="instance">The instance (zero for global entry points).</param>
    /// <param name="name">The entry point's name.</param>
    /// <returns>The function pointer, or zero.</returns>
    public IntPtr Lookup(IntPtr instance, string name)
    {
        byte[] utf8 = Utf8(name);
        fixed (byte* text = utf8) return getInstanceProcAddr(instance, text);
    }

    /// <summary>
    /// Calls <c>vkGetDeviceQueue</c>. Only call it with a family and index the device was created with (anything
    /// else is invalid Vulkan usage).
    /// </summary>
    /// <param name="device">The <c>VkDevice</c>.</param>
    /// <param name="family">The queue family index.</param>
    /// <param name="index">The queue index within the family.</param>
    /// <returns>The <c>VkQueue</c>, or zero when the entry point cannot be found.</returns>
    public IntPtr GetDeviceQueue(IntPtr device, uint family, uint index)
    {
        IntPtr entry = GetProcedureAddress("vkGetDeviceQueue", Instance, device);
        if (entry == IntPtr.Zero) return IntPtr.Zero;
        IntPtr queue = IntPtr.Zero;
        ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)entry)(device, family, index, &queue);
        return queue;
    }

    private IntPtr GetProcedureAddress(string name, IntPtr instance, IntPtr device)
    {
        byte[] utf8 = Utf8(name);
        fixed (byte* text = utf8)
        {
            if (device != IntPtr.Zero && getDeviceProcAddr != null)
            {
                IntPtr entry = getDeviceProcAddr(device, text);
                if (entry != IntPtr.Zero) return entry;
            }

            return getInstanceProcAddr(instance, text);
        }
    }

    private static byte[] Utf8(string name)
    {
        byte[] utf8 = new byte[Encoding.UTF8.GetByteCount(name) + 1];
        Encoding.UTF8.GetBytes(name, 0, name.Length, utf8, 0);
        return utf8;
    }
}
