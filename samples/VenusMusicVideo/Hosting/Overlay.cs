using System;
using System.Collections.Generic;
using System.Globalization;
using SkiaSharp;
using VenusMusicVideo.Choreography;

namespace VenusMusicVideo.Hosting;

/// <summary>
/// The preview's information panel, drawn by SkiaSharp onto the frame AFTER it was read back for any video (so it
/// never appears in a render): the song and video clocks, the camera, the key in effect, and the keyboard help.
/// </summary>
internal static class Overlay
{
    /// <summary>Draws the panel.</summary>
    /// <param name="canvas">The frame's canvas.</param>
    /// <param name="width">The frame's width.</param>
    /// <param name="script">The flight.</param>
    /// <param name="pose">The camera.</param>
    /// <param name="status">Extra status lines (paused, reloaded, render progress ...).</param>
    /// <param name="showHelp">Adds the key help.</param>
    public static void Draw(SKCanvas canvas, int width, FlightScript script, CameraPose pose, IReadOnlyList<string> status, bool showHelp)
    {
        double song = pose.VideoTime - script.LeadInSeconds;
        List<string> lines = new List<string>
        {
            "song " + FlightScript.FormatTime(song) + "   video " + FlightScript.FormatTime(pose.VideoTime) + " / " + FlightScript.FormatTime(script.VideoSeconds),
            string.Format(CultureInfo.InvariantCulture, "alt {0:N0} km   over {1:0.0}°{2} {3:0.0}°{4}", pose.AltitudeKm, Math.Abs(pose.SubLat), pose.SubLat >= 0 ? "N" : "S", Math.Abs(pose.SubLon), pose.SubLon >= 0 ? "E" : "W"),
        };
        string place = VenusFeatures.Nearest(pose.SubLat, pose.SubLon);
        if (place != null) lines.Add("near " + place);
        if (pose.KeyIndex >= 0)
        {
            FlightKey key = script.Keys[pose.KeyIndex];
            string next = pose.KeyIndex + 1 < script.Keys.Count ? "  ->  next at " + FlightScript.FormatTime(script.Keys[pose.KeyIndex + 1].VideoTime - script.LeadInSeconds) : string.Empty;
            lines.Add("key " + key.Index + " @ " + FlightScript.FormatTime(key.VideoTime - script.LeadInSeconds) + (key.Note != null ? "  " + key.Note : string.Empty) + next);
        }

        lines.AddRange(status);
        if (showHelp)
        {
            lines.Add("F start/restart  Space pause  Left/Right 5 s (Shift 1 s)");
            lines.Add("Up/Down prev/next key  Home start  M mute  H help  P still  Esc quit");
        }

        float scale = width / 1080f;
        float size = 26 * scale;
        float lineHeight = size * 1.3f;
        float pad = 16 * scale;
        using SKFont font = new SKFont(SKTypeface.FromFamilyName("Helvetica Neue") ?? SKTypeface.Default, size);
        float boxWidth = 0;
        foreach (string line in lines) boxWidth = Math.Max(boxWidth, font.MeasureText(line));
        SKRect box = SKRect.Create(pad, pad, boxWidth + (2 * pad), (lines.Count * lineHeight) + pad);
        using SKPaint panel = new SKPaint { Color = new SKColor(10, 6, 14, 190), IsAntialias = true };
        using SKPaint text = new SKPaint { Color = new SKColor(255, 236, 220), IsAntialias = true };
        canvas.DrawRoundRect(box, 12 * scale, 12 * scale, panel);
        for (int i = 0; i < lines.Count; i++)
        {
            canvas.DrawText(lines[i], box.Left + pad, box.Top + (pad * 0.5f) + ((i + 1) * lineHeight) - (lineHeight * 0.25f), SKTextAlign.Left, font, text);
        }
    }
}
