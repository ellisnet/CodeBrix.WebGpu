using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace VenusMusicVideo.Maps;

/// <summary>
/// Makes sure the planet maps are ready in the sample's <c>downloaded</c> folder (git-ignored, so the NASA/USGS
/// data and the maps made from it are never committed). On the first run it downloads the two public-domain
/// Magellan products from the USGS Astrogeology map server and builds the colour and relief maps from them
/// (<see cref="MapBuilder" />); later runs find everything in place and start at once. Progress is published
/// through <see cref="Progress" /> for the preview window (or the console).
/// </summary>
internal sealed class MapPreparation
{
    /// <summary>Magellan C3-MDIR global radar mosaic, 2025 m/pixel (USGS Astrogeology; public domain).</summary>
    public const string RadarUrl = "https://planetarymaps.usgs.gov/mosaic/Venus_Magellan_C3-MDIR_Global_Mosaic_2025m.tif";

    /// <summary>Magellan global topography (GTDR), 4641 m/pixel (USGS Astrogeology; Ford, Pettengill et al.; public domain).</summary>
    public const string TopographyUrl = "https://planetarymaps.usgs.gov/mosaic/Venus_Magellan_Topography_Global_4641m_v02.tif";

    private readonly string folder;

    /// <summary>Prepares into a folder (created when missing).</summary>
    public MapPreparation(string downloadFolder)
    {
        folder = downloadFolder;
    }

    /// <summary>What is happening (thread-safe snapshot).</summary>
    public PreparationProgress Progress { get; } = new PreparationProgress();

    /// <summary>The downloaded radar mosaic.</summary>
    public string RadarPath => Path.Combine(folder, Path.GetFileName(new Uri(RadarUrl).LocalPath));

    /// <summary>The downloaded topography.</summary>
    public string TopographyPath => Path.Combine(folder, Path.GetFileName(new Uri(TopographyUrl).LocalPath));

    /// <summary>The colour map at a width (16384 or 8192).</summary>
    public string ColorMapPath(int width) => Path.Combine(folder, MapBuilder.ColorMapName(width));

    /// <summary>The relief map.</summary>
    public string ReliefMapPath => Path.Combine(folder, MapBuilder.ReliefMapName);

    /// <summary>The downloaded nomenclature (place names) zip.</summary>
    public string GazetteerPath => Path.Combine(folder, Path.GetFileName(new Uri(Gazetteer.Url).LocalPath));

    /// <summary>True when the maps are already built and the place names downloaded.</summary>
    public bool IsReady => File.Exists(ColorMapPath(16384)) && File.Exists(ColorMapPath(8192)) && File.Exists(ReliefMapPath) && File.Exists(GazetteerPath);

    /// <summary>Downloads and builds whatever is missing.</summary>
    /// <param name="cancel">Cancels (a partial download is discarded).</param>
    public void EnsureReady(CancellationToken cancel)
    {
        Directory.CreateDirectory(folder);
        if (IsReady)
        {
            Progress.Set("Maps ready", null, 1, 1);
            return;
        }

        using (HttpClient http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("CodeBrix.WebGpu-VenusMusicVideo/1.0");
            Download(http, RadarUrl, RadarPath, "Downloading the Magellan radar mosaic", 0.0, 0.42, cancel);
            Download(http, TopographyUrl, TopographyPath, "Downloading the Magellan elevation map", 0.42, 0.56, cancel);
            Download(http, Gazetteer.Url, GazetteerPath, "Downloading the Venus place names (IAU / USGS)", 0.56, 0.58, cancel);
        }

        if (File.Exists(ColorMapPath(16384)) && File.Exists(ColorMapPath(8192)) && File.Exists(ReliefMapPath))
        {
            Progress.Set("Maps ready", null, 1, 1);
            return;
        }

        MapBuilder.Build(RadarPath, TopographyPath, folder, Progress, 0.58, 1.0, cancel);
        Progress.Set("Maps ready", null, 1, 1);
    }

