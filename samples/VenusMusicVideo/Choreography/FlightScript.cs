using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace VenusMusicVideo.Choreography;

/// <summary>
/// The flight around Venus, read from <c>assets/flight.json</c>: the video's timing, the look, and the camera keys.
/// <para>
/// TIME: keys are given in SONG time (<c>"t": 72.5</c> or <c>"t": "1:12.5"</c>); the video starts
/// <see cref="LeadInSeconds" /> before the song (negative song times are the descent), and runs
/// <see cref="TailSeconds" /> past its end.
/// </para>
/// <para>
/// WHERE: <c>"over": "Maxwell Montes"</c> or <c>"over": [lat, lon]</c> puts the camera above that point of the
/// SURFACE at the key's time (the planet spins, so this is the way to sync a place to a moment).
/// <c>"lat"</c>/<c>"lon"</c> instead give a direction in SPACE (stars fixed): keys that hold the same lat/lon watch
/// the planet turn beneath. <c>"alt"</c> is the altitude in km (interpolated logarithmically).
/// </para>
/// <para>
/// LOOK: <c>"gaze"</c> 0 looks at the planet's centre (north up), 1 looks ahead along the flight, <c>"pitch"</c>
/// degrees below the horizon; values between blend. <c>"roll"</c> banks the picture (degrees).
/// </para>
/// Every channel is a monotone cubic through the keys, so motion is smooth and never overshoots a key.
/// </summary>
internal sealed class FlightScript
{
    /// <summary>Venus' radius in km (the unit of the renderer's space is one radius).</summary>
    public const double RadiusKm = 6051.8;

    private MonotoneCurve logAltitude;
    private MonotoneCurve latitude;
    private MonotoneCurve longitude;
    private MonotoneCurve gaze;
    private MonotoneCurve pitch;
    private MonotoneCurve roll;
    private MonotoneCurve spinRate;
    private MonotoneCurve phase;
    private MonotoneCurve sunlight;
    private MonotoneCurve heading;
    private MonotoneCurve labels;
    private MonotoneCurve northUp;
    private double[] spinTable;
    private const double SpinTableStep = 1.0 / 240;

    /// <summary>Seconds of video before the song starts (the descent).</summary>
    public double LeadInSeconds { get; private set; } = 8;

    /// <summary>Seconds of video after the song ends (pulling away).</summary>
    public double TailSeconds { get; private set; } = 6;

    /// <summary>Frames per second of the video.</summary>
    public int Fps { get; private set; } = 30;

    /// <summary>The video's width.</summary>
    public int Width { get; private set; } = 1080;

    /// <summary>The video's height.</summary>
    public int Height { get; private set; } = 1920;

    /// <summary>
    /// The planet's spin when no key sets <c>"spin"</c>, degrees per second (positive = retrograde, like Venus: the
    /// surface drifts west under a camera fixed in space). Keys can change the rate; the angle is its integral.
    /// </summary>
    public double SpinDegreesPerSecond { get; private set; } = 1.5;

    /// <summary>How many seconds either side the direction of travel is measured over (larger = gentler turns).</summary>
    public double TravelSmoothingSeconds { get; private set; } = 2.5;

    /// <summary>The camera's vertical field of view, degrees.</summary>
    public double FieldOfViewDegrees { get; private set; } = 55;

    /// <summary>Seconds of fade from black at the start.</summary>
    public double FadeInSeconds { get; private set; } = 1;

    /// <summary>Seconds of fade to black at the end.</summary>
    public double FadeOutSeconds { get; private set; } = 2.5;

    /// <summary>The look of the planet, haze and stars.</summary>
    public VenusLook Look { get; private set; } = new VenusLook();

    /// <summary>Labels pinned to a moment by keys like <c>{ "t": "0:38", "label": "Ishtar Terra" }</c> (video times).</summary>
    public IReadOnlyList<(string Name, double VideoTime)> PinnedLabels { get; private set; } = Array.Empty<(string, double)>();

    /// <summary>
    /// Places held back by keys like <c>{ "t": "2:00", "holdLabels": ["Dekla Tessera"] }</c>: they are not labelled
    /// before that moment (video time), and get their label on their first pass after it.
    /// </summary>
    public IReadOnlyDictionary<string, double> HeldLabels { get; private set; } = new Dictionary<string, double>();

