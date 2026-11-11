using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using VenusMusicVideo.Choreography;
using VenusMusicVideo.Maps;

namespace VenusMusicVideo.Rendering;

/// <summary>
/// Labels named places (from the USGS gazetteer) the FIRST time each one passes through the view: a small yellow
/// mark on the place, an angular yellow leader line (up and across, always the same way) and the name in red, all
/// moving with the place as the planet turns. Which stretch of time is "the first pass" is worked out from the flight itself (sampled every
/// 0.1 s while the flight's <c>"labels"</c> channel is on), never from what the preview happened to show, so a
/// render or a seek always labels the same places at the same moments. Drawn with SkiaSharp onto the frame
/// before it is read back, so the labels are part of the video.
/// </summary>
internal sealed class FeatureMarkers
{
    private const double Step = 0.1;
    private const double MinimumSeconds = 2.0;
    private const double FadeIn = 0.6;
    private const double FadeOut = 0.6;
    private const double MaxDelay = 1.0;
    private const float Margin = 0.98f;

    private readonly FlightScript script;
    private readonly List<Pass> passes = new List<Pass>();
    private readonly List<NamedFeature> candidates;

    /// <summary>
    /// Works out the labels: each candidate place's first pass through the view, then a choice of the biggest
    /// places, keeping at most <see cref="VenusLook.MaxLabelsOnScreen" /> on screen at once and new labels at least
    /// <see cref="VenusLook.LabelSpacingSeconds" /> apart, up to <see cref="VenusLook.LabelCount" /> in all.
    /// </summary>
    public FeatureMarkers(FlightScript script)
    {
        this.script = script;
        VenusLook look = script.Look;
        candidates = Candidates(look.LabelMinDiameterKm);
        int count = candidates.Count;

        // Pinned labels first: each appears at exactly its key's moment (if its place is in view then) and stays
        // until the place leaves the frame. The place is never labelled at any other time.
        HashSet<int> pinned = new HashSet<int>();
        foreach ((string name, double at) in script.PinnedLabels)
        {
            int index = candidates.FindIndex(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                NamedFeature extra = Gazetteer.Current?.Find(name) ?? FindBuiltIn(name);
                if (extra == null)
                {
                    Console.WriteLine("Pinned label: no place called '" + name + "'.");
                    continue;
                }

                candidates.Add(extra);
                index = candidates.Count - 1;
            }

            // A pinned label still obeys the horizon rule: it appears when its place comes into view. The pin
            // says WHEN that should be; the route has to deliver it (the console says when it actually happens).
            (double Start, double End)? arrival = null;
            foreach ((double Start, double End) interval in VisibleIntervals(candidates[index]))
            {
                if (arrival == null || Math.Abs(interval.Start - at) < Math.Abs(arrival.Value.Start - at)) arrival = interval;
            }

            if (arrival == null)
            {
                Console.WriteLine("Pinned label: " + name + " never comes into view.");
                continue;
            }

            if (Math.Abs(arrival.Value.Start - at) > 0.15)
            {
                Console.WriteLine("Pinned label: " + name + " comes into view at song " + FlightScript.FormatTime(arrival.Value.Start - script.LeadInSeconds)
                    + ", not " + FlightScript.FormatTime(at - script.LeadInSeconds) + " - adjust the route.");
            }

            double end = arrival.Value.Start;
            while (end < arrival.Value.End && script.Sample(end).Labels > 0.5) end += Step;
            passes.Add(Directed(new Pass(index, arrival.Value.Start, end)));
            pinned.Add(index);
        }

        double[] holdUntil = new double[count];
        for (int i = 0; i < count; i++)
        {
            holdUntil[i] = script.HeldLabels.TryGetValue(candidates[i].Name, out double until) ? until : double.NegativeInfinity;
        }

        double[] start = new double[count];
        bool[] startedOn = new bool[count];
        bool[] done = new bool[count];
        Array.Fill(start, double.NaN);
        List<Pass> firstPasses = new List<Pass>();
        for (double t = 0; t <= script.VideoSeconds; t += Step)
        {
            CameraPose pose = script.Sample(t);
            bool on = pose.Labels > 0.5;
            for (int i = 0; i < count; i++)
            {
                if (done[i] || pinned.Contains(i)) continue;
                bool visible = Project(pose, i, out _, out _);
                if (visible && double.IsNaN(start[i]))
                {
                    // The place comes into view (over the horizon or across the frame's edge). It can only be
                    // labelled on a pass that BEGINS while labels are on - never part-way through.
                    // Only a place RISING OVER THE HORIZON is labelled - not one that slides in across the
                    // frame's edge as the camera turns.
                    start[i] = t;
                    startedOn[i] = on && t >= holdUntil[i] && RoseOverHorizon(i, t);
                }
                else if ((!visible || !on) && !double.IsNaN(start[i]))
                {
                    if (startedOn[i] && t - start[i] >= MinimumSeconds)
                    {
                        firstPasses.Add(Directed(new Pass(i, start[i], t)));
                        done[i] = true;
                    }

                    start[i] = double.NaN;
                }
            }
        }

        for (int i = 0; i < count; i++)
        {
            if (!done[i] && !double.IsNaN(start[i]) && startedOn[i] && script.VideoSeconds - start[i] >= MinimumSeconds) firstPasses.Add(Directed(new Pass(i, start[i], script.VideoSeconds)));
        }

        // Priority: Ishtar Terra and Aphrodite Terra (the two "continents") first - always, the moment they come
        // into view; then the major goddess-named places (the built-in list: Ovda Regio, Atla Regio, Maat Mons ...),
        // also always; then other goddess-named places, then everything else, biggest first, up to the label
        // count. A label appears as its place comes into view; one that would crowd the labels already chosen
        // (too soon after another, too many on screen, while a continent's label is up, or physically
        // overlapping another label on screen) may wait up to MaxDelay, and is otherwise left out.
        int Tier(Pass p) => IsContinent(candidates[p.Feature]) ? -1 : IsMajorGoddess(candidates[p.Feature]) ? 0 : candidates[p.Feature].IsGoddess ? 1 : 2;
        firstPasses.Sort((a, b) =>
        {
            int tier = Tier(a).CompareTo(Tier(b));
            return tier != 0 ? tier : candidates[b.Feature].DiameterKm.CompareTo(candidates[a.Feature].DiameterKm);
        });
        foreach (Pass pass in firstPasses)
        {
            int tier = Tier(pass);
            if (pinned.Contains(pass.Feature)) continue;
            if (tier > 0 && passes.Count >= look.LabelCount) continue;
            if (tier < 0)
            {
                // A continent's label still keeps clear of a pinned label.
                if (Fits(pass.Feature, pass.Down, pass.Start, pass.End, int.MaxValue, blockedByContinent: false, look.LabelSpacingSeconds)) passes.Add(pass);
                continue;
            }

            int limit = tier == 0 ? look.MaxLabelsOnScreen + 1 : look.MaxLabelsOnScreen;
            for (double begin = pass.Start; begin <= Math.Min(pass.Start + MaxDelay, pass.End - MinimumSeconds); begin += 0.25)
            {
                if (Fits(pass.Feature, pass.Down, begin, pass.End, limit, blockedByContinent: tier > 0, look.LabelSpacingSeconds))
                {
                    passes.Add(pass with { Start = begin });
                    break;
                }
            }
        }

        passes.Sort((a, b) => a.Start.CompareTo(b.Start));
    }

