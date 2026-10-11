using System;
using System.Drawing;
using System.Numerics;
using CodeBrix.WebGpu.Graphics;
using SkiaSharp;

namespace WebGpuModelViewer.Hud;

/// <summary>
/// A bitmap font for the sprite batch: SkiaSharp rasterises the printable ASCII glyphs ONCE into an atlas texture,
/// and every HUD string is then drawn as sprites from that one texture - so all the HUD text and shapes cost one draw.
/// </summary>
internal sealed class GlyphAtlas
{
    private const char First = ' ';
    private const char Last = '~';
    private readonly RectangleF[] cells = new RectangleF[Last - First + 1];
    private readonly float[] advances = new float[Last - First + 1];

    /// <summary>Builds the atlas.</summary>
    /// <param name="context">The graphics context.</param>
    /// <param name="size">The font size in pixels.</param>
    public GlyphAtlas(GraphicsContext context, float size)
    {
        using SKTypeface typeface = SKTypeface.FromFamilyName("monospace", SKFontStyle.Bold) ?? SKTypeface.Default;
        using SKFont font = new SKFont(typeface, size) { Edging = SKFontEdging.Antialias };
        font.GetFontMetrics(out SKFontMetrics metrics);
        LineHeight = MathF.Ceiling(metrics.Descent - metrics.Ascent + 2);
        int cellWidth = (int)MathF.Ceiling(font.MeasureText("W") + 2);
        int cellHeight = (int)LineHeight;
        const int Columns = 16;
        int rows = (cells.Length + Columns - 1) / Columns;
        int width = Columns * cellWidth;
        int height = rows * cellHeight;

        using SKBitmap bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (SKCanvas canvas = new SKCanvas(bitmap))
        using (SKPaint paint = new SKPaint { Color = SKColors.White, IsAntialias = true })
        {
            canvas.Clear(SKColors.Transparent);
            for (int index = 0; index < cells.Length; index++)
            {
                string text = ((char)(First + index)).ToString();
                int x = (index % Columns) * cellWidth;
                int y = (index / Columns) * cellHeight;
                canvas.DrawText(text, x + 1, y + 1 - metrics.Ascent, SKTextAlign.Left, font, paint);
                cells[index] = new RectangleF(x, y, cellWidth, cellHeight);
                advances[index] = font.MeasureText(text);
            }
        }

        // White glyphs with coverage in alpha: straight alpha is just "RGB = white".
        byte[] pixels = bitmap.Bytes;
        for (int index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 255;
            pixels[index + 1] = 255;
            pixels[index + 2] = 255;
        }

        Texture = context.CreateTextureFromRgba(width, height, pixels, label: "glyph atlas");
        CellWidth = cellWidth;
    }

    /// <summary>The atlas texture.</summary>
    public GpuTexture Texture { get; }

    /// <summary>The line height in pixels.</summary>
    public float LineHeight { get; }

    /// <summary>The width of one glyph cell.</summary>
    public int CellWidth { get; }

    /// <summary>The width of a string in pixels.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The width.</returns>
    public float Measure(string text)
    {
        float width = 0;
        foreach (char character in text) width += advances[Index(character)];
        return width;
    }

    /// <summary>Draws a string with its top-left corner at <paramref name="position" />.</summary>
    /// <param name="batch">A batch between Begin and End.</param>
    /// <param name="text">The text.</param>
    /// <param name="position">The top-left corner.</param>
    /// <param name="color">The colour.</param>
    public void Draw(SpriteBatch batch, string text, Vector2 position, Color4 color)
    {
        float x = position.X;
        foreach (char character in text)
        {
            int index = Index(character);
            RectangleF cell = cells[index];
            batch.Draw(Texture, new RectangleF(MathF.Round(x), MathF.Round(position.Y), cell.Width, cell.Height), cell, color);
            x += advances[index];
        }
    }

    private static int Index(char character) => character < First || character > Last ? '?' - First : character - First;
}