    /// <summary>The keys, in order.</summary>
    public IReadOnlyList<FlightKey> Keys { get; private set; }

    private Rendering.FeatureMarkers markers;

    private Rendering.SoundWave wave;

    /// <summary>The sound wave's loudspeakers (worked out on first use, after the labels).</summary>
    public Rendering.SoundWave Wave => wave ??= new Rendering.SoundWave(this);

    /// <summary>The place labels (worked out on first use, after <see cref="SongSeconds" /> is set).</summary>
    public Rendering.FeatureMarkers Markers => markers ??= new Rendering.FeatureMarkers(this);

    /// <summary>The song's length in seconds (set by the caller; the video length depends on it).</summary>
    public double SongSeconds { get; set; } = 259.2;

    /// <summary>The video's length in seconds.</summary>
    public double VideoSeconds => LeadInSeconds + SongSeconds + TailSeconds;

    /// <summary>The number of video frames.</summary>
    public int FrameCount => (int)Math.Round(VideoSeconds * Fps);

    /// <summary>The planet's spin angle (radians) at a video time: the integral of the keyed spin rate.</summary>
    public double SpinAngle(double videoSeconds)
    {
        if (spinTable == null) return SpinDegreesPerSecond * videoSeconds * Math.PI / 180;
        double f = videoSeconds / SpinTableStep;
        if (f <= 0) return spinTable[0] + (videoSeconds * spinRate.Evaluate(0) * Math.PI / 180);
        int i = (int)f;
        if (i >= spinTable.Length - 1)
        {
            double end = (spinTable.Length - 1) * SpinTableStep;
            return spinTable[^1] + ((videoSeconds - end) * spinRate.Evaluate(end) * Math.PI / 180);
        }

        return spinTable[i] + ((spinTable[i + 1] - spinTable[i]) * (f - i));
    }

