using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using VenusMusicVideo.Choreography;
using VenusMusicVideo.Rendering;

namespace VenusMusicVideo.Video;

/// <summary>
/// Renders the flight into an MP4: every frame at an exact 1/fps step of the video clock (however long it takes),
/// read back and piped straight into ffmpeg (H.264, CRF 16, yuv420p) together with the song, which is delayed by
/// the lead-in (real silence before it) or, for a render that starts later, cut in at the right place. Work
/// happens one frame per <see cref="Step" />, so the preview window can show progress (or a headless loop can
/// just call it until done).
/// </summary>
internal sealed class VideoExporter : IDisposable
{
    private readonly FlightScript script;
    private readonly VenusRenderer renderer;
    private readonly int firstFrame;
    private readonly int lastFrame;
    private readonly byte[] pixels;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private Process ffmpeg;
    private Stream input;

    /// <summary>Starts ffmpeg.</summary>
    /// <param name="script">The flight.</param>
    /// <param name="renderer">The renderer (its frame is the video's size).</param>
    /// <param name="song">The song, or null for silence.</param>
    /// <param name="output">The MP4 to write.</param>
    /// <param name="fromSeconds">The video time of the first frame.</param>
    /// <param name="toSeconds">The video time to stop at (exclusive); null = the end.</param>
    public VideoExporter(FlightScript script, VenusRenderer renderer, string song, string output, double fromSeconds = 0, double? toSeconds = null)
    {
        this.script = script;
        this.renderer = renderer;
        OutputPath = output;
        firstFrame = Math.Clamp((int)Math.Round(fromSeconds * script.Fps), 0, script.FrameCount - 1);
        lastFrame = Math.Clamp((int)Math.Round((toSeconds ?? script.VideoSeconds) * script.Fps), firstFrame + 1, script.FrameCount);
        pixels = new byte[renderer.Width * renderer.Height * 4];
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".");

        double start = firstFrame / (double)script.Fps;
        double duration = (lastFrame - firstFrame) / (double)script.Fps;
        double songAt = script.LeadInSeconds - start;
        ProcessStartInfo info = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardInput = true };
        void Add(params string[] args)
        {
            foreach (string a in args) info.ArgumentList.Add(a);
        }

        string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        Add("-y", "-hide_banner", "-loglevel", "error");
        Add("-f", "rawvideo", "-pix_fmt", renderer.IsBgra ? "bgra" : "rgba", "-s", renderer.Width + "x" + renderer.Height, "-r", script.Fps.ToString(CultureInfo.InvariantCulture), "-i", "pipe:0");
        bool withSong = song != null && File.Exists(song) && songAt < script.SongSeconds;
        if (withSong)
        {
            if (songAt > 0)
            {
                Add("-i", song, "-filter_complex", "[1:a]adelay=delays=" + F(songAt * 1000) + ":all=1,apad[a]", "-map", "0:v:0", "-map", "[a]");
            }
            else
            {
                Add("-ss", F(-songAt), "-i", song, "-filter_complex", "[1:a]apad[a]", "-map", "0:v:0", "-map", "[a]");
            }

            Add("-c:a", "aac", "-b:a", "256k");
        }

        Add("-c:v", "libx264", "-preset", "slow", "-crf", "16", "-pix_fmt", "yuv420p", "-profile:v", "high", "-r", script.Fps.ToString(CultureInfo.InvariantCulture));
        Add("-t", F(duration), "-movflags", "+faststart", output);
        ffmpeg = Process.Start(info) ?? throw new InvalidOperationException("Could not start ffmpeg (is it installed and on the PATH?).");
        input = ffmpeg.StandardInput.BaseStream;
        FrameCount = lastFrame - firstFrame;
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "Video: {0} frames ({1}) at {2}x{3}, {4} fps, from video {5} -> {6}",
            FrameCount, FlightScript.FormatTime(duration), renderer.Width, renderer.Height, script.Fps, FlightScript.FormatTime(start), output));
    }

    /// <summary>The MP4.</summary>
    public string OutputPath { get; }

    /// <summary>Frames to render.</summary>
    public int FrameCount { get; }

    /// <summary>Frames done.</summary>
    public int FramesDone { get; private set; }

    /// <summary>True when every frame is written and ffmpeg has finished.</summary>
    public bool IsFinished { get; private set; }

    /// <summary>The pose of the frame rendered last.</summary>
    public CameraPose LastPose { get; private set; }

    /// <summary>A progress line.</summary>
    public string Progress
    {
        get
        {
            double perFrame = FramesDone == 0 ? 0 : clock.Elapsed.TotalSeconds / FramesDone;
            TimeSpan left = TimeSpan.FromSeconds(perFrame * (FrameCount - FramesDone));
            return string.Format(CultureInfo.InvariantCulture, "Rendering frame {0:N0} of {1:N0} ({2:P0}), about {3} left", FramesDone, FrameCount, FramesDone / (double)Math.Max(1, FrameCount), left.ToString(@"h\:mm\:ss"));
        }
    }

    /// <summary>Renders and writes the next frame; after the last one, waits for ffmpeg to finish.</summary>
    public void Step()
    {
        if (IsFinished) return;
        int index = firstFrame + FramesDone;
        double t = index / (double)script.Fps;
        LastPose = script.Sample(t);
        renderer.Render(script, LastPose, script.FieldOfViewDegrees);
        renderer.ReadPixels(pixels);
        input.Write(pixels, 0, pixels.Length);
        FramesDone++;
        if (FramesDone < FrameCount) return;

        input.Flush();
        input.Close();
        ffmpeg.WaitForExit();
        int code = ffmpeg.ExitCode;
        ffmpeg.Dispose();
        ffmpeg = null;
        IsFinished = true;
        if (code != 0) throw new InvalidOperationException("ffmpeg failed (exit code " + code + ").");
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "Wrote {0} ({1} frames in {2:F1} min)", OutputPath, FrameCount, clock.Elapsed.TotalMinutes));
    }

    /// <summary>Stops ffmpeg if it is still running (a cancelled render leaves a partial file).</summary>
    public void Dispose()
    {
        if (ffmpeg == null) return;
        try
        {
            input.Close();
            if (!ffmpeg.WaitForExit(5000)) ffmpeg.Kill();
        }
        catch (Exception)
        {
        }

        ffmpeg.Dispose();
        ffmpeg = null;
    }
}
