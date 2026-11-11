using System;
using System.Diagnostics;
using CodeBrix.Audio.Wave;

namespace VenusMusicVideo.Audio;

/// <summary>
/// The song's loudness over time, sampled <see cref="Rate" /> times a second - what the sound-wave ripples are
/// drawn from, so each ring on the planet is a real moment of the song. Built from the decoded MP3 (CodeBrix.Audio):
/// the RMS of each 1/120 s window, compressed (square root) so quiet passages still show, normalised to the loud
/// passages, and with every sudden rise (a beat, a consonant, a new note) accented so it becomes a bright ring.
/// </summary>
internal sealed class SongEnvelope
{
    /// <summary>Samples per second.</summary>
    public const int Rate = 120;

    private SongEnvelope(float[] values)
    {
        Values = values;
    }

    /// <summary>The envelope loaded at start-up, or null when the song could not be read.</summary>
    public static SongEnvelope Current { get; set; }

    /// <summary>The envelope, 0..~1.5, one value per 1/<see cref="Rate" /> s of song.</summary>
    public float[] Values { get; }

    /// <summary>The envelope at a song time (linear between samples; 0 outside the song).</summary>
    public float At(double songSeconds)
    {
        double f = songSeconds * Rate;
        if (f < 0 || f >= Values.Length - 1) return 0;
        int i = (int)f;
        float t = (float)(f - i);
        return (Values[i] * (1 - t)) + (Values[i + 1] * t);
    }

    /// <summary>Decodes the song and builds its envelope.</summary>
    /// <param name="path">The song (MP3, WAV, Ogg or FLAC).</param>
    /// <returns>The envelope.</returns>
    public static SongEnvelope Load(string path)
    {
        Stopwatch clock = Stopwatch.StartNew();
        using AudioFileReader reader = new AudioFileReader(path);
        int channels = reader.WaveFormat.Channels;
        int window = Math.Max(1, reader.WaveFormat.SampleRate / Rate) * channels;
        float[] buffer = new float[window * 64];
        System.Collections.Generic.List<float> rms = new System.Collections.Generic.List<float>();
        double sum = 0;
        int inWindow = 0;
        int read;
        while ((read = reader.Read(buffer.AsSpan())) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                sum += buffer[i] * buffer[i];
                if (++inWindow == window)
                {
                    rms.Add((float)Math.Sqrt(sum / window));
                    sum = 0;
                    inWindow = 0;
                }
            }
        }

        float[] level = rms.ToArray();

        // Compress, then normalise to the loud passages (the 97th percentile = 1).
        for (int i = 0; i < level.Length; i++) level[i] = MathF.Sqrt(level[i]);
        float[] sorted = (float[])level.Clone();
        Array.Sort(sorted);
        float loud = Math.Max(1e-4f, sorted[(int)(sorted.Length * 0.97)]);
        for (int i = 0; i < level.Length; i++) level[i] /= loud;

        // Accent each rise above the recent average (about a quarter second), so beats and syllables stand out.
        float[] values = new float[level.Length];
        float average = 0;
        for (int i = 0; i < level.Length; i++)
        {
            average += (level[i] - average) * (1f / 30f);
            float rise = Math.Max(0, level[i] - average);
            values[i] = (0.28f * level[i] * level[i]) + (2.4f * rise);
        }

        Console.WriteLine("Song envelope: " + values.Length + " samples (" + (values.Length / (double)Rate).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s) in " + clock.ElapsedMilliseconds + " ms");
        return new SongEnvelope(values);
    }
}
