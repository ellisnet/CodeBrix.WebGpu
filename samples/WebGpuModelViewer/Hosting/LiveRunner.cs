using System;
using System.Diagnostics;
using System.Globalization;
using CodeBrix.Sdl3;
using CodeBrix.WebGpu.Graphics;
using CodeBrix.WebGpu.Native;
using WebGpuModelViewer.Rendering;
using static CodeBrix.Sdl3.SDL3;

namespace WebGpuModelViewer.Hosting;

/// <summary>
/// Opens an SDL window, creates a WebGPU surface over it from its native handles and renders at vsync until the window
/// closes or Escape is pressed. Drag with the left mouse button to orbit, use the wheel to zoom.
/// </summary>
internal static unsafe class LiveRunner
{
    /// <summary>Runs the live mode.</summary>
    /// <param name="assets">The Assets folder.</param>
    /// <param name="width">The initial window width (logical).</param>
    /// <param name="height">The initial window height (logical).</param>
    /// <param name="seconds">Close by itself after this many seconds (0 = only on Escape or close).</param>
    /// <param name="videoDriver">The windowing system to force (x11, wayland, windows, cocoa), or null for the default.</param>
    /// <returns>The process exit code.</returns>
    public static int Run(string assets, int width, int height, double seconds, string videoDriver)
    {
        if (!string.IsNullOrEmpty(videoDriver)) SDL_SetHint(SDL_HINT_VIDEO_DRIVER, videoDriver);
        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO))
        {
            Console.Error.WriteLine("SDL_Init failed: " + SDL_GetError());
            return 1;
        }

        SDL_WindowFlags flags = SDL_WindowFlags.SDL_WINDOW_RESIZABLE | SDL_WindowFlags.SDL_WINDOW_HIGH_PIXEL_DENSITY;
        if (OperatingSystem.IsMacOS()) flags |= SDL_WindowFlags.SDL_WINDOW_METAL;
        SDL_Window* window = SDL_CreateWindow("WebGpuModelViewer - CodeBrix.WebGpu.Graphics", width, height, flags);
        if (window == null)
        {
            Console.Error.WriteLine("SDL_CreateWindow failed: " + SDL_GetError());
            SDL_Quit();
            return 1;
        }

        IntPtr metalView = IntPtr.Zero;
        try
        {
            NativeWindowSource source = SdlWindowHandles.From(window, out metalView);
            Console.WriteLine("SDL video driver: " + SDL_GetCurrentVideoDriver() + "; window: " + source);
            int pixelWidth;
            int pixelHeight;
            SDL_GetWindowSizeInPixels(window, &pixelWidth, &pixelHeight);

            using ViewerRenderer renderer = new ViewerRenderer(assets, pixelWidth, pixelHeight);
            using WindowSurface surface = renderer.Context.CreateWindowSurface(source);
            surface.Configure(pixelWidth, pixelHeight, PresentMode.Fifo, TextureFormat.Undefined, withDepth: false);
            Console.WriteLine("Device: " + renderer.Device.Backend + " - " + renderer.Device.AdapterName);
            Console.WriteLine("Surface: " + surface.Width + "x" + surface.Height + " " + surface.Format + " " + surface.PresentMode + "; target " + renderer.Target);
            return Loop(renderer, surface, seconds);
        }
        finally
        {
            if (metalView != IntPtr.Zero) SDL_Metal_DestroyView(metalView);
            SDL_DestroyWindow(window);
            SDL_Quit();
        }
    }

    private static int Loop(ViewerRenderer renderer, WindowSurface surface, double seconds)
    {
        Stopwatch clock = Stopwatch.StartNew();
        Stopwatch frameClock = Stopwatch.StartNew();
        long presented = 0;
        bool running = true;
        SDL_Event e;
        while (running)
        {
            while (SDL_PollEvent(&e))
            {
                switch ((SDL_EventType)e.type)
                {
                    case SDL_EventType.SDL_EVENT_QUIT:
                    case SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED:
                        running = false;
                        break;
                    case SDL_EventType.SDL_EVENT_KEY_DOWN:
                        if (e.key.key == SDL_Keycode.SDLK_ESCAPE) running = false;
                        break;
                    case SDL_EventType.SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED:
                        surface.Resize(e.window.data1, e.window.data2);
                        if (e.window.data1 > 0 && e.window.data2 > 0) renderer.Resize(e.window.data1, e.window.data2);
                        break;
                    case SDL_EventType.SDL_EVENT_MOUSE_MOTION:
                        if ((e.motion.state & SDL_MouseButtonFlags.SDL_BUTTON_LMASK) != 0)
                        {
                            renderer.UserYaw -= e.motion.xrel * 0.008f;
                            renderer.Orbit.Pitch += e.motion.yrel * 0.006f;
                        }

                        break;
                    case SDL_EventType.SDL_EVENT_MOUSE_WHEEL:
                        renderer.Orbit.Zoom(e.wheel.y > 0 ? 0.9f : 1.1f);
                        break;
                }
            }

            if (seconds > 0 && clock.Elapsed.TotalSeconds >= seconds) running = false;
            if (!running) break;
            if (surface.Width == 0 || surface.Height == 0)
            {
                SDL_Delay(16);
                continue;
            }

            float time = (float)clock.Elapsed.TotalSeconds;
            renderer.RenderFrame(time);
            if (renderer.PresentTo(surface)) presented++;
            renderer.Stats.Add((float)frameClock.Elapsed.TotalMilliseconds);
            frameClock.Restart();
            foreach (string error in renderer.Device.TakeUncapturedErrors()) Console.WriteLine("WebGPU error: " + error);
        }

        double elapsed = clock.Elapsed.TotalSeconds;
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "Presented {0} frames in {1:F1} s: {2:F1} fps average", presented, elapsed, presented / Math.Max(0.001, elapsed)));
        return 0;
    }
}