    /// <summary>Reads a flight file.</summary>
    /// <param name="path">The JSON file.</param>
    /// <returns>The flight.</returns>
    /// <exception cref="FormatException">The file is not a valid flight (the message says where).</exception>
    public static FlightScript Load(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        JsonElement root = document.RootElement;
        FlightScript script = new FlightScript();
        script.LeadInSeconds = Number(root, "leadInSeconds", script.LeadInSeconds);
        script.TailSeconds = Number(root, "tailSeconds", script.TailSeconds);
        script.Fps = (int)Number(root, "fps", script.Fps);
        script.Width = (int)Number(root, "width", script.Width);
        script.Height = (int)Number(root, "height", script.Height);
        script.SpinDegreesPerSecond = Number(root, "spinDegreesPerSecond", script.SpinDegreesPerSecond);
        script.FieldOfViewDegrees = Number(root, "fieldOfViewDegrees", script.FieldOfViewDegrees);
        script.TravelSmoothingSeconds = Math.Max(0.05, Number(root, "travelSmoothingSeconds", script.TravelSmoothingSeconds));
        script.FadeInSeconds = Number(root, "fadeInSeconds", script.FadeInSeconds);
        script.FadeOutSeconds = Number(root, "fadeOutSeconds", script.FadeOutSeconds);
        if (root.TryGetProperty("look", out JsonElement look)) script.Look = VenusLook.Read(look);
        if (!root.TryGetProperty("keys", out JsonElement keys) || keys.ValueKind != JsonValueKind.Array || keys.GetArrayLength() == 0)
        {
            throw new FormatException("flight.json needs a non-empty \"keys\" array.");
        }

        List<FlightKey> list = new List<FlightKey>();
        int index = 0;
        foreach (JsonElement k in keys.EnumerateArray())
        {
            try
            {
                list.Add(script.ReadKey(k, index));
            }
            catch (Exception e) when (e is not FormatException)
            {
                throw new FormatException("Key " + index + ": " + e.Message, e);
            }

            index++;
        }

        list.Sort((a, b) => a.VideoTime.CompareTo(b.VideoTime));
        for (int i = 1; i < list.Count; i++)
        {
            if (list[i].VideoTime - list[i - 1].VideoTime < 1e-3) throw new FormatException("Two keys at the same time (song " + FormatTime(list[i].VideoTime - script.LeadInSeconds) + ").");
        }

        // Spin, phase and sunlight carry over from the previous key when a key leaves them out.
        double carrySpin = script.SpinDegreesPerSecond;
        double carryPhase = 0;
        double carrySun = 0;
        double carryHeading = 0;
        double carryLabels = 0;
        double carryNorth = 0;
        double carryGaze = 0;
        double carryPitch = 15;
        double carryRoll = 0;
        foreach (FlightKey k in list)
        {
            carryGaze = k.Gaze = double.IsNaN(k.Gaze) ? carryGaze : k.Gaze;
            carryPitch = k.Pitch = double.IsNaN(k.Pitch) ? carryPitch : k.Pitch;
            carryRoll = k.Roll = double.IsNaN(k.Roll) ? carryRoll : k.Roll;
            carryNorth = k.NorthUp = double.IsNaN(k.NorthUp) ? carryNorth : Math.Clamp(k.NorthUp, 0, 1);
            carryHeading = k.Heading = double.IsNaN(k.Heading) ? carryHeading : k.Heading;
            carryLabels = k.Labels = double.IsNaN(k.Labels) ? carryLabels : Math.Clamp(k.Labels, 0, 1);
            carrySpin = k.Spin = double.IsNaN(k.Spin) ? carrySpin : k.Spin;
            carryPhase = k.Phase = double.IsNaN(k.Phase) ? carryPhase : k.Phase;
            carrySun = k.Sunlight = double.IsNaN(k.Sunlight) ? carrySun : Math.Clamp(k.Sunlight, 0, 1);
        }

        double[] t = list.ConvertAll(k => k.VideoTime).ToArray();
        script.spinRate = new MonotoneCurve(t, list.ConvertAll(k => k.Spin).ToArray());
        script.phase = new MonotoneCurve(t, list.ConvertAll(k => k.Phase).ToArray());
        script.sunlight = new MonotoneCurve(t, list.ConvertAll(k => k.Sunlight).ToArray());
        script.heading = new MonotoneCurve(t, list.ConvertAll(k => k.Heading).ToArray());
        script.labels = new MonotoneCurve(t, list.ConvertAll(k => k.Labels).ToArray());
        script.northUp = new MonotoneCurve(t, list.ConvertAll(k => k.NorthUp).ToArray());

        // The spin angle: the integral of the rate (trapezoids, 1/240 s), from video time 0 to past the last key.
        double tableEnd = Math.Max(list[^1].VideoTime, script.LeadInSeconds + script.SongSeconds + script.TailSeconds) + 30;
        int steps = (int)Math.Ceiling(tableEnd / SpinTableStep) + 1;
        script.spinTable = new double[steps];
        double previousRate = script.spinRate.Evaluate(0);
        for (int i = 1; i < steps; i++)
        {
            double rate = script.spinRate.Evaluate(i * SpinTableStep);
            script.spinTable[i] = script.spinTable[i - 1] + ((previousRate + rate) * 0.5 * SpinTableStep * Math.PI / 180);
            previousRate = rate;
        }

        // "over" keys: the space longitude that puts the camera above that surface point at the key's time.
        foreach (FlightKey k in list)
        {
            if (!double.IsNaN(k.OverLon)) k.SpaceLon = k.OverLon - (script.SpinAngle(k.VideoTime) * 180 / Math.PI);
        }

        // The route uses only the keys that give a position. Unwrap their space longitudes so the camera takes
        // the short way round between keys.
        List<FlightKey> route = list.FindAll(k => k.HasPosition);
        if (route.Count == 0) throw new FormatException("flight.json needs at least one key with a position (\"over\" or \"lat\"/\"lon\", and \"alt\" or \"fill\").");
        for (int i = 1; i < route.Count; i++)
        {
            double previous = route[i - 1].SpaceLon;
            double lon = route[i].SpaceLon;
            while (lon - previous > 180) lon -= 360;
            while (lon - previous < -180) lon += 360;
            route[i].SpaceLon = lon;
        }

        script.Keys = list;
        script.PinnedLabels = list.FindAll(k => k.PinnedLabel != null).ConvertAll(k => (k.PinnedLabel, k.VideoTime));
        Dictionary<string, double> held = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (FlightKey k in list)
        {
            foreach (string name in k.HeldLabels) held[name] = k.VideoTime;
        }

        script.HeldLabels = held;
        double[] routeTimes = route.ConvertAll(k => k.VideoTime).ToArray();
        script.logAltitude = new MonotoneCurve(routeTimes, route.ConvertAll(k => Math.Log(Math.Max(1, k.AltitudeKm))).ToArray());
        script.latitude = new MonotoneCurve(routeTimes, route.ConvertAll(k => k.Lat).ToArray(), monotone: false);
        script.longitude = new MonotoneCurve(routeTimes, route.ConvertAll(k => k.SpaceLon).ToArray(), monotone: false);
        script.gaze = new MonotoneCurve(t, list.ConvertAll(k => k.Gaze).ToArray());
        script.pitch = new MonotoneCurve(t, list.ConvertAll(k => k.Pitch).ToArray());
        script.roll = new MonotoneCurve(t, list.ConvertAll(k => k.Roll).ToArray());
        return script;
    }