    private bool Fits(int feature, bool down, double begin, double end, int limit, bool blockedByContinent, double spacing)
    {
        int overlapping = 0;
        foreach (Pass kept in passes)
        {
            // Two labels that would sit on top of each other on screen never share it.
            if (begin < kept.End && kept.Start < end && Collide(feature, down, begin, end, kept)) return false;
            if (Math.Abs(kept.Start - begin) < spacing) return false;
            bool showing = kept.Start <= begin + spacing && begin <= kept.End;
            if (!showing) continue;
            overlapping++;
            if (blockedByContinent && IsContinent(candidates[kept.Feature]) && kept.Start <= begin && begin <= kept.End) return false;
        }

        return overlapping < limit;
    }

    /// <summary>
    /// True when the label of <paramref name="feature" /> (shown from begin to end) would overlap the label of
    /// <paramref name="kept" /> on screen at any moment both are up (checked every quarter second).
    /// </summary>
    private bool Collide(int feature, bool down, double begin, double end, Pass kept)
    {
        double from = Math.Max(begin, kept.Start);
        double to = Math.Min(end, kept.End);
        for (double t = from; t <= to; t += 0.25)
        {
            CameraPose pose = script.Sample(t);
            if (!LabelBox(pose, feature, down, out SKRect a) || !LabelBox(pose, kept.Feature, kept.Down, out SKRect b)) continue;
            if (a.IntersectsWith(b)) return true;
        }

        return false;
    }

