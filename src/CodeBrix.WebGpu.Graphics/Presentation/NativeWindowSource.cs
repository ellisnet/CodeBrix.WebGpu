using System;
using System.Globalization;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>The kind of native window a <see cref="NativeWindowSource" /> describes.</summary>
public enum NativeWindowKind
{
    /// <summary>An X11 window through Xlib: a <c>Display*</c> and a <c>Window</c> id.</summary>
    Xlib,

    /// <summary>An X11 window through XCB: an <c>xcb_connection_t*</c> and an <c>xcb_window_t</c>.</summary>
    Xcb,

    /// <summary>A Wayland surface: a <c>wl_display*</c> and a <c>wl_surface*</c>.</summary>
    Wayland,

    /// <summary>A Win32 window: the module's <c>HINSTANCE</c> and the <c>HWND</c>.</summary>
    Win32,

    /// <summary>A Core Animation Metal layer (<c>CAMetalLayer*</c>) on macOS or iOS.</summary>
    MetalLayer,

    /// <summary>An Android <c>ANativeWindow*</c>.</summary>
    AndroidNativeWindow,
}

/// <summary>
/// The native handles of a window the application created (with any toolkit), for
/// <see cref="GraphicsContext.CreateWindowSurface(NativeWindowSource)" />. The library creates no windows. Build one with
/// the static factories.
/// </summary>
public sealed class NativeWindowSource
{
    private NativeWindowSource(NativeWindowKind kind, IntPtr display, IntPtr window, ulong windowId)
    {
        Kind = kind;
        Display = display;
        Window = window;
        WindowId = windowId;
    }

    /// <summary>The kind of window.</summary>
    public NativeWindowKind Kind { get; }

    /// <summary>The display, connection or module handle (null for Metal and Android).</summary>
    public IntPtr Display { get; }

    /// <summary>The window, surface, layer or native-window pointer (null for Xlib and XCB, which use <see cref="WindowId" />).</summary>
    public IntPtr Window { get; }

    /// <summary>The X11 window id (Xlib and XCB only).</summary>
    public ulong WindowId { get; }

    /// <summary>An X11 window through Xlib.</summary>
    /// <param name="display">The <c>Display*</c>.</param>
    /// <param name="window">The <c>Window</c> id.</param>
    /// <returns>The source.</returns>
    public static NativeWindowSource FromXlib(IntPtr display, ulong window) => new NativeWindowSource(NativeWindowKind.Xlib, Require(display, nameof(display)), IntPtr.Zero, window);

    /// <summary>An X11 window through XCB.</summary>
    /// <param name="connection">The <c>xcb_connection_t*</c>.</param>
    /// <param name="window">The <c>xcb_window_t</c>.</param>
    /// <returns>The source.</returns>
    public static NativeWindowSource FromXcb(IntPtr connection, uint window) => new NativeWindowSource(NativeWindowKind.Xcb, Require(connection, nameof(connection)), IntPtr.Zero, window);

    /// <summary>A Wayland surface.</summary>
    /// <param name="display">The <c>wl_display*</c>.</param>
    /// <param name="surface">The <c>wl_surface*</c>.</param>
    /// <returns>The source.</returns>
    public static NativeWindowSource FromWayland(IntPtr display, IntPtr surface) => new NativeWindowSource(NativeWindowKind.Wayland, Require(display, nameof(display)), Require(surface, nameof(surface)), 0);

    /// <summary>A Win32 window.</summary>
    /// <param name="hinstance">The <c>HINSTANCE</c> of the module that owns the window class.</param>
    /// <param name="hwnd">The <c>HWND</c>.</param>
    /// <returns>The source.</returns>
    public static NativeWindowSource FromWin32(IntPtr hinstance, IntPtr hwnd) => new NativeWindowSource(NativeWindowKind.Win32, hinstance, Require(hwnd, nameof(hwnd)), 0);

    /// <summary>A <c>CAMetalLayer</c> (for an <c>NSView</c>, make the view layer-backed with a Metal layer first).</summary>
    /// <param name="layer">The <c>CAMetalLayer*</c>.</param>
    /// <returns>The source.</returns>
    public static NativeWindowSource FromMetalLayer(IntPtr layer) => new NativeWindowSource(NativeWindowKind.MetalLayer, IntPtr.Zero, Require(layer, nameof(layer)), 0);