    /// <summary>The camera at a video time.</summary>
    /// <param name="videoSeconds">Seconds from the start of the video.</param>
    /// <returns>The pose.</returns>
    public CameraPose Sample(double videoSeconds)
    {
        double altitude = Math.Exp(logAltitude.Evaluate(videoSeconds));
        double r = 1 + (altitude / RadiusKm);
        Vector3D position = SpacePosition(videoSeconds) * r;
        Vector3D n = position.Normalized();

        // The direction of travel over the GROUND (what "ahead" looks like), from the surface point beneath,
        // measured over a few seconds either side so the camera eases into a turn instead of snapping round.
        double h = TravelSmoothingSeconds;
        double spin = SpinAngle(videoSeconds);
        Vector3D before = Spin(Spin(SpacePosition(videoSeconds - h), SpinAngle(videoSeconds - h)), -spin);
        Vector3D after = Spin(Spin(SpacePosition(videoSeconds + h), SpinAngle(videoSeconds + h)), -spin);
        Vector3D travel = after - before;
        travel -= n * Vector3D.Dot(travel, n);
        if (travel.Length() < 1e-9) travel = Vector3D.Cross(new Vector3D(0, 1, 0), n);
        if (travel.Length() < 1e-9) travel = new Vector3D(1, 0, 0);
        travel = travel.Normalized();

        double g = Math.Clamp(gaze.Evaluate(videoSeconds), 0, 1);
        double dip = Math.Acos(Math.Min(1, 1 / r));
        double down = dip + (pitch.Evaluate(videoSeconds) * Math.PI / 180);
        // "heading" turns the view away from the direction of travel (positive = to the right of it), so the
        // ground can stream sideways across the frame.
        double turn = heading.Evaluate(videoSeconds) * Math.PI / 180;
        Vector3D look = ((travel * Math.Cos(turn)) + (Vector3D.Cross(travel, n) * Math.Sin(turn))).Normalized();
        Vector3D ahead = ((look * Math.Cos(down)) - (n * Math.Sin(down))).Normalized();
        Vector3D forward = Slerp(-n, ahead, g);

        // Up on screen: the planet's north when gazing at it; when looking ahead, either away from the planet (the
        // horizon across the top), or - with "horizonLeft" - the ground's right-hand side, so the direction of
        // travel is always screen-left and the horizon runs down the left of the frame, however the route turns.
        Vector3D north = new Vector3D(0, 1, 0);
        double side = Math.Clamp(northUp.Evaluate(videoSeconds), 0, 1);
        Vector3D rightOfTravel = Vector3D.Cross(travel, n).Normalized();
        Vector3D upReference = ((north * (1 - g)) + (n * (g * (1 - side))) + (rightOfTravel * (g * side))).Normalized();
        upReference = (upReference - (forward * Vector3D.Dot(upReference, forward))).Length() < 0.05 ? n : upReference;
        if (Math.Abs(Vector3D.Dot(upReference, forward)) > 0.995) upReference = look;
        Vector3D right = Vector3D.Cross(forward, upReference).Normalized();
        Vector3D up = Vector3D.Cross(right, forward).Normalized();
        double bank = roll.Evaluate(videoSeconds) * Math.PI / 180;
        if (bank != 0)
        {
            Vector3D r2 = (right * Math.Cos(bank)) + (up * Math.Sin(bank));
            up = (up * Math.Cos(bank)) - (right * Math.Sin(bank));
            right = r2;
        }

        Vector3D fixedPoint = Spin(n, spin);
        double subLat = Math.Asin(Math.Clamp(fixedPoint.Y, -1, 1)) * 180 / Math.PI;
        double subLon = Math.Atan2(fixedPoint.X, fixedPoint.Z) * 180 / Math.PI;
        return new CameraPose(videoSeconds, position, forward, right, up, altitude, subLat, subLon, spin, KeyIndexAt(videoSeconds),
            phase.Evaluate(videoSeconds), Math.Clamp(sunlight.Evaluate(videoSeconds), 0, 1), Math.Clamp(labels.Evaluate(videoSeconds), 0, 1));
    }