    /// <summary>
    /// Points a label's line DOWN (then right) when, pointing up, its name would leave the top of the frame while
    /// its place is still on screen - decided once for the whole pass, so a label never changes direction.
    /// </summary>
    private Pass Directed(Pass pass)
    {
        for (double t = pass.Start; t <= pass.End; t += 0.25)
        {
            if (LabelBox(script.Sample(t), pass.Feature, false, out SKRect up) && up.Top < 0)
            {
                pass = pass with { Down = true };
                break;
            }
        }

        // The pass lasts until the whole label has travelled off the frame (not just its place), so it never
        // fades out while still on screen - unless labels switch off or the place sinks below the horizon.
        double end = pass.End;
        while (end < script.VideoSeconds)
        {
            CameraPose pose = script.Sample(end);
            if (pose.Labels <= 0.5 || !LabelOnScreen(pose, pass)) break;
            end += Step;
        }

        return pass with { End = end };
    }

    private bool LabelOnScreen(CameraPose pose, Pass pass)
    {
        if (!ProjectPlace(pose, candidates[pass.Feature], out float nx, out float ny, requireOnScreen: false)) return false;
        float scale = script.Width / 1080f;
        float x = (nx + 1) * 0.5f * script.Width;
        float y = (1 - ny) * 0.5f * script.Height;
        string name = candidates[pass.Feature].Name;
        float size = (IsFeatured(name) ? 62 : 38) * scale;
        float right = x + ((120 + 150) * scale) + (name.Length * size * 0.56f);
        float top = pass.Down ? y - (12 * scale) : y - (120 * scale) - (size * 1.15f);
        float bottom = pass.Down ? y + (120 * scale) : y + (12 * scale);
        return right > 0 && x - (12 * scale) < script.Width && bottom > 0 && top < script.Height;
    }

    /// <summary>The screen rectangle a place's label covers (diamond, leader line and name, with a margin).</summary>
    private bool LabelBox(CameraPose pose, int feature, bool down, out SKRect box)
    {
        box = SKRect.Empty;
        if (!Project(pose, feature, out float nx, out float ny)) return false;
        float scale = script.Width / 1080f;
        float x = (nx + 1) * 0.5f * script.Width;
        float y = (1 - ny) * 0.5f * script.Height;
        string name = candidates[feature].Name;
        float size = (IsFeatured(name) ? 62 : 38) * scale;
        float textWidth = name.Length * size * 0.56f;
        float margin = 10 * scale;
        float right = x + ((120 + 150) * scale) + textWidth + margin;
        box = down
            ? new SKRect(x - (12 * scale) - margin, Math.Min(y - (12 * scale), y + (120 * scale) - (12 * scale) - (size * 1.15f)) - margin, right, y + (120 * scale) + margin)
            : new SKRect(x - (12 * scale) - margin, y - (120 * scale) - (size * 1.15f) - margin, right, y + (12 * scale) + margin);
        return true;
    }