    /// <summary>An Android native window.</summary>
    /// <param name="window">The <c>ANativeWindow*</c>.</param>
    /// <returns>The source.</returns>
    public static NativeWindowSource FromAndroid(IntPtr window) => new NativeWindowSource(NativeWindowKind.AndroidNativeWindow, IntPtr.Zero, Require(window, nameof(window)), 0);

    /// <summary>Describes the source.</summary>
    /// <returns>For example <c>Xlib window 0x3A00007</c>.</returns>
    public override string ToString() => Kind == NativeWindowKind.Xlib || Kind == NativeWindowKind.Xcb
        ? Kind + " window 0x" + WindowId.ToString("X", CultureInfo.InvariantCulture)
        : Kind + " 0x" + Window.ToString("X", CultureInfo.InvariantCulture);

    /// <summary>Creates the WebGPU surface (the <c>WGPUSurfaceSource*</c> chained struct for the kind).</summary>
    /// <param name="instance">The instance.</param>
    /// <param name="source">The window.</param>
    /// <returns>The surface, or null.</returns>
    internal static unsafe Surface CreateSurface(Instance instance, NativeWindowSource source)
    {
        SurfaceDescriptor descriptor = default;
        switch (source.Kind)
        {
            case NativeWindowKind.Xlib:
            {
                SurfaceSourceXlibWindow chained = new SurfaceSourceXlibWindow { Display = (void*)source.Display, Window = source.WindowId };
                chained.Chain.SType = SurfaceSourceXlibWindow.StructType;
                descriptor.NextInChain = &chained.Chain;
                return Native.WebGpu.InstanceCreateSurface(instance, &descriptor);
            }

            case NativeWindowKind.Xcb:
            {
                SurfaceSourceXCBWindow chained = new SurfaceSourceXCBWindow { Connection = (void*)source.Display, Window = (uint)source.WindowId };
                chained.Chain.SType = SurfaceSourceXCBWindow.StructType;
                descriptor.NextInChain = &chained.Chain;
                return Native.WebGpu.InstanceCreateSurface(instance, &descriptor);
            }

            case NativeWindowKind.Wayland:
            {
                SurfaceSourceWaylandSurface chained = new SurfaceSourceWaylandSurface { Display = (void*)source.Display, Surface = (void*)source.Window };
                chained.Chain.SType = SurfaceSourceWaylandSurface.StructType;
                descriptor.NextInChain = &chained.Chain;
                return Native.WebGpu.InstanceCreateSurface(instance, &descriptor);
            }

            case NativeWindowKind.Win32:
            {
                SurfaceSourceWindowsHWND chained = new SurfaceSourceWindowsHWND { Hinstance = (void*)source.Display, Hwnd = (void*)source.Window };
                chained.Chain.SType = SurfaceSourceWindowsHWND.StructType;
                descriptor.NextInChain = &chained.Chain;
                return Native.WebGpu.InstanceCreateSurface(instance, &descriptor);
            }

            case NativeWindowKind.MetalLayer:
            {
                SurfaceSourceMetalLayer chained = new SurfaceSourceMetalLayer { Layer = (void*)source.Window };
                chained.Chain.SType = SurfaceSourceMetalLayer.StructType;
                descriptor.NextInChain = &chained.Chain;
                return Native.WebGpu.InstanceCreateSurface(instance, &descriptor);
            }

            case NativeWindowKind.AndroidNativeWindow:
            {
                SurfaceSourceAndroidNativeWindow chained = new SurfaceSourceAndroidNativeWindow { Window = (void*)source.Window };
                chained.Chain.SType = SurfaceSourceAndroidNativeWindow.StructType;
                descriptor.NextInChain = &chained.Chain;
                return Native.WebGpu.InstanceCreateSurface(instance, &descriptor);
            }

            default:
                return Surface.Null;
        }
    }

    private static IntPtr Require(IntPtr handle, string name)
    {
        if (handle == IntPtr.Zero) throw new ArgumentException("The handle is null.", name);
        return handle;
    }
}
