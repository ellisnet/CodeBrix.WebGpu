using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CodeBrix.Audio.Wave;
using SkiaSharp;
using VenusMusicVideo.Audio;
using VenusMusicVideo.Choreography;
using VenusMusicVideo.Hosting;
using VenusMusicVideo.Maps;
using VenusMusicVideo.Rendering;
using VenusMusicVideo.Video;

namespace VenusMusicVideo;

/// <summary>
/// A music video flying around Venus, rendered with WebGPU at 1080x1920 (portrait), 30 fps, synced to
/// assets/music.mp3 by the keys of assets/flight.json.
/// <code>
/// dotnet run --project samples/VenusMusicVideo                            # live preview with the song
/// dotnet run --project samples/VenusMusicVideo -- --start 1:12            # preview from song time 1:12
/// dotnet run --project samples/VenusMusicVideo -- --render                # the whole video -> Output/Venus-*.mp4
/// dotnet run --project samples/VenusMusicVideo -- --render --from 1:00 --to 1:30
/// dotnet run --project samples/VenusMusicVideo -- --stills 0:00,1:05,2:40 # PNG stills -> Output/
/// dotnet run --project samples/VenusMusicVideo -- --track                 # print where the camera is, no GPU
/// dotnet run --project samples/VenusMusicVideo -- --visible "Ishtar Terra" # when a place is in view
/// </code>
/// All times are SONG times (negative = the descent before the music). On the first run the app downloads the
/// Magellan data from USGS into samples/VenusMusicVideo/downloaded (git-ignored) and builds the planet's maps there.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            Dictionary<string, string> options = Parse(args);
            AssetLocator assets = AssetLocator.Find();
            FlightScript script = FlightScript.Load(assets.FlightPath);
            script.SongSeconds = SongLength(assets.SongPath) ?? script.SongSeconds;

            if (options.ContainsKey("track"))
            {
                MapPreparation names = new MapPreparation(assets.DownloadFolder);
                if (File.Exists(names.GazetteerPath)) names.LoadGazetteer();
                SongEnvelope.Current = SongEnvelope.Load(assets.SongPath);
                return Track(script, double.Parse(options.GetValueOrDefault("every", "2"), CultureInfo.InvariantCulture));
            }

            if (options.TryGetValue("visible", out string placeName))
            {
                MapPreparation names = new MapPreparation(assets.DownloadFolder);
                if (File.Exists(names.GazetteerPath)) names.LoadGazetteer();
                NamedFeature place = Gazetteer.Current?.Find(placeName) ?? throw new ArgumentException("No place called '" + placeName + "' (needs the downloaded gazetteer).");
                Console.WriteLine(place.Name + " is in view (song times):");
                foreach ((double from, double to) in script.Markers.VisibleIntervals(place))
                {
                    Console.WriteLine("  " + FlightScript.FormatTime(from - script.LeadInSeconds) + " - " + FlightScript.FormatTime(to - script.LeadInSeconds));
                }

                return 0;
            }

            if (options.TryGetValue("stills", out string stills)) return Stills(assets, script, stills);
            if (options.ContainsKey("render")) return Render(assets, script, options);
            double start = options.TryGetValue("start", out string s) ? ParseSongTime(s) : -script.LeadInSeconds;
            return LiveRunner.Run(assets, script, start);
        }
        catch (Exception e) when (e is FormatException or FileNotFoundException or DirectoryNotFoundException or ArgumentException or System.Net.Http.HttpRequestException or IOException)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
    }

    /// <summary>Saves the renderer's current frame as a PNG.</summary>
    public static void SavePng(VenusRenderer renderer, string path)
    {
        using SKImage image = renderer.Target.ReadImage() ?? throw new InvalidOperationException("Reading the frame back failed.");
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream stream = File.Create(path);
        png.SaveTo(stream);
    }

    /// <summary>The colour map width the renderer can take.</summary>
    public static int ColorMapWidth(VenusRenderer renderer) => renderer.MaxTextureSize >= 16384 ? 16384 : 8192;

    private static VenusRenderer CreateRendererWithMaps(AssetLocator assets, FlightScript script)
    {
        MapPreparation maps = new MapPreparation(assets.DownloadFolder);
        maps.EnsureReadyWithConsoleProgress();
        maps.LoadGazetteer();
        VenusRenderer renderer = new VenusRenderer(script.Width, script.Height);
        Console.WriteLine("Device: " + renderer.Device.Backend + " - " + renderer.Device.AdapterName);
        renderer.LoadMaps(maps.ColorMapPath(ColorMapWidth(renderer)), maps.ReliefMapPath);
        LoadSongEnvelope(assets, renderer);
        return renderer;
    }

    /// <summary>Reads the song's loudness (the sound-wave ripples are drawn from it) and hands it to the renderer.</summary>
    public static void LoadSongEnvelope(AssetLocator assets, VenusRenderer renderer)
    {
        try
        {
            SongEnvelope.Current = SongEnvelope.Load(assets.SongPath);
            renderer.SetEnvelope(SongEnvelope.Current.Values);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException)
        {
            Console.WriteLine("The song could not be read for the sound wave: " + e.Message);
        }
    }

    private static int Render(AssetLocator assets, FlightScript script, Dictionary<string, string> options)
    {
        double from = options.TryGetValue("from", out string f) ? ParseSongTime(f) + script.LeadInSeconds : 0;
        double? to = options.TryGetValue("to", out string t) ? ParseSongTime(t) + script.LeadInSeconds : null;
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string output = options.GetValueOrDefault("out") ?? Path.Combine(assets.OutputFolder, "Venus-" + stamp + ".mp4");
        using VenusRenderer renderer = CreateRendererWithMaps(assets, script);
        using VideoExporter exporter = new VideoExporter(script, renderer, assets.SongPath, output, from, to);
        int lastPercent = -1;
        while (!exporter.IsFinished)
        {
            exporter.Step();
            int percent = exporter.FramesDone * 100 / exporter.FrameCount;
            if (percent != lastPercent && percent % 5 == 0)
            {
                lastPercent = percent;
                Console.WriteLine(exporter.Progress);
            }
        }

        return 0;
    }

    private static int Stills(AssetLocator assets, FlightScript script, string times)
    {
        Directory.CreateDirectory(assets.OutputFolder);
        using VenusRenderer renderer = CreateRendererWithMaps(assets, script);
        foreach (string item in times.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            double song = ParseSongTime(item);
            CameraPose pose = script.Sample(song + script.LeadInSeconds);
            renderer.Render(script, pose, script.FieldOfViewDegrees);
            string path = Path.Combine(assets.OutputFolder, "still-song-" + FlightScript.FormatTime(song).Replace(':', '_') + ".png");
            SavePng(renderer, path);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0}  alt {1:N0} km  over {2:0.0},{3:0.0} {4}", path, pose.AltitudeKm, pose.SubLat, pose.SubLon, VenusFeatures.Nearest(pose.SubLat, pose.SubLon)));
        }

        return 0;
    }

    private static int Track(FlightScript script, double every)
    {
        Console.WriteLine("  song     video   altitude km   over (lat, lon)   near");
        for (double t = 0; t <= script.VideoSeconds + 1e-6; t += every)
        {
            CameraPose pose = script.Sample(t);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,7} {1,7} {2,12:N0}   {3,6:0.0} {4,7:0.0}   {5}",
                FlightScript.FormatTime(t - script.LeadInSeconds), FlightScript.FormatTime(t), pose.AltitudeKm, pose.SubLat, pose.SubLon, VenusFeatures.Nearest(pose.SubLat, pose.SubLon)));
        }

        Console.WriteLine();
        Console.WriteLine("Sound-wave loudspeakers, song times:");
        foreach (SoundWave.Source source in script.Wave.Sources)
        {
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0,-24} from {1}  {2:0.0} deg/s", source.Place.Name,
                FlightScript.FormatTime(source.Start - script.LeadInSeconds), source.Speed * 180 / Math.PI));
        }

        Console.WriteLine();
        Console.WriteLine("Place labels (first pass through the view), song times:");
        foreach (FeatureMarkers.Pass pass in script.Markers.Passes)
        {
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0,-24} {1} - {2}{3}", script.Markers.NameOf(pass),
                FlightScript.FormatTime(pass.Start - script.LeadInSeconds), FlightScript.FormatTime(pass.End - script.LeadInSeconds), pass.Down ? "  (line points down)" : string.Empty));
        }

        return 0;
    }

    private static double? SongLength(string path)
    {
        try
        {
            using AudioFileReader reader = new AudioFileReader(path);
            return reader.TotalTime.TotalSeconds;
        }
        catch (Exception e)
        {
            Console.WriteLine("Could not read the song's length (" + e.Message + "); using the flight's default.");
            return null;
        }
    }

    private static double ParseSongTime(string s)
    {
        s = s.Trim();
        bool negative = s.StartsWith('-');
        if (negative) s = s.Substring(1);
        double value = s.Split(':').Aggregate(0.0, (acc, part) => (acc * 60) + double.Parse(part, CultureInfo.InvariantCulture));
        return negative ? -value : value;
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        Dictionary<string, string> options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Unexpected argument '" + args[i] + "'.");
            string name = args[i].Substring(2);
            string value = i + 1 < args.Length && (!args[i + 1].StartsWith("--", StringComparison.Ordinal)) ? args[++i] : string.Empty;
            options[name] = value;
        }

        return options;
    }
}
