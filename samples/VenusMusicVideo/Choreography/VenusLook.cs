using System.Numerics;
using System.Text.Json;

namespace VenusMusicVideo.Choreography;

/// <summary>
/// The look of the planet (the <c>"look"</c> object of flight.json; every field optional): terrain relief, the
/// light, the glowing haze around the planet and the stars.
/// </summary>
internal sealed class VenusLook
{
    /// <summary>How much the terrain's slopes are exaggerated for shading (1 = true slopes).</summary>
    public float ReliefExaggeration { get; set; } = 14f;

    /// <summary>Light that reaches every surface (0..1).</summary>
    public float Ambient { get; set; } = 0.38f;

    /// <summary>The directional light's strength.</summary>
    public float Diffuse { get; set; } = 0.85f;

    /// <summary>Ambient light in full sunlight (the night side's brightness).</summary>
    public float NightAmbient { get; set; } = 0.015f;

    /// <summary>The sun's strength in full sunlight.</summary>
    public float SunDiffuse { get; set; } = 1.3f;

    /// <summary>How soft the day/night line is in sunlight (0 = hard).</summary>
    public float TerminatorSoftness { get; set; } = 0.15f;

    /// <summary>How many places are labelled in the whole video (the biggest ones along the flight).</summary>
    public int LabelCount { get; set; } = 44;

    /// <summary>The most labels on screen at once.</summary>
    public int MaxLabelsOnScreen { get; set; } = 2;

    /// <summary>The least time between two labels appearing, seconds.</summary>
    public double LabelSpacingSeconds { get; set; } = 2.5;

    /// <summary>Places smaller than this (km) are never labelled.</summary>
    public double LabelMinDiameterKm { get; set; } = 150;

    /// <summary>The sound wave's colour (linear RGB).</summary>
    public System.Numerics.Vector3 WaveColor { get; set; } = new System.Numerics.Vector3(0.16f, 0.03f, 0.62f);

    /// <summary>How strongly the sound wave shows (0 = off).</summary>
    public float WaveStrength { get; set; } = 1.05f;

    /// <summary>How far the rings carry before fading to half, degrees of arc.</summary>
    public float WaveReachDegrees { get; set; } = 24f;

    /// <summary>The fine ripple texture within the rings, ripples per second of song (0 = none).</summary>
    public float WaveRippleHz { get; set; } = 6f;

    /// <summary>The slowest a hand-over wave may travel, degrees per second (slower: that place is skipped).</summary>
    public double WaveMinSpeedDegrees { get; set; } = 0.6;

    /// <summary>The fastest a hand-over wave may travel, degrees per second (faster: that place is skipped).</summary>
    public double WaveMaxSpeedDegrees { get; set; } = 14;

    /// <summary>The last loudspeaker's wave speed, degrees per second.</summary>
    public double WaveFinalSpeedDegrees { get; set; } = 5;

    /// <summary>Overall brightness of the surface.</summary>
    public float Exposure { get; set; } = 1.1f;

    /// <summary>How thick the glowing haze shell is, km.</summary>
    public float HazeThicknessKm { get; set; } = 380f;

    /// <summary>How dense the haze is (per planet radius of path).</summary>
    public float HazeDensity { get; set; } = 1.6f;

    /// <summary>Brightness of the haze glow.</summary>
    public float HazeGlow { get; set; } = 1.15f;

    /// <summary>The haze colour near the surface (linear RGB).</summary>
    public Vector3 HazeLow { get; set; } = new Vector3(1.0f, 0.42f, 0.10f);

    /// <summary>The haze colour high up (linear RGB).</summary>
    public Vector3 HazeHigh { get; set; } = new Vector3(0.75f, 0.10f, 0.55f);

    /// <summary>Brightness of the stars (0 = none).</summary>
    public float StarBrightness { get; set; } = 0.55f;

    /// <summary>Fraction of star cells that hold a star (density of the star field).</summary>
    public float StarDensity { get; set; } = 0.07f;

    /// <summary>Reads the look object.</summary>
    public static VenusLook Read(JsonElement e)
    {
        VenusLook look = new VenusLook();
        look.ReliefExaggeration = (float)FlightScript.Number(e, "reliefExaggeration", look.ReliefExaggeration);
        look.Ambient = (float)FlightScript.Number(e, "ambient", look.Ambient);
        look.Diffuse = (float)FlightScript.Number(e, "diffuse", look.Diffuse);
        look.Exposure = (float)FlightScript.Number(e, "exposure", look.Exposure);
        look.WaveColor = Color(e, "waveColor", look.WaveColor);
        look.WaveStrength = (float)FlightScript.Number(e, "waveStrength", look.WaveStrength);
        look.WaveReachDegrees = (float)FlightScript.Number(e, "waveReachDegrees", look.WaveReachDegrees);
        look.WaveRippleHz = (float)FlightScript.Number(e, "waveRippleHz", look.WaveRippleHz);
        look.WaveMinSpeedDegrees = FlightScript.Number(e, "waveMinSpeedDegrees", look.WaveMinSpeedDegrees);
        look.WaveMaxSpeedDegrees = FlightScript.Number(e, "waveMaxSpeedDegrees", look.WaveMaxSpeedDegrees);
        look.WaveFinalSpeedDegrees = FlightScript.Number(e, "waveFinalSpeedDegrees", look.WaveFinalSpeedDegrees);
        look.LabelCount = (int)FlightScript.Number(e, "labelCount", look.LabelCount);
        look.MaxLabelsOnScreen = (int)FlightScript.Number(e, "maxLabelsOnScreen", look.MaxLabelsOnScreen);
        look.LabelSpacingSeconds = FlightScript.Number(e, "labelSpacingSeconds", look.LabelSpacingSeconds);
        look.LabelMinDiameterKm = FlightScript.Number(e, "labelMinDiameterKm", look.LabelMinDiameterKm);
        look.NightAmbient = (float)FlightScript.Number(e, "nightAmbient", look.NightAmbient);
        look.SunDiffuse = (float)FlightScript.Number(e, "sunDiffuse", look.SunDiffuse);
        look.TerminatorSoftness = (float)FlightScript.Number(e, "terminatorSoftness", look.TerminatorSoftness);
        look.HazeThicknessKm = (float)FlightScript.Number(e, "hazeThicknessKm", look.HazeThicknessKm);
        look.HazeDensity = (float)FlightScript.Number(e, "hazeDensity", look.HazeDensity);
        look.HazeGlow = (float)FlightScript.Number(e, "hazeGlow", look.HazeGlow);
        look.HazeLow = Color(e, "hazeLow", look.HazeLow);
        look.HazeHigh = Color(e, "hazeHigh", look.HazeHigh);
        look.StarBrightness = (float)FlightScript.Number(e, "starBrightness", look.StarBrightness);
        look.StarDensity = (float)FlightScript.Number(e, "starDensity", look.StarDensity);
        return look;
    }

    private static Vector3 Color(JsonElement e, string name, Vector3 fallback)
    {
        if (!e.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Array || v.GetArrayLength() != 3) return fallback;
        return new Vector3((float)v[0].GetDouble(), (float)v[1].GetDouble(), (float)v[2].GetDouble());
    }
}