    /// <summary>Loads the place names into <see cref="Gazetteer.Current" /> (labels fall back to a built-in list without them).</summary>
    public void LoadGazetteer()
    {
        try
        {
            Gazetteer.Current = Gazetteer.Load(GazetteerPath);
            Console.WriteLine("Place names: " + Gazetteer.Current.Features.Count + " Venus features (IAU / USGS gazetteer)");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.WriteLine("Place names unavailable (" + e.Message + "); using the built-in list.");
        }
    }

    /// <summary>Runs <see cref="EnsureReady" /> with progress printed to the console (headless modes).</summary>
    public void EnsureReadyWithConsoleProgress()
    {
        if (IsReady) return;
        Console.WriteLine("First run: downloading the Venus data from USGS into " + folder + " and building the maps.");
        Task task = Task.Run(() => EnsureReady(CancellationToken.None));
        string last = null;
        while (!task.Wait(1000))
        {
            (string title, _, _, double overall) = Progress.Snapshot();
            string key = title + (int)(overall * 50);
            if (key != last) Console.WriteLine(Progress.Describe());
            last = key;
        }

        task.GetAwaiter().GetResult();
        Console.WriteLine("Maps ready.");
    }

    private void Download(HttpClient http, string url, string path, string title, double from, double to, CancellationToken cancel)
    {
        if (File.Exists(path)) return;
        string part = path + ".part";
        if (File.Exists(part)) File.Delete(part);
        Progress.Set(title, "connecting...", 0, from);
        using HttpResponseMessage response = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        Stopwatch clock = Stopwatch.StartNew();
        try
        {
            using (Stream input = response.Content.ReadAsStream(cancel))
            using (FileStream output = File.Create(part))
            {
                byte[] buffer = new byte[1 << 20];
                long done = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                    done += read;
                    double fraction = total.HasValue ? done / (double)total.Value : 0;
                    double rate = done / Math.Max(0.1, clock.Elapsed.TotalSeconds) / 1e6;
                    string detail = total.HasValue
                        ? string.Format(CultureInfo.InvariantCulture, "{0:N0} of {1:N0} MB  ·  {2:0.0} MB/s", done / 1e6, total.Value / 1e6, rate)
                        : string.Format(CultureInfo.InvariantCulture, "{0:N0} MB  ·  {1:0.0} MB/s", done / 1e6, rate);
                    Progress.Set(title, detail, fraction, from + ((to - from) * fraction));
                }

                if (total.HasValue && done != total.Value) throw new IOException("The download of " + url + " ended early (" + done + " of " + total.Value + " bytes).");
            }

            File.Move(part, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(part);
            }
            catch (IOException)
            {
            }

            throw;
        }
    }
}

/// <summary>A thread-safe progress snapshot.</summary>
internal sealed class PreparationProgress
{
    private readonly object gate = new object();
    private string title = "Preparing";
    private string detail;
    private double step;
    private double overall;

    /// <summary>Updates the progress.</summary>
    /// <param name="title">What is happening.</param>
    /// <param name="detail">Details, or null.</param>
    /// <param name="stepFraction">Progress of this step, 0..1.</param>
    /// <param name="overallFraction">Progress of the whole preparation, 0..1.</param>
    public void Set(string title, string detail, double stepFraction, double overallFraction)
    {
        lock (gate)
        {
            this.title = title;
            this.detail = detail;
            step = Math.Clamp(stepFraction, 0, 1);
            overall = Math.Clamp(overallFraction, 0, 1);
        }
    }

    /// <summary>A copy of the current state.</summary>
    public (string Title, string Detail, double Step, double Overall) Snapshot()
    {
        lock (gate)
        {
            return (title, detail, step, overall);
        }
    }

    /// <summary>One line for the console.</summary>
    public string Describe()
    {
        (string t, string d, _, double o) = Snapshot();
        return string.Format(CultureInfo.InvariantCulture, "[{0,3:0}%] {1}{2}", o * 100, t, d == null ? string.Empty : "  ·  " + d);
    }
}