    /// <summary>The index of the last key at or before a time (-1 before the first).</summary>
    public int KeyIndexAt(double videoSeconds)
    {
        int found = -1;
        for (int i = 0; i < Keys.Count; i++)
        {
            if (Keys[i].VideoTime <= videoSeconds + 1e-9) found = i;
        }

        return found;
    }

    /// <summary>m:ss.s.</summary>
    public static string FormatTime(double seconds)
    {
        string sign = seconds < 0 ? "-" : string.Empty;
        seconds = Math.Abs(seconds);
        int minutes = (int)(seconds / 60);
        return sign + minutes.ToString(CultureInfo.InvariantCulture) + ":" + (seconds - (minutes * 60)).ToString("00.0", CultureInfo.InvariantCulture);
    }

    /// <summary>Turns a point about the planet's axis (space to surface for a positive angle).</summary>
    public static Vector3D Spin(Vector3D v, double angle)
    {
        double c = Math.Cos(angle);
        double s = Math.Sin(angle);
        return new Vector3D((v.X * c) + (v.Z * s), v.Y, (-v.X * s) + (v.Z * c));
    }

    /// <summary>A unit vector from latitude and east longitude in degrees (y = north, longitude 0 = +z).</summary>
    public static Vector3D Direction(double latDegrees, double lonDegrees)
    {
        double lat = latDegrees * Math.PI / 180;
        double lon = lonDegrees * Math.PI / 180;
        return new Vector3D(Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat), Math.Cos(lat) * Math.Cos(lon));
    }

    private Vector3D SpacePosition(double t) => Direction(latitude.Evaluate(t), longitude.Evaluate(t));

    private static Vector3D Slerp(Vector3D a, Vector3D b, double t)
    {
        double dot = Math.Clamp(Vector3D.Dot(a, b), -1, 1);
        double angle = Math.Acos(dot);
        if (angle < 1e-6) return b;
        if (Math.PI - angle < 1e-3)
        {
            // Opposite directions: turn through any perpendicular.
            Vector3D axis = Vector3D.Cross(a, new Vector3D(0, 1, 0));
            if (axis.Length() < 1e-6) axis = Vector3D.Cross(a, new Vector3D(1, 0, 0));
            axis = axis.Normalized();
            return ((a * Math.Cos(angle * t)) + (axis * Math.Sin(angle * t))).Normalized();
        }

        double s = Math.Sin(angle);
        return ((a * (Math.Sin((1 - t) * angle) / s)) + (b * (Math.Sin(t * angle) / s))).Normalized();
    }

    private FlightKey ReadKey(JsonElement k, int index)
    {
        if (!k.TryGetProperty("t", out JsonElement te)) throw new FormatException("Key " + index + " has no \"t\" (song time).");
        double songTime = ParseTime(te);
        double videoTime = songTime + LeadInSeconds;
        FlightKey key = new FlightKey
        {
            Index = index,
            VideoTime = videoTime,
            AltitudeKm = Number(k, "alt", double.NaN),
            Gaze = Number(k, "gaze", double.NaN),
            Pitch = Number(k, "pitch", double.NaN),
            Roll = Number(k, "roll", double.NaN),
            Spin = Number(k, "spin", double.NaN),
            Phase = Number(k, "phase", double.NaN),
            Sunlight = Number(k, "sunlight", double.NaN),
            Heading = Number(k, "heading", double.NaN),
            Labels = Number(k, "labels", double.NaN),
            NorthUp = Number(k, "horizonLeft", double.NaN),
            PinnedLabel = k.TryGetProperty("label", out JsonElement pinned) && pinned.ValueKind == JsonValueKind.String ? pinned.GetString() : null,
            HeldLabels = k.TryGetProperty("holdLabels", out JsonElement hold) && hold.ValueKind == JsonValueKind.Array
                ? hold.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()).ToList()
                : new List<string>(),
            Note = k.TryGetProperty("note", out JsonElement note) ? note.GetString() : null,
        };
        double fill = Number(k, "fill", double.NaN);
        if (!double.IsNaN(fill))
        {
            // The planet's width as a fraction of the frame's width: its silhouette's half-angle a has
            // tan(a) = fill * tan(half the horizontal field of view), and sin(a) = 1 / (distance from the centre).
            if (fill <= 0) throw new FormatException("Key " + index + ": \"fill\" must be > 0.");
            double tanX = Math.Tan(FieldOfViewDegrees * Math.PI / 360) * Width / Height;
            double a = Math.Atan(fill * tanX);
            key.AltitudeKm = ((1 / Math.Sin(a)) - 1) * RadiusKm;
            key.Fill = fill;
        }

        bool hasPlace = k.TryGetProperty("over", out JsonElement over) || k.TryGetProperty("lat", out _) || k.TryGetProperty("lon", out _);
        if (!hasPlace && double.IsNaN(key.AltitudeKm))
        {
            // A timing-only key: it sets settings (labels, light ...) at its moment and leaves the route alone.
            key.HasPosition = false;
            return key;
        }

        if (double.IsNaN(key.AltitudeKm) || key.AltitudeKm <= 0) throw new FormatException("Key " + index + " (song " + FormatTime(songTime) + ") needs \"alt\" (km, > 0) or \"fill\".");

        if (hasPlace && over.ValueKind != JsonValueKind.Undefined)
        {
            double lat;
            double lon;
            if (over.ValueKind == JsonValueKind.String)
            {
                (lat, lon) = VenusFeatures.Find(over.GetString());
                key.Over = over.GetString();
            }
            else if (over.ValueKind == JsonValueKind.Array && over.GetArrayLength() == 2)
            {
                lat = over[0].GetDouble();
                lon = over[1].GetDouble();
                key.Over = lat.ToString("0.#", CultureInfo.InvariantCulture) + ", " + lon.ToString("0.#", CultureInfo.InvariantCulture);
            }
            else
            {
                throw new FormatException("Key " + index + ": \"over\" is a place name or [lat, lon].");
            }

            key.Lat = lat;
            key.OverLon = lon;
        }
        else
        {
            key.Lat = Number(k, "lat", double.NaN);
            key.SpaceLon = Number(k, "lon", double.NaN);
            if (double.IsNaN(key.Lat) || double.IsNaN(key.SpaceLon)) throw new FormatException("Key " + index + " (song " + FormatTime(songTime) + ") needs \"over\", or \"lat\" and \"lon\".");
        }

        key.Lat = Math.Clamp(key.Lat, -89, 89);
        return key;
    }

    private static double ParseTime(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number) return e.GetDouble();
        string s = e.GetString()?.Trim() ?? throw new FormatException("\"t\" is a number of seconds or \"m:ss.s\".");
        bool negative = s.StartsWith('-');
        if (negative) s = s.Substring(1);
        string[] parts = s.Split(':');
        double value = 0;
        foreach (string part in parts) value = (value * 60) + double.Parse(part, CultureInfo.InvariantCulture);
        return negative ? -value : value;
    }

    internal static double Number(JsonElement e, string name, double fallback) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;
}