    private static bool IsFeatured(string name) => name.Equals("Ishtar Terra", StringComparison.OrdinalIgnoreCase) || name.Equals("Aphrodite Terra", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the place was below the horizon a moment before <paramref name="t" />.</summary>
    private bool RoseOverHorizon(int feature, double t)
    {
        CameraPose before = script.Sample(Math.Max(0, t - Step));
        NamedFeature place = candidates[feature];
        Vector3D point = FlightScript.Spin(FlightScript.Direction(place.Lat, place.Lon), -before.SpinAngle);
        return Vector3D.Dot(point, before.Position) <= 1.0;
    }

    /// <summary>When a place is in view (video times), sampled every 0.1 s.</summary>
    public List<(double Start, double End)> VisibleIntervals(NamedFeature place)
    {
        List<(double, double)> intervals = new List<(double, double)>();
        double begin = double.NaN;
        for (double t = 0; t <= script.VideoSeconds; t += Step)
        {
            bool visible = ProjectPlace(script.Sample(t), place, out _, out _);
            if (visible && double.IsNaN(begin)) begin = t;
            else if (!visible && !double.IsNaN(begin))
            {
                intervals.Add((begin, t));
                begin = double.NaN;
            }
        }

        if (!double.IsNaN(begin)) intervals.Add((begin, script.VideoSeconds));
        return intervals;
    }

    /// <summary>The place of a labelled pass.</summary>
    public NamedFeature PlaceOf(Pass pass) => candidates[pass.Feature];

    /// <summary>The name of a labelled place.</summary>
    public string NameOf(Pass pass) => candidates[pass.Feature].Name;

    private static NamedFeature FindBuiltIn(string name)
    {
        foreach ((string n, double lat, double lon, bool goddess) in VenusFeatures.All)
        {
            if (n.Equals(name, StringComparison.OrdinalIgnoreCase)) return new NamedFeature(n, lat, lon, 1000, string.Empty, goddess ? "goddess" : string.Empty);
        }

        return null;
    }

    private static bool IsContinent(NamedFeature place) =>
        place.Name.Equals("Ishtar Terra", StringComparison.OrdinalIgnoreCase) || place.Name.Equals("Aphrodite Terra", StringComparison.OrdinalIgnoreCase);

    private static bool IsMajorGoddess(NamedFeature place) =>
        VenusFeatures.All.Any(f => f.Goddess && string.Equals(f.Name, place.Name, StringComparison.OrdinalIgnoreCase));

    private static List<NamedFeature> Candidates(double minimumDiameterKm)
    {
        if (Gazetteer.Current != null)
        {
            return Gazetteer.Current.Features.Where(f => f.IsGoddess && f.DiameterKm >= minimumDiameterKm).ToList();
        }

        // Without the gazetteer: the built-in list of major places.
        return VenusFeatures.All.Where(f => f.Goddess).Select(f => new NamedFeature(f.Name, f.Lat, f.Lon, 1000, string.Empty, "goddess")).ToList();
    }

    /// <summary>The labelled passes (place index, start and end video time), in order.</summary>
    public IReadOnlyList<Pass> Passes => passes;

    /// <summary>Draws the labels that are showing at the pose's time.</summary>
    /// <param name="canvas">The frame's canvas.</param>
    /// <param name="width">Frame width.</param>
    /// <param name="height">Frame height.</param>
    /// <param name="pose">The camera.</param>
    public void Draw(SKCanvas canvas, int width, int height, CameraPose pose)
    {
        double t = pose.VideoTime;
        float scale = width / 1080f;
        using SKTypeface face = SKTypeface.FromFamilyName("Helvetica Neue", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright) ?? SKTypeface.Default;
        using SKFont font = new SKFont(face, 38 * scale);
        using SKFont featured = new SKFont(face, 62 * scale);
        foreach (Pass pass in passes)
        {
            if (t < pass.Start || t > pass.End) continue;
            // Drawn wherever the place is (in front, above the horizon), even past the frame's edge: a label
            // travels off the screen with its place instead of fading.
            if (!ProjectPlace(pose, candidates[pass.Feature], out float nx, out float ny, requireOnScreen: false)) continue;
            float appear = (float)Math.Clamp((t - pass.Start) / FadeIn, 0, 1);
            float alpha = appear * (float)Math.Clamp((pass.End - t) / FadeOut, 0, 1);
            if (alpha <= 0.01f) continue;
            float x = (nx + 1) * 0.5f * width;
            float y = (1 - ny) * 0.5f * height;
            NamedFeature place = candidates[pass.Feature];
            bool ishtar = IsFeatured(place.Name); // Ishtar Terra and Aphrodite Terra
            // The loudspeaker the song is spreading from pulses with the music.
            float pulse = -1;
            SoundWave.Source speaker = script.Wave.CurrentSource(t);
            if (speaker != null && speaker.Place.Name == place.Name && Audio.SongEnvelope.Current != null)
            {
                pulse = Audio.SongEnvelope.Current.At(t - script.LeadInSeconds);
            }

            DrawMarker(canvas, ishtar ? featured : font, scale, x, y, place.Name, Ease(appear), alpha, ishtar || place.IsGoddess ? Featured : Normal, pulse, pass.Down);
        }
    }

    private static float Ease(float v) => v * v * (3 - (2 * v));

    /// <summary>The label colour for places not named after a goddess: red.</summary>
    private static readonly SKColor Normal = new SKColor(255, 52, 52);

    /// <summary>Goddess names (Ishtar Terra and Aphrodite Terra larger): a deep, saturated magenta, dark enough to read.</summary>
    private static readonly SKColor Featured = new SKColor(236, 26, 196);

    private static void DrawMarker(SKCanvas canvas, SKFont font, float scale, float x, float y, string name, float grow, float alpha, SKColor color, float pulse, bool down)
    {
        // Up at 45 degrees and then across to the right - or, for a place near the top of the frame, DOWN and
        // across, so its name stays on screen. The direction is fixed for the whole pass: a label never flips.
        const float side = 1;
        float rise = 120 * scale;
        float run = 150 * scale;
        float textWidth = font.MeasureText(name);
        SKPoint a = new SKPoint(x, y);
        SKPoint b = new SKPoint(x + (side * rise), down ? y + rise : y - rise);
        SKPoint c = new SKPoint(b.X + (side * (run + textWidth)), b.Y);

        // The line draws itself out from the place: the diagonal first, then the run under the name.
        float diagonal = rise * 1.41421f;
        float total = diagonal + run + textWidth;
        float drawn = total * grow;
        using SKPathBuilder builder = new SKPathBuilder();
        builder.MoveTo(a);
        if (drawn <= diagonal)
        {
            builder.LineTo(a.X + ((b.X - a.X) * drawn / diagonal), a.Y + ((b.Y - a.Y) * drawn / diagonal));
        }
        else
        {
            builder.LineTo(b);
            builder.LineTo(b.X + (side * (drawn - diagonal)), b.Y);
        }

        using SKPath path = builder.Detach();

        byte A(float v) => (byte)Math.Clamp(v * alpha * 255, 0, 255);
        using SKPaint shadow = new SKPaint { Color = new SKColor(20, 4, 16, A(0.55f)), StrokeWidth = 7 * scale, Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeJoin = SKStrokeJoin.Miter };
        using SKPaint line = new SKPaint { Color = new SKColor(255, 222, 40, A(1)), StrokeWidth = 3.2f * scale, Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeJoin = SKStrokeJoin.Miter };
        using SKPaint dot = new SKPaint { Color = new SKColor(255, 222, 40, A(1)), Style = SKPaintStyle.Stroke, StrokeWidth = 3.2f * scale, IsAntialias = true };
        canvas.DrawPath(path, shadow);
        canvas.DrawPath(path, line);

        // The loudspeaker: a violet glow and a diamond that swell with the song.
        if (pulse >= 0)
        {
            float glow = (16 + (26 * Math.Min(pulse, 1.5f))) * scale;
            using SKPaint halo = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateRadialGradient(new SKPoint(x, y), glow, new[] { new SKColor(170, 80, 255, A(0.85f)), new SKColor(120, 40, 255, 0) }, SKShaderTileMode.Clamp),
            };
            canvas.DrawCircle(x, y, glow, halo);
        }

        // A small diamond on the place itself.
        float r = (9 + (pulse >= 0 ? 7 * Math.Min(pulse, 1.5f) : 0)) * scale;
        using (SKPathBuilder diamondBuilder = new SKPathBuilder())
        {
            diamondBuilder.MoveTo(x, y - r);
            diamondBuilder.LineTo(x + r, y);
            diamondBuilder.LineTo(x, y + r);
            diamondBuilder.LineTo(x - r, y);
            diamondBuilder.Close();
            using SKPath diamond = diamondBuilder.Detach();
            canvas.DrawPath(diamond, shadow);
            canvas.DrawPath(diamond, dot);
        }

        // The name sits on the horizontal run, once the line has reached it.
        float textAlpha = Math.Clamp((drawn - diagonal) / (run * 0.6f), 0, 1);
        if (textAlpha <= 0) return;
        float tx = b.X + run;
        float ty = b.Y - (12 * scale);
        using SKPaint outline = new SKPaint { Color = new SKColor(24, 4, 12, A(0.8f * textAlpha)), Style = SKPaintStyle.Stroke, StrokeWidth = font.Size * 0.17f, StrokeJoin = SKStrokeJoin.Round, IsAntialias = true };
        using SKPaint text = new SKPaint { Color = color.WithAlpha(A(textAlpha)), IsAntialias = true };
        canvas.DrawText(name, tx, ty, SKTextAlign.Left, font, outline);
        canvas.DrawText(name, tx, ty, SKTextAlign.Left, font, text);
    }

