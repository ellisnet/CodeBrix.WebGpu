using System;
using System.Drawing;
using System.Globalization;
using System.Numerics;
using CodeBrix.WebGpu.Graphics;
using WebGpuModelViewer.Scene;

namespace WebGpuModelViewer.Hud;

/// <summary>The statistics the HUD shows.</summary>
internal sealed class FrameStats
{
    private readonly float[] history = new float[120];
    private int next;
    private int count;

    /// <summary>The last frame time in milliseconds.</summary>
    public float LastMilliseconds { get; private set; }

    /// <summary>The average frame time over the history, in milliseconds.</summary>
    public float AverageMilliseconds
    {
        get
        {
            if (count == 0) return 0;
            float sum = 0;
            for (int index = 0; index < count; index++) sum += history[index];
            return sum / count;
        }
    }

    /// <summary>The frames recorded so far.</summary>
    public long FrameCount { get; private set; }

    /// <summary>Records a frame time.</summary>
    /// <param name="milliseconds">The time.</param>
    public void Add(float milliseconds)
    {
        LastMilliseconds = milliseconds;
        history[next] = milliseconds;
        next = (next + 1) % history.Length;
        count = Math.Min(count + 1, history.Length);
        FrameCount++;
    }

    /// <summary>The history, oldest first.</summary>
    /// <param name="index">0 = oldest.</param>
    /// <returns>The time, or 0.</returns>
    public float At(int index) => index >= count ? 0 : history[((next - count + index) % history.Length + history.Length) % history.Length];

    /// <summary>How many entries the history holds.</summary>
    public int HistoryLength => count;
}

/// <summary>The 2D HUD drawn with the sprite batch: a statistics panel, a frame-time graph and a mini map.</summary>
internal sealed class HudOverlay
{
    private static readonly Color4 Panel = new Color4(0.05f, 0.07f, 0.10f, 0.62f);
    private static readonly Color4 Accent = Color4.FromHex("#FFC857");
    private readonly GlyphAtlas glyphs;

    /// <summary>Creates the HUD.</summary>
    /// <param name="context">The context.</param>
    public HudOverlay(GraphicsContext context)
    {
        glyphs = new GlyphAtlas(context, 15);
    }

    /// <summary>Draws the HUD.</summary>
    /// <param name="batch">A batch between Begin and End.</param>
    /// <param name="width">The target width.</param>
    /// <param name="height">The target height.</param>
    /// <param name="stats">The frame statistics.</param>
    /// <param name="scene">The scene.</param>
    /// <param name="meshDrawCalls">Draw calls of the last 3D pass.</param>
    /// <param name="cameraYaw">The camera's orbit angle (for the mini map).</param>
    public void Draw(SpriteBatch batch, int width, int height, FrameStats stats, ViewerScene scene, int meshDrawCalls, float cameraYaw)
    {
        CultureInfo invariant = CultureInfo.InvariantCulture;
        float average = stats.AverageMilliseconds;
        string[] lines =
        {
            "CodeBrix.WebGpu.Graphics",
            string.Format(invariant, "frame {0,6:F2} ms  avg {1,6:F2} ms", stats.LastMilliseconds, average),
            string.Format(invariant, "{0,4:F0} fps   frame #{1}", average > 0 ? 1000f / average : 0, stats.FrameCount),
            string.Format(invariant, "models {0} placed, {1} files", scene.ActorCount, scene.ModelFileCount),
            string.Format(invariant, "meshes {0}  draw calls {1}", scene.LastDrawableCount, meshDrawCalls),
            string.Format(invariant, "triangles {0:N0}", scene.LastTriangleCount),
        };

        float panelWidth = 0;
        foreach (string line in lines) panelWidth = Math.Max(panelWidth, glyphs.Measure(line));
        RectangleF panel = new RectangleF(12, 12, panelWidth + 24, (lines.Length * glyphs.LineHeight) + 16);
        batch.FillRectangle(panel, Panel);
        batch.DrawRectangle(panel, Accent.WithAlpha(0.8f), 1.5f);
        for (int index = 0; index < lines.Length; index++)
        {
            glyphs.Draw(batch, lines[index], new Vector2(24, 20 + (index * glyphs.LineHeight)), index == 0 ? Accent : Color4.White);
        }

        DrawGraph(batch, height, stats);
        DrawMiniMap(batch, width, scene, cameraYaw);
    }

    private void DrawGraph(SpriteBatch batch, int height, FrameStats stats)
    {
        const float BarWidth = 2.5f;
        const float GraphHeight = 70;
        const float MillisecondsHigh = 33.3f;
        RectangleF area = new RectangleF(12, height - GraphHeight - 34, (120 * BarWidth) + 16, GraphHeight + 22);
        batch.FillRectangle(area, Panel);
        float baseline = area.Bottom - 8;
        for (int index = 0; index < stats.HistoryLength; index++)
        {
            float milliseconds = stats.At(index);
            float bar = Math.Min(GraphHeight, milliseconds / MillisecondsHigh * GraphHeight);
            Color4 color = milliseconds <= 17.5f ? Color4.FromHex("#6BD66B") : milliseconds <= 34f ? Accent : Color4.FromHex("#FF6B6B");
            batch.FillRectangle(new RectangleF(area.Left + 8 + (index * BarWidth), baseline - bar, BarWidth - 0.5f, bar), color);
        }

        float sixty = baseline - (16.67f / MillisecondsHigh * GraphHeight);
        batch.DrawLine(new Vector2(area.Left + 6, sixty), new Vector2(area.Right - 6, sixty), Color4.White.WithAlpha(0.6f), 1);
        glyphs.Draw(batch, "frame time (16.7 ms line)", new Vector2(area.Left + 8, area.Top + 2), Color4.White.WithAlpha(0.85f));
    }

    private static void DrawMiniMap(SpriteBatch batch, int width, ViewerScene scene, float cameraYaw)
    {
        const float Radius = 58;
        const float WorldRadius = 12;
        Vector2 center = new Vector2(width - Radius - 18, Radius + 18);
        batch.FillCircle(center, Radius, Panel);
        batch.DrawCircle(center, Radius, Accent.WithAlpha(0.8f), 1.5f);
        batch.DrawCircle(center, 5.4f / WorldRadius * Radius, Color4.White.WithAlpha(0.25f), 1);
        batch.FillRectangle(new RectangleF(center.X - 3, center.Y - 3, 6, 6), Color4.FromHex("#B5793C"));
        foreach (Vector3 position in scene.AnimalPositions)
        {
            batch.FillCircle(center + (new Vector2(position.X, position.Z) / WorldRadius * Radius), 3, Color4.White);
        }

        Vector3 bird = scene.BirdPosition;
        batch.FillCircle(center + (new Vector2(bird.X, bird.Z) / WorldRadius * Radius), 3.5f, Color4.FromHex("#58C4F6"));
        Vector2 eye = center + (new Vector2(MathF.Sin(cameraYaw), MathF.Cos(cameraYaw)) * Radius * 0.92f);
        batch.DrawLine(eye, center, Accent.WithAlpha(0.7f), 1.5f);
        batch.FillCircle(eye, 4, Accent);
    }
}
