using System;
using System.Runtime.InteropServices;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Native.Tests;

/// <summary>
/// <c>SurfaceGetCapabilities</c> reports the usages the backend offers with their own bits
/// (wgpu-native-tools/patches/0008: CopyDst was reported as CopySrc, StorageBinding and TransientAttachment were
/// dropped). Uses a real (unmapped) Xlib window: Linux with an X display only, skipped elsewhere.
/// </summary>
public unsafe class SurfaceCapabilitiesTests : IClassFixture<GpuFixture>
{
    private readonly GpuSession gpu;

    public SurfaceCapabilitiesTests(GpuFixture fixture)
    {
        gpu = fixture.Session;
    }

    [Fact]
    public void SurfaceGetCapabilities_on_an_X11_window_reports_CopyDst_as_CopyDst()
    {
        //Arrange
        Assert.SkipWhen(gpu.SkipReason != null, gpu.SkipReason ?? string.Empty);
        Assert.SkipUnless(OperatingSystem.IsLinux() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")), "Needs Linux with an X display (DISPLAY).");
        IntPtr display = Xlib.OpenDisplay();
        Assert.SkipWhen(display == IntPtr.Zero, "XOpenDisplay failed (no X server reachable).");
        nuint window = Xlib.XCreateSimpleWindow(display, Xlib.XDefaultRootWindow(display), 0, 0, 64, 64, 0, 0, 0);
        SurfaceSourceXlibWindow source = new SurfaceSourceXlibWindow { Display = (void*)display, Window = window };
        source.Chain.SType = SurfaceSourceXlibWindow.StructType;
        SurfaceDescriptor descriptor = new SurfaceDescriptor { NextInChain = &source.Chain };
        SurfaceCapabilities capabilities = default;

        //Act
        Surface surface = WebGpu.InstanceCreateSurface(gpu.Instance, &descriptor);
        Status status = WebGpu.SurfaceGetCapabilities(surface, gpu.Adapter, &capabilities);
        TextureUsage usages = capabilities.Usages;
        if (status == Status.Success) WebGpu.SurfaceCapabilitiesFreeMembers(capabilities);
        WebGpu.SurfaceRelease(surface);
        Xlib.XDestroyWindow(display, window);
        Xlib.XCloseDisplay(display);

        //Assert
        surface.IsNull.Should().BeFalse();
        status.Should().Be(Status.Success);
        usages.HasFlag(TextureUsage.RenderAttachment).Should().BeTrue();
        // Vulkan swapchains on this class of driver offer TRANSFER_DST; wgpu maps it to COPY_DST.
        usages.HasFlag(TextureUsage.CopyDst).Should().BeTrue(usages.ToString());
    }

    private static class Xlib
    {
        private const string Library = "libX11.so.6";

        public static IntPtr OpenDisplay()
        {
            try
            {
                return XOpenDisplay(IntPtr.Zero);
            }
            catch (DllNotFoundException)
            {
                return IntPtr.Zero;
            }
        }

        [DllImport(Library)]
        public static extern IntPtr XOpenDisplay(IntPtr name);

        [DllImport(Library)]
        public static extern nuint XDefaultRootWindow(IntPtr display);

        [DllImport(Library)]
        public static extern nuint XCreateSimpleWindow(IntPtr display, nuint parent, int x, int y, uint width, uint height, uint borderWidth, nuint border, nuint background);

        [DllImport(Library)]
        public static extern int XDestroyWindow(IntPtr display, nuint window);

        [DllImport(Library)]
        public static extern int XCloseDisplay(IntPtr display);
    }
}
