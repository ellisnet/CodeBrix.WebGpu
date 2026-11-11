using System;
using System.Collections.Generic;
using VenusMusicVideo.Choreography;
using VenusMusicVideo.Maps;

namespace VenusMusicVideo.Rendering;

/// <summary>
/// The song travelling across Venus as a sound wave, loudspeaker to loudspeaker. The first loudspeaker is Ishtar
/// Terra's marker, from the moment its label appears. Each next loudspeaker is the next goddess-named place to be
/// labelled: the wave from the current one travels at exactly the speed that makes its front reach that place the
/// moment its label appears, and from then on that marker is the loudspeaker (the rings already sent out keep
/// travelling). A point at angular distance d from a loudspeaker shows the song as it was d / speed seconds
/// earlier - so the rings ARE the song's loudness, beat by beat, spreading out.
/// </summary>
internal sealed class SoundWave
{
    /// <summary>The most loudspeakers the shader looks at (the current one and those before it).</summary>
    public const int MaxSources = 4;

    private readonly List<Source> sources = new List<Source>();

    /// <summary>Builds the chain from the flight's labels.</summary>
    public SoundWave(FlightScript script)
    {
        VenusLook look = script.Look;
        double minSpeed = look.WaveMinSpeedDegrees * Math.PI / 180;
        double maxSpeed = look.WaveMaxSpeedDegrees * Math.PI / 180;
        FeatureMarkers markers = script.Markers;
        IReadOnlyList<FeatureMarkers.Pass> passes = markers.Passes;
        int first = -1;
        for (int i = 0; i < passes.Count; i++)
        {
            if (markers.PlaceOf(passes[i]).Name.Equals("Ishtar Terra", StringComparison.OrdinalIgnoreCase))
            {
                first = i;
                break;
            }
        }

        if (first < 0) return;
        NamedFeature current = markers.PlaceOf(passes[first]);
        double currentStart = passes[first].Start;
        for (int i = first + 1; i < passes.Count; i++)
        {
            NamedFeature next = markers.PlaceOf(passes[i]);
            if (!next.IsGoddess) continue;
            double gap = passes[i].Start - currentStart;
            if (gap < 0.5) continue;
            double distance = Angle(current, next);
            double speed = distance / gap;
            if (speed < minSpeed || speed > maxSpeed) continue; // a hand-over that would look wrong: skip that place
            sources.Add(new Source(current, currentStart, passes[i].Start, speed));
            current = next;
            currentStart = passes[i].Start;
        }

        sources.Add(new Source(current, currentStart, double.PositiveInfinity, look.WaveFinalSpeedDegrees * Math.PI / 180));
    }

    /// <summary>The loudspeakers in order.</summary>
    public IReadOnlyList<Source> Sources => sources;

    /// <summary>The loudspeaker sounding at a video time, or null.</summary>
    public Source CurrentSource(double videoSeconds)
    {
        foreach (Source source in sources)
        {
            if (videoSeconds >= source.Start && videoSeconds < source.End) return source;
        }

        return null;
    }

    /// <summary>Fills up to <see cref="MaxSources" /> loudspeakers whose rings may be visible at a time (newest first).</summary>
    public int Visible(double videoSeconds, Span<Source> into)
    {
        int count = 0;
        for (int i = sources.Count - 1; i >= 0 && count < into.Length; i--)
        {
            if (sources[i].Start <= videoSeconds) into[count++] = sources[i];
        }

        return count;
    }

    /// <summary>Great-circle angle between two places, radians.</summary>
    public static double Angle(NamedFeature a, NamedFeature b)
    {
        Vector3D pa = FlightScript.Direction(a.Lat, a.Lon);
        Vector3D pb = FlightScript.Direction(b.Lat, b.Lon);
        return Math.Acos(Math.Clamp(Vector3D.Dot(pa, pb), -1, 1));
    }

    /// <summary>One loudspeaker: where, when it sounds (video times) and how fast its rings travel (radians/s).</summary>
    internal sealed record Source(NamedFeature Place, double Start, double End, double Speed);
}
