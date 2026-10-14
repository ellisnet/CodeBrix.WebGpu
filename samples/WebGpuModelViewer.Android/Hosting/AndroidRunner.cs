using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using Android.Content;
using Android.OS;
using CodeBrix.Sdl3;
using CodeBrix.WebGpu.Graphics;
using CodeBrix.WebGpu.Native;
using SkiaSharp;
using WebGpuModelViewer.Rendering;
using static CodeBrix.Sdl3.SDL3;

namespace WebGpuModelViewer.AndroidHosting;

/// <summary>
/// The Android host: copies the assets, opens a full-screen SDL window, creates the WebGPU surface over its
/// <c>ANativeWindow</c>, writes a few headless PNG frames to the app's files folder as a check, then renders live at
/// the display's refresh rate. One finger drags to orbit; two fingers pinch to zoom.
/// </summary>
internal static unsafe class AndroidRunner
{
    /// <summary>How many headless frames are written at start.</summary>
    private const int HeadlessFrames = 6;

    private static readonly Dictionary<ulong, Vector2> Fingers = new Dictionary<ulong, Vector2>();

    /// <summary>Runs the viewer until SDL quits.</summary>
    /// <param name="context">The activity.</param>
    public static void Run(Context context)
    {
        ViewerLog.Info("Starting on " + Build.Manufacturer + " " + Build.Model + " (Android " + Build.VERSION.Release + ", API " + (int)Build.VERSION.SdkInt + ", " + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture + ")");
        Stopwatch copyClock = Stopwatch.StartNew();
        string assets = AssetInstaller.Install(context, out int copied, out int total);
        ViewerLog.Info(string.Format(CultureInfo.InvariantCulture, "Assets: {0} files, {1} copied to {2} in {3:F0} ms", total, copied, assets, copyClock.Elapsed.TotalMilliseconds));

        SDL_SetHint(SDL_HINT_ORIENTATIONS, "LandscapeLeft LandscapeRight");
        SDL_SetHint(SDL_HINT_TOUCH_MOUSE_EVENTS, "0");
        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO))
        {
            ViewerLog.Error("SDL_Init failed: " + SDL_GetError());
            return;
        }

        SDL_Window* window = SDL_CreateWindow("WebGpuModelViewer", 0, 0, SDL_WindowFlags.SDL_WINDOW_FULLSCREEN | SDL_WindowFlags.SDL_WINDOW_HIGH_PIXEL_DENSITY);
        if (window == null)
        {
            ViewerLog.Error("SDL_CreateWindow failed: " + SDL_GetError());
            SDL_Quit();
            return;
        }

        try
        {
            int pixelWidth;
            int pixelHeight;
            SDL_GetWindowSizeInPixels(window, &pixelWidth, &pixelHeight);
            ViewerLog.Info("SDL video driver: " + SDL_GetCurrentVideoDriver() + "; window " + pixelWidth + "x" + pixelHeight + " pixels");

            Stopwatch setup = Stopwatch.StartNew();
            using ViewerRenderer renderer = new ViewerRenderer(assets, 960, 540);
            ViewerLog.Info("Device: " + renderer.Device.Backend + " - " + (renderer.Device.AdapterName ?? string.Empty).Replace('\n', ' '));
            ViewerLog.Info(string.Format(CultureInfo.InvariantCulture, "Loaded {0} model files ({1} placed, {2} shared textures, {3} materials) and the environment in {4:F0} ms; {5}, lit by the {6}", renderer.Scene.ModelFileCount, renderer.Scene.ActorCount, renderer.Scene.TextureCache.Count, renderer.Scene.MaterialCount, setup.Elapsed.TotalMilliseconds, renderer.Options.Hdr ? "HDR (RGBA16Float + tone mapping)" : "LDR", renderer.EnvironmentDescription));

            // The app-specific external files folder, so "adb pull" can fetch the frames from a release (non-debuggable) build.
            string filesRoot = context.GetExternalFilesDir(null)?.AbsolutePath ?? context.FilesDir.AbsolutePath;
            WriteHeadlessFrames(renderer, Path.Combine(filesRoot, "headless"));

            renderer.Resize(pixelWidth, pixelHeight);
            WindowSurface surface = CreateSurface(window, renderer, pixelWidth, pixelHeight);
            try
            {
                Loop(window, renderer, ref surface);
            }
            finally
            {
                surface?.Dispose();
            }
        }
        finally
        {
            SDL_DestroyWindow(window);
            SDL_Quit();
            ViewerLog.Info("Stopped.");
        }
    }

    private static WindowSurface CreateSurface(SDL_Window* window, ViewerRenderer renderer, int width, int height)
    {
        IntPtr nativeWindow = SDL_GetPointerProperty(SDL_GetWindowProperties(window), SDL_PROP_WINDOW_ANDROID_WINDOW_POINTER, IntPtr.Zero);
        if (nativeWindow == IntPtr.Zero)
        {
            ViewerLog.Warn("SDL reported no ANativeWindow yet; waiting for the surface.");
            return null;
        }

        WindowSurface surface = renderer.Context.CreateWindowSurface(NativeWindowSource.FromAndroid(nativeWindow));
        surface.Configure(width, height, PresentMode.Fifo, TextureFormat.Undefined, withDepth: false);
        ViewerLog.Info("Surface: " + surface.Width + "x" + surface.Height + " " + surface.Format + " " + surface.PresentMode + " (formats: " + string.Join(", ", surface.SupportedFormats) + "; present modes: " + string.Join(", ", surface.SupportedPresentModes) + "); target " + renderer.Target);
        return surface;
    }

    private static void WriteHeadlessFrames(ViewerRenderer renderer, string folder)
    {
        Directory.CreateDirectory(folder);
        Stopwatch clock = Stopwatch.StartNew();
        for (int frame = 0; frame < HeadlessFrames; frame++)
        {
            Stopwatch render = Stopwatch.StartNew();
            renderer.RenderFrame(frame / 30f * 15f);
            using SKImage image = renderer.ReadImage();
            renderer.Stats.Add((float)render.Elapsed.TotalMilliseconds);
            using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
            string path = Path.Combine(folder, string.Format(CultureInfo.InvariantCulture, "frame_{0:D4}.png", frame));
            using FileStream stream = File.Create(path);
            png.SaveTo(stream);
        }

        ViewerLog.Info(string.Format(CultureInfo.InvariantCulture, "Headless: wrote {0} PNG frames of {1}x{2} to {3} in {4:F0} ms", HeadlessFrames, renderer.Target.Width, renderer.Target.Height, folder, clock.Elapsed.TotalMilliseconds));
        LogErrors(renderer);
    }

    private static void Loop(SDL_Window* window, ViewerRenderer renderer, ref WindowSurface surface)
    {
        Stopwatch clock = Stopwatch.StartNew();
        Stopwatch frameClock = Stopwatch.StartNew();
        Stopwatch reportClock = Stopwatch.StartNew();
        long presented = 0;
        long presentedAtReport = 0;
        bool running = true;
        bool background = false;
        SDL_Event e;
        while (running)
        {
            while (SDL_PollEvent(&e))
            {
                switch ((SDL_EventType)e.type)
                {
                    case SDL_EventType.SDL_EVENT_QUIT:
                    case SDL_EventType.SDL_EVENT_TERMINATING:
                    case SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED:
                        running = false;
                        break;
                    case SDL_EventType.SDL_EVENT_KEY_DOWN:
                        if (e.key.key == SDL_Keycode.SDLK_AC_BACK || e.key.key == SDL_Keycode.SDLK_ESCAPE) running = false;
                        break;
                    case SDL_EventType.SDL_EVENT_WILL_ENTER_BACKGROUND:
                    case SDL_EventType.SDL_EVENT_DID_ENTER_BACKGROUND:
                        if (!background) ViewerLog.Info("Entering the background; releasing the surface.");
                        background = true;
                        renderer.Context.WaitIdle();
                        surface?.Dispose();
                        surface = null;
                        break;
                    case SDL_EventType.SDL_EVENT_DID_ENTER_FOREGROUND:
                        background = false;
                        ViewerLog.Info("Back in the foreground.");
                        break;
                    case SDL_EventType.SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED:
                        ViewerLog.Info("Window pixel size: " + e.window.data1 + "x" + e.window.data2);
                        if (e.window.data1 > 0 && e.window.data2 > 0) renderer.Resize(e.window.data1, e.window.data2);
                        surface?.Resize(e.window.data1, e.window.data2);
                        break;
                    case SDL_EventType.SDL_EVENT_FINGER_DOWN:
                        Fingers[(ulong)e.tfinger.fingerID] = new Vector2(e.tfinger.x, e.tfinger.y);
                        break;
                    case SDL_EventType.SDL_EVENT_FINGER_UP:
                    case SDL_EventType.SDL_EVENT_FINGER_CANCELED:
                        Fingers.Remove((ulong)e.tfinger.fingerID);
                        break;
                    case SDL_EventType.SDL_EVENT_FINGER_MOTION:
                        OnFingerMotion(renderer, (ulong)e.tfinger.fingerID, new Vector2(e.tfinger.x, e.tfinger.y));
                        break;
                }
            }

            if (!running) break;
            if (background)
            {
                SDL_Delay(50);
                continue;
            }

            if (surface == null)
            {
                int width;
                int height;
                SDL_GetWindowSizeInPixels(window, &width, &height);
                if (width > 0 && height > 0)
                {
                    renderer.Resize(width, height);
                    surface = CreateSurface(window, renderer, width, height);
                }

                if (surface == null)
                {
                    SDL_Delay(50);
                    continue;
                }
            }

            float time = (float)clock.Elapsed.TotalSeconds;
            renderer.RenderFrame(time);
            if (renderer.PresentTo(surface)) presented++;
            renderer.Stats.Add((float)frameClock.Elapsed.TotalMilliseconds);
            frameClock.Restart();
            LogErrors(renderer);

            if (reportClock.Elapsed.TotalSeconds >= 2)
            {
                double fps = (presented - presentedAtReport) / reportClock.Elapsed.TotalSeconds;
                ViewerLog.Info(string.Format(CultureInfo.InvariantCulture, "fps {0:F1} (frame {1:F2} ms average, {2} presented, {3}x{4})", fps, renderer.Stats.AverageMilliseconds, presented, surface.Width, surface.Height));
                presentedAtReport = presented;
                reportClock.Restart();
            }
        }

        ViewerLog.Info(string.Format(CultureInfo.InvariantCulture, "Presented {0} frames in {1:F1} s: {2:F1} fps average", presented, clock.Elapsed.TotalSeconds, presented / Math.Max(0.001, clock.Elapsed.TotalSeconds)));
    }

    private static void OnFingerMotion(ViewerRenderer renderer, ulong finger, Vector2 position)
    {
        if (!Fingers.TryGetValue(finger, out Vector2 previous))
        {
            Fingers[finger] = position;
            return;
        }

        if (Fingers.Count == 1)
        {
            Vector2 delta = position - previous;
            renderer.UserYaw -= delta.X * 4f;
            renderer.Orbit.Pitch = Math.Clamp(renderer.Orbit.Pitch + (delta.Y * 2.5f), 0.05f, 1.35f);
        }
        else if (Fingers.Count == 2)
        {
            Vector2 other = Vector2.Zero;
            foreach (KeyValuePair<ulong, Vector2> pair in Fingers)
            {
                if (pair.Key != finger) other = pair.Value;
            }

            float before = Vector2.Distance(previous, other);
            float after = Vector2.Distance(position, other);
            if (before > 0.01f && after > 0.01f)
            {
                renderer.Orbit.Zoom(before / after);
                renderer.Orbit.Distance = Math.Clamp(renderer.Orbit.Distance, 3f, 30f);
            }
        }

        Fingers[finger] = position;
    }

    private static void LogErrors(ViewerRenderer renderer)
    {
        foreach (string error in renderer.Device.TakeUncapturedErrors()) ViewerLog.Error("WebGPU error (Skia device): " + error);
        foreach (GraphicsError error in renderer.Context.TakeErrors()) ViewerLog.Error("WebGPU error (Graphics context): " + error);
    }
}