/// <summary>One key of the flight.</summary>
internal sealed class FlightKey
{
    /// <summary>The key's position in the file.</summary>
    public int Index { get; set; }

    /// <summary>Video time, seconds.</summary>
    public double VideoTime { get; set; }

    /// <summary>Altitude above the surface, km.</summary>
    public double AltitudeKm { get; set; }

    /// <summary>Latitude of the camera, degrees.</summary>
    public double Lat { get; set; }

    /// <summary>Longitude of the camera in SPACE (unwrapped), degrees.</summary>
    public double SpaceLon { get; set; }

    /// <summary>0 = look at the centre, 1 = look ahead.</summary>
    public double Gaze { get; set; }

    /// <summary>Degrees below the horizon when looking ahead.</summary>
    public double Pitch { get; set; }

    /// <summary>Bank, degrees.</summary>
    public double Roll { get; set; }

    /// <summary>Places whose labels must wait until this key's moment.</summary>
    public List<string> HeldLabels { get; set; } = new List<string>();

    /// <summary>A place whose label must appear at exactly this key's moment, or null.</summary>
    public string PinnedLabel { get; set; }

    /// <summary>False for a timing-only key (no position: it only changes settings).</summary>
    public bool HasPosition { get; set; } = true;

    /// <summary>The surface longitude of an "over" key (NaN for a space key); turned into <see cref="SpaceLon" /> after the spin is known.</summary>
    public double OverLon { get; set; } = double.NaN;

