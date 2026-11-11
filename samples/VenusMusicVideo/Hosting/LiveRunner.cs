using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CodeBrix.Sdl3;
using CodeBrix.WebGpu.Graphics;
using CodeBrix.WebGpu.Native;
using VenusMusicVideo.Audio;
using VenusMusicVideo.Choreography;
using VenusMusicVideo.Maps;
using VenusMusicVideo.Rendering;
using VenusMusicVideo.Video;
using static CodeBrix.Sdl3.SDL3;

namespace VenusMusicVideo.Hosting;

/// <summary>
/// The preview: a portrait window showing the video frame (always rendered at the video's size, scaled to fit)
/// with the song in step. flight.json is watched and reloaded whenever it is saved, keeping the playhead, so a
/// sync can be tweaked while watching. Keys: Space play/pause, Left/Right seek 5 s (Shift: 1 s), Up/Down jump to
/// the previous/next key, Home the start, M mute, H the panel, P saves a still, R renders the whole video
/// (Esc cancels the render), Esc quits.
/// </summary>
internal static unsafe class LiveRunner
{
    /// <summary>Runs the preview.</summary>
    public static int Run(AssetLocator assets, FlightScript script, double startSongSeconds)
    {
        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO))
        {
            Console.Error.WriteLine("SDL_Init failed: " + SDL_GetError());
            return 1;
        }

        SDL_WindowFlags flags = SDL_WindowFlags.SDL_WINDOW_RESIZABLE | SDL_WindowFlags.SDL_WINDOW_HIGH_PIXEL_DENSITY;
        if (OperatingSystem.IsMacOS()) flags |= SDL_WindowFlags.SDL_WINDOW_METAL;
        SDL_Window* window = SDL_CreateWindow("Venus music video", 486, 864, flags);
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
            int pixelWidth;
            int pixelHeight;
            SDL_GetWindowSizeInPixels(window, &pixelWidth, &pixelHeight);
            using VenusRenderer renderer = new VenusRenderer(script.Width, script.Height);
            Console.WriteLine("Device: " + renderer.Device.Backend + " - " + renderer.Device.AdapterName);
            using WindowSurface surface = renderer.Context.CreateWindowSurface(source);
            surface.Configure(pixelWidth, pixelHeight, PresentMode.Fifo, TextureFormat.Undefined, withDepth: false);
            if (!PrepareMaps(assets, renderer, surface)) return 0;
            using SongPlayer song = new SongPlayer(assets.SongPath);
            return new Session(assets, script, renderer, surface, song, startSongSeconds + script.LeadInSeconds).Loop();
        }
        finally
        {
            if (metalView != IntPtr.Zero) SDL_Metal_DestroyView(metalView);
            SDL_DestroyWindow(window);
            SDL_Quit();
        }
    }

    /// <summary>
    /// Downloads and builds the maps on the first run (on a worker thread) while the window shows the progress;
    /// then loads them onto the GPU. Returns false when the user quit (Esc or closing the window) meanwhile.
    /// </summary>
    private static bool PrepareMaps(AssetLocator assets, VenusRenderer renderer, WindowSurface surface)
    {
        MapPreparation maps = new MapPreparation(assets.DownloadFolder);
        if (!maps.IsReady)
        {
            Console.WriteLine("First run: downloading the Venus data from USGS into " + assets.DownloadFolder + " and building the maps.");
            using CancellationTokenSource cancel = new CancellationTokenSource();
            Task work = Task.Run(() => maps.EnsureReady(cancel.Token));
            SDL_Event e;
            while (!work.IsCompleted)
            {
                while (SDL_PollEvent(&e))
                {
                    SDL_EventType type = (SDL_EventType)e.type;
                    bool quit = type == SDL_EventType.SDL_EVENT_QUIT || type == SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED
                        || (type == SDL_EventType.SDL_EVENT_KEY_DOWN && e.key.key == SDL_Keycode.SDLK_ESCAPE);
                    if (quit)
                    {
                        cancel.Cancel();
                        try
                        {
                            work.Wait();
                        }
                        catch (AggregateException)
                        {
                        }

                        return false;
                    }

                    if (type == SDL_EventType.SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED) surface.Resize(e.window.data1, e.window.data2);
                }

                (string title, string detail, double step, double overall) = maps.Progress.Snapshot();
                renderer.RenderStatus(title, detail, step, overall);
                if (surface.Width > 0 && surface.Height > 0) renderer.PresentTo(surface);
                else SDL_Delay(16);
            }

            if (work.IsFaulted)
            {
                Exception error = work.Exception.GetBaseException();
                Console.Error.WriteLine("Preparing the maps failed: " + error.Message);
                ShowUntilClosed(renderer, surface, "Preparing the maps failed", error.Message);
                return false;
            }
        }

        renderer.RenderStatus("Loading the planet onto the GPU", null, null, null);
        renderer.PresentTo(surface);
        maps.LoadGazetteer();
        renderer.LoadMaps(maps.ColorMapPath(Program.ColorMapWidth(renderer)), maps.ReliefMapPath);
        Program.LoadSongEnvelope(assets, renderer);
        return true;
    }

    private static void ShowUntilClosed(VenusRenderer renderer, WindowSurface surface, string title, string detail)
    {
        SDL_Event e;
        while (true)
        {
            while (SDL_PollEvent(&e))
            {
                SDL_EventType type = (SDL_EventType)e.type;
                if (type == SDL_EventType.SDL_EVENT_QUIT || type == SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED || type == SDL_EventType.SDL_EVENT_KEY_DOWN) return;
            }

            renderer.RenderStatus(title, detail, null, null);
            if (surface.Width > 0 && surface.Height > 0) renderer.PresentTo(surface);
            else SDL_Delay(16);
        }
    }

    private sealed class Session
    {
        private readonly AssetLocator assets;
        private readonly VenusRenderer renderer;
        private readonly WindowSurface surface;
        private readonly SongPlayer song;
        private readonly List<string> status = new List<string>();
        private FlightScript script;
        private double videoTime;
        private bool playing;
        private bool started;
        private readonly double startTime;
        private bool showHelp = true;
        private DateTime flightStamp;
        private double lastCheck;
        private double wallNow;
        private string message;
        private double messageUntil;
        private VideoExporter exporter;

        public Session(AssetLocator assets, FlightScript script, VenusRenderer renderer, WindowSurface surface, SongPlayer song, double videoTime)
        {
            this.assets = assets;
            this.script = script;
            this.renderer = renderer;
            this.surface = surface;
            this.song = song;
            this.videoTime = Math.Max(0, videoTime);
            startTime = this.videoTime;
            flightStamp = File.GetLastWriteTimeUtc(assets.FlightPath);
        }

        public int Loop()
        {
            Stopwatch wall = Stopwatch.StartNew();
            Stopwatch frameClock = Stopwatch.StartNew();
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
                            running = OnKey(e.key, wall.Elapsed.TotalSeconds);
                            break;
                        case SDL_EventType.SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED:
                            surface.Resize(e.window.data1, e.window.data2);
                            break;
                    }
                }

                double dt = Math.Min(0.25, frameClock.Elapsed.TotalSeconds);
                frameClock.Restart();
                double now = wall.Elapsed.TotalSeconds;
                wallNow = now;
                status.Clear();

                if (exporter != null)
                {
                    StepExport();
                }
                else
                {
                    if (now - lastCheck > 0.4)
                    {
                        lastCheck = now;
                        ReloadIfChanged();
                    }

                    if (playing) videoTime += dt;
                    if (videoTime >= script.VideoSeconds)
                    {
                        videoTime = script.VideoSeconds;
                        playing = false;
                    }

                    song.Follow(videoTime - script.LeadInSeconds, playing);
                    CameraPose pose = script.Sample(videoTime);
                    renderer.Render(script, pose, script.FieldOfViewDegrees, applyFade: started);
                    if (!started) status.Add("READY - position the window, then press F to start");
                    else if (!playing) status.Add("PAUSED");
                    if (song.IsMuted) status.Add("muted");
                    if (message != null && now < messageUntil) status.Add(message);
                    if (showHelp) Overlay.Draw(renderer.Target.Surface.Canvas, renderer.Width, script, pose, status, showHelp: true);
                }

                if (surface.Width == 0 || surface.Height == 0)
                {
                    SDL_Delay(16);
                    continue;
                }

                renderer.PresentTo(surface);
                foreach (string error in renderer.Device.TakeUncapturedErrors()) Console.WriteLine("WebGPU error: " + error);
            }

            exporter?.Dispose();
            return 0;
        }

        private void StepExport()
        {
            try
            {
                exporter.Step();
                status.Add(exporter.Progress);
                status.Add("Esc cancels");
                Overlay.Draw(renderer.Target.Surface.Canvas, renderer.Width, script, exporter.LastPose, status, showHelp: false);
                if (exporter.IsFinished)
                {
                    Say("Wrote " + Path.GetFileName(exporter.OutputPath), 8);
                    exporter.Dispose();
                    exporter = null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Render failed: " + ex.Message);
                Say("Render failed: " + ex.Message, 10);
                exporter.Dispose();
                exporter = null;
            }
        }

        private void ReloadIfChanged()
        {
            DateTime stamp = File.GetLastWriteTimeUtc(assets.FlightPath);
            if (stamp == flightStamp) return;
            flightStamp = stamp;
            try
            {
                FlightScript fresh = FlightScript.Load(assets.FlightPath);
                fresh.SongSeconds = script.SongSeconds;
                script = fresh;
                Say("flight.json reloaded (" + script.Keys.Count + " keys)", 3);
                Console.WriteLine("flight.json reloaded at " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                Say("flight.json NOT reloaded: " + ex.Message, 10);
                Console.WriteLine("flight.json NOT reloaded: " + ex.Message);
            }
        }

        private void Say(string text, double seconds)
        {
            message = text;
            messageUntil = wallNow + seconds;
        }

        private bool OnKey(SDL_KeyboardEvent key, double now)
        {
            wallNow = now;
            bool shift = (key.mod & SDL_Keymod.SDL_KMOD_SHIFT) != 0;
            if (exporter != null)
            {
                if (key.key == SDL_Keycode.SDLK_ESCAPE && !key.repeat)
                {
                    exporter.Dispose();
                    exporter = null;
                    Say("Render cancelled", 4);
                }

                return true;
            }

            switch (key.key)
            {
                case SDL_Keycode.SDLK_ESCAPE:
                    return false;
                case SDL_Keycode.SDLK_F:
                    // Start (or restart) the flight and the music from the beginning.
                    if (!key.repeat)
                    {
                        videoTime = startTime;
                        started = true;
                        playing = true;
                    }

                    break;
                case SDL_Keycode.SDLK_SPACE:
                    if (!key.repeat && started) playing = !playing;
                    break;
                case SDL_Keycode.SDLK_LEFT:
                    videoTime = Math.Max(0, videoTime - (shift ? 1 : 5));
                    break;
                case SDL_Keycode.SDLK_RIGHT:
                    videoTime = Math.Min(script.VideoSeconds, videoTime + (shift ? 1 : 5));
                    break;
                case SDL_Keycode.SDLK_UP:
                    videoTime = PreviousKey();
                    break;
                case SDL_Keycode.SDLK_DOWN:
                    videoTime = NextKey();
                    break;
                case SDL_Keycode.SDLK_HOME:
                    videoTime = 0;
                    break;
                case SDL_Keycode.SDLK_M:
                    if (!key.repeat) song.IsMuted = !song.IsMuted;
                    break;
                case SDL_Keycode.SDLK_H:
                    if (!key.repeat) showHelp = !showHelp;
                    break;
                case SDL_Keycode.SDLK_P:
                    if (!key.repeat) SaveStill();
                    break;
                case SDL_Keycode.SDLK_R:
                    if (!key.repeat) StartExport();
                    break;
            }

            return true;
        }

        private double PreviousKey()
        {
            double best = 0;
            foreach (FlightKey k in script.Keys)
            {
                if (k.VideoTime < videoTime - 0.25) best = Math.Max(best, k.VideoTime);
            }

            return best;
        }

        private double NextKey()
        {
            foreach (FlightKey k in script.Keys)
            {
                if (k.VideoTime > videoTime + 0.01) return k.VideoTime;
            }

            return videoTime;
        }

        private void SaveStill()
        {
            Directory.CreateDirectory(assets.OutputFolder);
            string path = Path.Combine(assets.OutputFolder, "still-song-" + FlightScript.FormatTime(videoTime - script.LeadInSeconds).Replace(':', '_') + ".png");
            renderer.Render(script, script.Sample(videoTime), script.FieldOfViewDegrees);
            Program.SavePng(renderer, path);
            Say("Saved " + Path.GetFileName(path), 4);
        }

        private void StartExport()
        {
            playing = false;
            song.Follow(0, false);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            exporter = new VideoExporter(script, renderer, assets.SongPath, Path.Combine(assets.OutputFolder, "Venus-" + stamp + ".mp4"));
        }
    }
}
