using System;
using CodeBrix.Audio.Playback;
using CodeBrix.Audio.Wave;

namespace VenusMusicVideo.Audio;

/// <summary>
/// Plays the song in the preview in step with the video clock (the video clock is the master, as in the rendered
/// video): the song starts <c>leadIn</c> seconds into the video and is re-seeked whenever it drifts. If the file or
/// the audio device is missing the preview carries on silently.
/// </summary>
internal sealed class SongPlayer : IDisposable
{
    private const double Tolerance = 0.12;

    private readonly string path;
    private AudioFilePlayer player;
    private bool failed;

    /// <summary>Creates the player (the file is opened on first use).</summary>
    public SongPlayer(string path)
    {
        this.path = path;
    }

    /// <summary>True when muted (M in the preview).</summary>
    public bool IsMuted { get; set; }

    private bool IsPlaying => player != null && player.PlaybackState == PlaybackState.Playing;

    /// <summary>Keeps the song in step with the video. Call every frame.</summary>
    /// <param name="songSeconds">Where the song should be (video time minus the lead-in).</param>
    /// <param name="running">False while the preview is paused.</param>
    public void Follow(double songSeconds, bool running)
    {
        if (!running || IsMuted || songSeconds < 0 || !EnsureLoaded() || songSeconds >= player.Duration.TotalSeconds - 0.05)
        {
            if (IsPlaying) player.Pause();
            return;
        }

        if (!IsPlaying)
        {
            player.Seek(TimeSpan.FromSeconds(songSeconds));
            player.Play();
        }
        else if (Math.Abs(player.Position.TotalSeconds - songSeconds) > Tolerance)
        {
            player.Seek(TimeSpan.FromSeconds(songSeconds));
        }
    }

    /// <summary>Stops and releases the player.</summary>
    public void Dispose()
    {
        player?.Dispose();
        player = null;
    }

    private bool EnsureLoaded()
    {
        if (player != null) return true;
        if (failed || path == null) return false;
        try
        {
            player = new AudioFilePlayer { IsLooping = false, Volume = 0.9f };
            player.Load(path);
            return true;
        }
        catch (Exception e)
        {
            failed = true;
            player?.Dispose();
            player = null;
            Console.WriteLine("Music unavailable in the preview: " + e.Message);
            return false;
        }
    }
}
