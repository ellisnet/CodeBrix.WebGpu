using System;
using SkiaSharp;

namespace VenusMusicVideo.Hosting;

/// <summary>The first-run screen: what is being downloaded or built, with progress bars (SkiaSharp).</summary>
internal static class StatusScreen
{
    /// <summary>Draws the screen onto a black frame.</summary>
    public static void Draw(SKCanvas canvas, int width, int height, string title, string detail, double? step, double? overall)
    {
        float scale = width / 1080f;
        float left = 90 * scale;
        float barWidth = width - (2 * left);
        float y = height * 0.42f;
        using SKTypeface face = SKTypeface.FromFamilyName("Helvetica Neue") ?? SKTypeface.Default;
        using SKFont heading = new SKFont(face, 46 * scale);
        using SKFont body = new SKFont(face, 30 * scale);
        using SKFont small = new SKFont(face, 24 * scale);
        using SKPaint warm = new SKPaint { Color = new SKColor(255, 196, 120), IsAntialias = true };
        using SKPaint text = new SKPaint { Color = new SKColor(240, 226, 214), IsAntialias = true };
        using SKPaint dim = new SKPaint { Color = new SKColor(150, 130, 140), IsAntialias = true };
        using SKPaint track = new SKPaint { Color = new SKColor(48, 30, 44), IsAntialias = true };
        using SKPaint bar = new SKPaint { Color = new SKColor(255, 120, 60), IsAntialias = true };
        using SKPaint overallBar = new SKPaint { Color = new SKColor(200, 70, 150), IsAntialias = true };

        canvas.DrawText("Venus", left, y, SKTextAlign.Left, heading, warm);
        y += 64 * scale;
        canvas.DrawText(title + "...", left, y, SKTextAlign.Left, body, text);
        y += 30 * scale;
        if (step.HasValue)
        {
            DrawBar(canvas, left, y, barWidth, 14 * scale, step.Value, track, bar);
            y += 48 * scale;
        }

        if (detail != null)
        {
            canvas.DrawText(detail, left, y, SKTextAlign.Left, small, dim);
            y += 60 * scale;
        }

        if (overall.HasValue)
        {
            canvas.DrawText(string.Format(System.Globalization.CultureInfo.InvariantCulture, "First run: preparing the planet  ·  {0:0}%", overall.Value * 100), left, y, SKTextAlign.Left, small, dim);
            y += 20 * scale;
            DrawBar(canvas, left, y, barWidth, 8 * scale, overall.Value, track, overallBar);
            y += 70 * scale;
        }

        canvas.DrawText("Magellan data: NASA / JPL / USGS  ·  place names: IAU / USGS", left, height - (90 * scale), SKTextAlign.Left, small, dim);
        canvas.DrawText("Esc quits (a partial download is discarded)", left, height - (54 * scale), SKTextAlign.Left, small, dim);
    }

    private static void DrawBar(SKCanvas canvas, float x, float y, float width, float height, double fraction, SKPaint track, SKPaint fill)
    {
        float radius = height / 2;
        canvas.DrawRoundRect(SKRect.Create(x, y, width, height), radius, radius, track);
        float w = (float)(width * Math.Clamp(fraction, 0, 1));
        if (w > 0.5f) canvas.DrawRoundRect(SKRect.Create(x, y, Math.Max(w, height), height), radius, radius, fill);
    }
}
