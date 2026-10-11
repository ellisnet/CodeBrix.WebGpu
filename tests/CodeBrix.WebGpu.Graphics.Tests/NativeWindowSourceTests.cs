using System;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>Native window descriptions (no window or GPU needed).</summary>
public class NativeWindowSourceTests
{
    [Fact]
    public void Factories_record_the_kind_and_handles()
    {
        //Arrange
        IntPtr display = new IntPtr(0x1000);
        IntPtr window = new IntPtr(0x2000);

        //Act
        NativeWindowSource xlib = NativeWindowSource.FromXlib(display, 0x3A00007);
        NativeWindowSource xcb = NativeWindowSource.FromXcb(display, 42);
        NativeWindowSource wayland = NativeWindowSource.FromWayland(display, window);
        NativeWindowSource win32 = NativeWindowSource.FromWin32(display, window);
        NativeWindowSource metal = NativeWindowSource.FromMetalLayer(window);
        NativeWindowSource android = NativeWindowSource.FromAndroid(window);

        //Assert
        xlib.Kind.Should().Be(NativeWindowKind.Xlib);
        xlib.WindowId.Should().Be(0x3A00007UL);
        xlib.ToString().Should().Be("Xlib window 0x3A00007");
        xcb.WindowId.Should().Be(42UL);
        wayland.Window.Should().Be(window);
        win32.Display.Should().Be(display);
        metal.Kind.Should().Be(NativeWindowKind.MetalLayer);
        android.ToString().Should().Be("AndroidNativeWindow 0x2000");
    }

    [Fact]
    public void Factories_reject_null_handles()
    {
        //Arrange
        //Act
        Action xlib = () => NativeWindowSource.FromXlib(IntPtr.Zero, 1);
        Action wayland = () => NativeWindowSource.FromWayland(new IntPtr(1), IntPtr.Zero);
        Action win32 = () => NativeWindowSource.FromWin32(IntPtr.Zero, IntPtr.Zero);

        //Assert
        xlib.Should().Throw<ArgumentException>();
        wayland.Should().Throw<ArgumentException>();
        win32.Should().Throw<ArgumentException>();
    }
}
