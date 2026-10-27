using System;
using System.Runtime.InteropServices;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Native.Tests;

/// <summary>
/// <c>wgpuQueueGetNativeMetalCommandQueue</c> (wgpu-native-tools/patches/0012 + 0013): on Metal it returns the
/// <c>MTLCommandQueue</c> the WebGPU queue submits on, owned by the device's own <c>MTLDevice</c> - the queue Skia's
/// Metal <c>GRContext</c> needs to share the device. The unpatched engine returns NULL there. Off Metal it is NULL.
/// The GPU tests skip, with the reason, when the machine has no adapter.
/// </summary>
public unsafe class MetalCommandQueueTests : IClassFixture<GpuFixture>
{
    private readonly GpuSession gpu;

    public MetalCommandQueueTests(GpuFixture fixture)
    {
        gpu = fixture.Session;
    }

    [Fact]
    public void QueueGetNativeMetalCommandQueue_is_a_Metal_function_in_the_proc_status_table() =>
        CodeBrixWebGpuNative.GetProcStatus("wgpuQueueGetNativeMetalCommandQueue")
            .Should().Be(OperatingSystem.IsMacOS() ? CodebrixProcStatus.Implemented : CodebrixProcStatus.NotAvailableOnBackend);

    [Fact]
    public void the_queue_is_an_MTLCommandQueue_of_the_devices_MTLDevice()
    {
        //Arrange
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Metal exists on macOS only.");
        Assert.SkipWhen(gpu.SkipReason != null, gpu.SkipReason ?? string.Empty);
        void* metalDevice = WebGpu.DeviceGetNativeMetalDevice(gpu.Device);

        //Act
        void* metalQueue = WebGpu.QueueGetNativeMetalCommandQueue(gpu.Queue);
        void* again = WebGpu.QueueGetNativeMetalCommandQueue(gpu.Queue);

        //Assert
        ((IntPtr)metalDevice).Should().NotBe(IntPtr.Zero);
        ((IntPtr)metalQueue).Should().NotBe(IntPtr.Zero, "the engine must expose the queue it submits on (patches/0012-0013)");
        ((IntPtr)again).Should().Be((IntPtr)metalQueue, "the queue is the same object on every call");
        ObjectiveC.Send((IntPtr)metalQueue, "device").Should().Be((IntPtr)metalDevice, "the queue belongs to the device's MTLDevice");
        ObjectiveC.RespondsTo((IntPtr)metalQueue, "commandBuffer").Should().BeTrue("it is an MTLCommandQueue");
    }

    [Fact]
    public void QueueGetNativeMetalCommandQueue_is_null_off_Metal()
    {
        //Arrange
        Assert.SkipWhen(OperatingSystem.IsMacOS(), "The device is on Metal here.");
        Assert.SkipWhen(gpu.SkipReason != null, gpu.SkipReason ?? string.Empty);

        //Act
        void* metalQueue = WebGpu.QueueGetNativeMetalCommandQueue(gpu.Queue);

        //Assert
        ((IntPtr)metalQueue).Should().Be(IntPtr.Zero);
    }

    /// <summary>Two Objective-C messages through the runtime, without a binding.</summary>
    private static class ObjectiveC
    {
        private static readonly IntPtr Library = NativeLibrary.Load("/usr/lib/libobjc.A.dylib");
        private static readonly delegate* unmanaged<IntPtr, IntPtr, IntPtr> MsgSend =
            (delegate* unmanaged<IntPtr, IntPtr, IntPtr>)NativeLibrary.GetExport(Library, "objc_msgSend");
        private static readonly delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte> MsgSendBool =
            (delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte>)NativeLibrary.GetExport(Library, "objc_msgSend");
        private static readonly delegate* unmanaged<byte*, IntPtr> RegisterName =
            (delegate* unmanaged<byte*, IntPtr>)NativeLibrary.GetExport(Library, "sel_registerName");

        public static IntPtr Send(IntPtr receiver, string selector) => MsgSend(receiver, Selector(selector));

        public static bool RespondsTo(IntPtr receiver, string selector) =>
            MsgSendBool(receiver, Selector("respondsToSelector:"), Selector(selector)) != 0;

        private static IntPtr Selector(string name)
        {
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(name + "\0");
            fixed (byte* bytes = utf8) return RegisterName(bytes);
        }
    }
}