    /// <summary>Projects a place into normalised screen coordinates; false when it is off screen or behind the horizon.</summary>
    private bool Project(CameraPose pose, int feature, out float x, out float y) => ProjectPlace(pose, candidates[feature], out x, out y);

    private bool ProjectPlace(CameraPose pose, NamedFeature place, out float x, out float y, bool requireOnScreen = true)
    {
        x = 0;
        y = 0;
        double lat = place.Lat;
        double lon = place.Lon;
        Vector3D point = FlightScript.Spin(FlightScript.Direction(lat, lon), -pose.SpinAngle);
        if (Vector3D.Dot(point, pose.Position) <= 1.0) return false; // on the far side of the horizon
        Vector3D v = point - pose.Position;
        double z = Vector3D.Dot(v, pose.Forward);
        if (z <= 0) return false;
        double tanY = Math.Tan(script.FieldOfViewDegrees * Math.PI / 360);
        double tanX = tanY * script.Width / script.Height;
        x = (float)(Vector3D.Dot(v, pose.Right) / (z * tanX));
        y = (float)(Vector3D.Dot(v, pose.Up) / (z * tanY));
        return !requireOnScreen || (Math.Abs(x) < Margin && Math.Abs(y) < Margin);
    }

    /// <summary>One labelled pass.</summary>
    internal readonly record struct Pass(int Feature, double Start, double End, bool Down = false);
}