    /// <summary>The planet's spin rate, degrees per second (NaN until carried over).</summary>
    public double Spin { get; set; } = double.NaN;

    /// <summary>The sun's angle around the planet as seen from the camera, degrees (0 = full, 180 = new).</summary>
    public double Phase { get; set; } = double.NaN;

    /// <summary>0 = the even "studio" light, 1 = real sunlight with a dark night side.</summary>
    public double Sunlight { get; set; } = double.NaN;

    /// <summary>The view's angle from the direction of travel, degrees (positive = to the right of it).</summary>
    public double Heading { get; set; } = double.NaN;

    /// <summary>1 = place labels may appear (each place is labelled on its first pass through the view).</summary>
    public double Labels { get; set; } = double.NaN;

    /// <summary>1 = when looking ahead, the direction of travel is screen-left (the horizon down the left side).</summary>
    public double NorthUp { get; set; } = double.NaN;

    /// <summary>The planet's width as a fraction of the frame width, when the key gave "fill".</summary>
    public double Fill { get; set; } = double.NaN;

    /// <summary>The place the key is over, if given.</summary>
    public string Over { get; set; }

    /// <summary>A note (shown in the preview).</summary>
    public string Note { get; set; }
}

/// <summary>The camera at one moment (space units: one Venus radius).</summary>
internal readonly record struct CameraPose(
    double VideoTime,
    Vector3D Position,
    Vector3D Forward,
    Vector3D Right,
    Vector3D Up,
    double AltitudeKm,
    double SubLat,
    double SubLon,
    double SpinAngle,
    int KeyIndex,
    double PhaseDegrees,
    double Sunlight,
    double Labels);

/// <summary>A double-precision vector (the flight is computed in doubles, the GPU gets floats).</summary>
internal readonly record struct Vector3D(double X, double Y, double Z)
{
    public static Vector3D operator +(Vector3D a, Vector3D b) => new Vector3D(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Vector3D operator -(Vector3D a, Vector3D b) => new Vector3D(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Vector3D operator -(Vector3D a) => new Vector3D(-a.X, -a.Y, -a.Z);

    public static Vector3D operator *(Vector3D a, double s) => new Vector3D(a.X * s, a.Y * s, a.Z * s);

    public static double Dot(Vector3D a, Vector3D b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

    public static Vector3D Cross(Vector3D a, Vector3D b) => new Vector3D((a.Y * b.Z) - (a.Z * b.Y), (a.Z * b.X) - (a.X * b.Z), (a.X * b.Y) - (a.Y * b.X));

    public double Length() => Math.Sqrt(Dot(this, this));

    public Vector3D Normalized()
    {
        double l = Length();
        return l > 0 ? this * (1 / l) : this;
    }

    public Vector3 ToFloat() => new Vector3((float)X, (float)Y, (float)Z);
}
