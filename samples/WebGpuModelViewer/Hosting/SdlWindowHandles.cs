using System;
using CodeBrix.Sdl3;
using CodeBrix.WebGpu.Graphics;
using static CodeBrix.Sdl3.SDL3;

namespace WebGpuModelViewer.Hosting;

/// <summary>
/// Turns an SDL window into the native handles <see cref="GraphicsContext.CreateWindowSurface" /> needs, from the
/// window's SDL properties: an X11 display and window id, a Wayland display and surface, a Win32 HWND and
/// HINSTANCE, or (macOS) a CAMetalLayer from an SDL Metal view.
/// </summary>
internal static unsafe class SdlWindowHandles
{
    /// <summary>Reads the window's native handles.</summary>
    /// <param name="window">The window.</param>
    /// <param name="metalView">The SDL Metal view created on macOS (destroy it with the window), or zero.</param>
    /// <returns>The source.</returns>
    /// <exception cref="InvalidOperationException">The video driver is not one the sample knows, or a handle is missing.</exception>
    public static NativeWindowSource From(SDL_Window* window, out IntPtr metalView)
    {
        metalView = IntPtr.Zero;
        string driver = SDL_GetCurrentVideoDriver() ?? string.Empty;
        SDL_PropertiesID properties = SDL_GetWindowProperties(window);
        switch (driver)
        {
            case "x11":
            {
                IntPtr display = SDL_GetPointerProperty(properties, SDL_PROP_WINDOW_X11_DISPLAY_POINTER, IntPtr.Zero);
                long id = SDL_GetNumberProperty(properties, SDL_PROP_WINDOW_X11_WINDOW_NUMBER, 0);
                if (display == IntPtr.Zero || id == 0) throw new InvalidOperationException("SDL reported no X11 display or window.");
                return NativeWindowSource.FromXlib(display, (ulong)id);
            }

            case "wayland":
            {
                IntPtr display = SDL_GetPointerProperty(properties, SDL_PROP_WINDOW_WAYLAND_DISPLAY_POINTER, IntPtr.Zero);
                IntPtr surface = SDL_GetPointerProperty(properties, SDL_PROP_WINDOW_WAYLAND_SURFACE_POINTER, IntPtr.Zero);
                if (display == IntPtr.Zero || surface == IntPtr.Zero) throw new InvalidOperationException("SDL reported no Wayland display or surface.");
                return NativeWindowSource.FromWayland(display, surface);
            }

            case "windows":
            {
                IntPtr hwnd = SDL_GetPointerProperty(properties, SDL_PROP_WINDOW_WIN32_HWND_POINTER, IntPtr.Zero);
                IntPtr instance = SDL_GetPointerProperty(properties, SDL_PROP_WINDOW_WIN32_INSTANCE_POINTER, IntPtr.Zero);
                if (hwnd == IntPtr.Zero) throw new InvalidOperationException("SDL reported no HWND.");
                return NativeWindowSource.FromWin32(instance, hwnd);
            }

            case "cocoa":
            {
                metalView = SDL_Metal_CreateView(window);
                IntPtr layer = metalView == IntPtr.Zero ? IntPtr.Zero : SDL_Metal_GetLayer(metalView);
                if (layer == IntPtr.Zero) throw new InvalidOperationException("SDL could not create a Metal view: " + SDL_GetError());
                return NativeWindowSource.FromMetalLayer(layer);
            }

            default:
                throw new InvalidOperationException("The SDL video driver '" + driver + "' is not supported by this sample (x11, wayland, windows and cocoa are).");
        }
    }
}
