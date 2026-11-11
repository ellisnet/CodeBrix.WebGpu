using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CodeBrix.Imaging;
using CodeBrix.Imaging.Advanced;
using CodeBrix.Imaging.Formats.Jpeg;
using CodeBrix.Imaging.Formats.Png;
using CodeBrix.Imaging.PixelFormats;
using CodeBrix.Imaging.Processing;

namespace VenusMusicVideo.Maps;

/// <summary>
/// Builds the planet's maps from the two Magellan products, with CodeBrix.Imaging doing the decoding, resampling
/// and encoding:
/// <list type="number">
/// <item>ELEVATION (8192x4096, metres relative to 6051 km): its gaps (8%, mostly near the south pole) are filled
/// by a pull-push pyramid (each missing pixel takes the mask-weighted average of ever coarser neighbourhoods).</item>
/// <item>RELIEF map (8192x4096 PNG): the east and north slopes of the lightly blurred elevation, encoded as
/// <c>sign(s) * sqrt(|s| * 4)</c> in R and G - the shader decodes them and lights the terrain with them.</item>
/// <item>RADAR mosaic (18775x9388, 8-bit): no-data pixels (0) and a 2-pixel fringe around them are refilled from a
/// pull-push fill of a quarter-size copy; it is then resampled to 16384x8192 (Lanczos 3) and contrast-stretched
/// between its 0.5th and 99.5th percentiles.</item>
/// <item>COLOUR map: each pixel takes the "fire" colour of its elevation (plum and magenta lowlands, crimson and
/// red plains, orange and gold highlands, hot white only on the highest peaks) and is shaded by the radar
/// brightness. Saved as 16384x8192 and 8192x4096 JPEGs (quality 95, no chroma subsampling).</item>
/// </list>
/// The output names carry <see cref="Version" />: changing the recipe (bump it) rebuilds the maps on the next run
/// without downloading again.
/// </summary>
internal static class MapBuilder
{
    /// <summary>The recipe version (part of the file names).</summary>
    public const int Version = 1;

    /// <summary>The relief map's file name.</summary>
    public static readonly string ReliefMapName = "venus_relief_v" + Version + "_8192x4096.png";

    private const float HeightLow = -3000f;
    private const float HeightHigh = 12000f;
    private const double RadiusMetres = 6051000.0;

    /// <summary>
    /// The "fire" palette: elevation (m) -> sRGB colour. Lowlands plum/magenta, then crimson, red, orange, gold,
    /// and white only on the highest peaks (Maxwell Montes).
    /// </summary>
    private static readonly float[,] Stops =
    {
        { -2500, 60, 8, 70 }, { -1200, 120, 15, 95 }, { -500, 165, 22, 80 }, { 0, 195, 30, 50 },
        { 500, 220, 50, 30 }, { 1100, 238, 82, 24 }, { 1900, 246, 112, 26 }, { 2900, 250, 140, 30 },
        { 4200, 252, 165, 38 }, { 6500, 255, 190, 60 }, { 9000, 255, 225, 150 }, { 11500, 255, 248, 225 },
    };

    /// <summary>The colour map's file name at a width.</summary>
    public static string ColorMapName(int width) => "venus_fire_v" + Version + "_" + width + "x" + (width / 2) + ".jpg";

    /// <summary>Builds the maps.</summary>
    /// <param name="radarPath">The radar mosaic GeoTIFF.</param>
    /// <param name="topographyPath">The topography GeoTIFF.</param>
    /// <param name="folder">Where the maps go.</param>
    /// <param name="progress">Progress reporting.</param>
    /// <param name="from">Overall progress at the start.</param>
    /// <param name="to">Overall progress at the end.</param>
    /// <param name="cancel">Cancellation.</param>
    public static void Build(string radarPath, string topographyPath, string folder, PreparationProgress progress, double from, double to, CancellationToken cancel)
    {
        Stopwatch clock = Stopwatch.StartNew();
        void Step(string title, double fraction) => progress.Set(title, "this happens once", fraction, from + ((to - from) * fraction));

        // 1. Elevation.
        Step("Reading the elevation map", 0.0);
        float[] height = ReadElevation(topographyPath, out int tw, out int th, out bool[] heightValid);
        cancel.ThrowIfCancellationRequested();
        Step("Filling gaps in the elevation", 0.05);
        height = PullPushFill(height, heightValid, tw, th);
        cancel.ThrowIfCancellationRequested();

        // 2. Relief.
        Step("Baking the terrain relief", 0.12);
        SaveRelief(height, tw, th, Path.Combine(folder, ReliefMapName));
        cancel.ThrowIfCancellationRequested();

        // 3. Radar.
        Step("Reading the radar mosaic", 0.22);
        using Image<L8> radarImage = LoadPatched<L8>(radarPath, out _);
        int rw = radarImage.Width;
        int rh = radarImage.Height;
        byte[] radar = new byte[rw * rh];
        radarImage.CopyPixelDataTo(radar);
        cancel.ThrowIfCancellationRequested();
        Step("Filling gaps in the radar mosaic", 0.32);
        (float low, float high) = FillRadar(radar, rw, rh);
        cancel.ThrowIfCancellationRequested();

        Step("Resampling the radar mosaic to 16384 x 8192", 0.45);
        const int Width = 16384;
        const int Height = 8192;
        byte[] shadeSource;
        using (Image<L8> filled = Image.LoadPixelData<L8>(radar, rw, rh, PngFormat.Instance))
        {
            radar = null;
            filled.Mutate(x => x.Resize(Width, Height, KnownResamplers.Lanczos3));
            shadeSource = new byte[Width * Height];
            filled.CopyPixelDataTo(shadeSource);
        }

        cancel.ThrowIfCancellationRequested();

        // 4. Colour.
        Step("Colouring the planet", 0.62);
        Rgb24[] ramp = BuildRamp(4096);
        float[] shade = new float[256];
        for (int v = 0; v < 256; v++)
        {
            float s = Math.Clamp((v - low) / (high - low), 0f, 1f);
            shade[v] = 0.28f + (0.95f * MathF.Pow(s, 0.9f));
        }

        using Image<Rgb24> color = new Image<Rgb24>(Width, Height);
        float scaleX = tw / (float)Width;
        float scaleY = th / (float)Height;
        Parallel.For(0, Height, y =>
        {
            Span<Rgb24> row = color.DangerousGetPixelRowMemory(y).Span;
            float ty = Math.Clamp(((y + 0.5f) * scaleY) - 0.5f, 0, th - 1);
            int y0 = (int)ty;
            int y1 = Math.Min(y0 + 1, th - 1);
            float fy = ty - y0;
            int shadeRow = y * Width;
            for (int x = 0; x < Width; x++)
            {
                float tx = ((x + 0.5f) * scaleX) - 0.5f;
                int x0 = (int)MathF.Floor(tx);
                float fx = tx - x0;
                x0 = ((x0 % tw) + tw) % tw;
                int x1 = (x0 + 1) % tw;
                float top = (height[(y0 * tw) + x0] * (1 - fx)) + (height[(y0 * tw) + x1] * fx);
                float bottom = (height[(y1 * tw) + x0] * (1 - fx)) + (height[(y1 * tw) + x1] * fx);
                float e = (top * (1 - fy)) + (bottom * fy);
                int index = Math.Clamp((int)((e - HeightLow) / (HeightHigh - HeightLow) * (ramp.Length - 1)), 0, ramp.Length - 1);
                Rgb24 c = ramp[index];
                float k = shade[shadeSource[shadeRow + x]];
                row[x] = new Rgb24((byte)Math.Min(255f, (c.R * k) + 0.5f), (byte)Math.Min(255f, (c.G * k) + 0.5f), (byte)Math.Min(255f, (c.B * k) + 0.5f));
            }
        });
        shadeSource = null;
        cancel.ThrowIfCancellationRequested();

        Step("Saving the colour maps", 0.80);
        JpegEncoder jpeg = new JpegEncoder { Quality = 95, ColorType = JpegColorType.YCbCrRatio444 };
        SaveAtomically(Path.Combine(folder, ColorMapName(Width)), path => color.Save(path, jpeg));
        Step("Saving the colour maps", 0.93);
        color.Mutate(x => x.Resize(Width / 2, Height / 2, KnownResamplers.Lanczos3));
        SaveAtomically(Path.Combine(folder, ColorMapName(Width / 2)), path => color.Save(path, jpeg));
        Console.WriteLine("Built the Venus maps in " + clock.Elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s");
    }

    private static float[] ReadElevation(string path, out int width, out int height, out bool[] valid)
    {
        using Image<L16> image = LoadPatched<L16>(path, out bool signed);
        width = image.Width;
        height = image.Height;
        ushort[] raw = new ushort[width * height];
        image.CopyPixelDataTo(MemoryMarshal.AsBytes(raw.AsSpan()));
        float[] metres = new float[raw.Length];
        valid = new bool[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            int v = signed ? unchecked((short)raw[i]) : raw[i];
            valid[i] = v != short.MinValue;
            metres[i] = valid[i] ? v : 0;
        }

        return metres;
    }

    private static Image<TPixel> LoadPatched<TPixel>(string path, out bool signed)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        byte[] bytes = File.ReadAllBytes(path);
        signed = TiffTags.MakeChunkyUnsigned(bytes);
        using MemoryStream stream = new MemoryStream(bytes, writable: false);
        return Image.Load<TPixel>(stream);
    }

    /// <summary>Fills invalid pixels from coarser, mask-weighted levels (wraps in x).</summary>
    private static float[] PullPushFill(float[] values, bool[] valid, int width, int height)
    {
        float[] w0 = new float[values.Length];
        float[] a0 = new float[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            w0[i] = valid[i] ? 1 : 0;
            a0[i] = valid[i] ? values[i] : 0;
        }

        return PullPush(a0, w0, width, height);
    }

    private static float[] PullPush(float[] a, float[] w, int width, int height)
    {
        if (Math.Min(width, height) <= 4)
        {
            return a;
        }

        int hw = width / 2;
        int hh = height / 2;
        float[] a2 = new float[hw * hh];
        float[] w2 = new float[hw * hh];
        Parallel.For(0, hh, y =>
        {
            for (int x = 0; x < hw; x++)
            {
                float sw = 0;
                float saw = 0;
                for (int dy = 0; dy < 2; dy++)
                {
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int i = (((2 * y) + dy) * width) + (2 * x) + dx;
                        sw += w[i];
                        saw += a[i] * w[i];
                    }
                }

                a2[(y * hw) + x] = sw > 0 ? saw / sw : 0;
                w2[(y * hw) + x] = Math.Min(sw, 1);
            }
        });

        float[] coarse = PullPush(a2, w2, hw, hh);
        float[] result = new float[width * height];
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width) + x;
                float up = SampleBilinear(coarse, hw, hh, ((x + 0.5f) * hw / width) - 0.5f, ((y + 0.5f) * hh / height) - 0.5f, wrapX: false);
                result[i] = (a[i] * w[i]) + (up * (1 - w[i]));
            }
        });
        return result;
    }

    private static float SampleBilinear(float[] data, int width, int height, float x, float y, bool wrapX)
    {
        y = Math.Clamp(y, 0, height - 1);
        int y0 = (int)y;
        int y1 = Math.Min(y0 + 1, height - 1);
        float fy = y - y0;
        int x0;
        int x1;
        float fx;
        if (wrapX)
        {
            int xf = (int)MathF.Floor(x);
            fx = x - xf;
            x0 = ((xf % width) + width) % width;
            x1 = (x0 + 1) % width;
        }
        else
        {
            x = Math.Clamp(x, 0, width - 1);
            x0 = (int)x;
            x1 = Math.Min(x0 + 1, width - 1);
            fx = x - x0;
        }

        float top = (data[(y0 * width) + x0] * (1 - fx)) + (data[(y0 * width) + x1] * fx);
        float bottom = (data[(y1 * width) + x0] * (1 - fx)) + (data[(y1 * width) + x1] * fx);
        return (top * (1 - fy)) + (bottom * fy);
    }

    /// <summary>Refills the radar's no-data pixels (and a 2-pixel fringe) in place; returns the stretch range.</summary>
    private static (float Low, float High) FillRadar(byte[] radar, int width, int height)
    {
        // Valid = non-zero, eroded by two pixels (the gap edges are dark).
        byte[] valid = new byte[radar.Length];
        for (int i = 0; i < radar.Length; i++) valid[i] = radar[i] > 0 ? (byte)1 : (byte)0;
        byte[] next = new byte[radar.Length];
        for (int pass = 0; pass < 2; pass++)
        {
            byte[] current = valid;
            byte[] output = next;
            Parallel.For(0, height, y =>
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    int i = row + x;
                    byte v = current[i];
                    if (v != 0)
                    {
                        if ((x > 0 && current[i - 1] == 0) || (x < width - 1 && current[i + 1] == 0) || (y > 0 && current[i - width] == 0) || (y < height - 1 && current[i + width] == 0)) v = 0;
                    }

                    output[i] = v;
                }
            });
            next = valid;
            valid = output;
        }

        // The stretch range from the histogram of the valid pixels.
        long[] histogram = new long[256];
        long count = 0;
        for (int i = 0; i < radar.Length; i++)
        {
            if (valid[i] != 0)
            {
                histogram[radar[i]]++;
                count++;
            }
        }

        float Percentile(double p)
        {
            long target = (long)(p * count);
            long sum = 0;
            for (int v = 0; v < 256; v++)
            {
                sum += histogram[v];
                if (sum > target) return v;
            }

            return 255;
        }

        float low = Percentile(0.005);
        float high = Math.Max(low + 1, Percentile(0.995));

        // A quarter-size mean of the valid pixels, gap-filled, sampled back into the gaps.
        const int F = 4;
        int sw = width / F;
        int sh = height / F;
        float[] a = new float[sw * sh];
        float[] w = new float[sw * sh];
        Parallel.For(0, sh, y =>
        {
            for (int x = 0; x < sw; x++)
            {
                float sum = 0;
                int n = 0;
                for (int dy = 0; dy < F; dy++)
                {
                    for (int dx = 0; dx < F; dx++)
                    {
                        int i = (((y * F) + dy) * width) + (x * F) + dx;
                        if (valid[i] != 0)
                        {
                            sum += radar[i];
                            n++;
                        }
                    }
                }

                a[(y * sw) + x] = n > 0 ? sum / n : 0;
                w[(y * sw) + x] = n > 0 ? 1 : 0;
            }
        });
        float[] small = PullPush(a, w, sw, sh);
        Parallel.For(0, height, y =>
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int i = row + x;
                if (valid[i] != 0) continue;
                float v = SampleBilinear(small, sw, sh, ((x + 0.5f) / F) - 0.5f, ((y + 0.5f) / F) - 0.5f, wrapX: true);
                radar[i] = (byte)Math.Clamp((int)(v + 0.5f), 1, 255);
            }
        });
        return (low, high);
    }

    private static void SaveRelief(float[] height, int width, int rows, string path)
    {
        // Light blur ([1 2 1] / 4 twice in each direction; wraps in x).
        float[] h = (float[])height.Clone();
        float[] t = new float[h.Length];
        for (int pass = 0; pass < 2; pass++)
        {
            float[] src = h;
            float[] dst = t;
            Parallel.For(0, rows, y =>
            {
                int r = y * width;
                for (int x = 0; x < width; x++)
                {
                    int xl = x == 0 ? width - 1 : x - 1;
                    int xr = x == width - 1 ? 0 : x + 1;
                    dst[r + x] = (src[r + xl] + (2 * src[r + x]) + src[r + xr]) / 4;
                }
            });
            Parallel.For(0, rows, y =>
            {
                int up = Math.Max(0, y - 1) * width;
                int down = Math.Min(rows - 1, y + 1) * width;
                int r = y * width;
                for (int x = 0; x < width; x++) src[r + x] = (dst[up + x] + (2 * dst[r + x]) + dst[down + x]) / 4;
            });
        }

        using Image<Rgba32> relief = new Image<Rgba32>(width, rows);
        double dy = Math.PI * RadiusMetres / rows;
        Parallel.For(0, rows, y =>
        {
            double latitude = (90.0 - ((y + 0.5) * 180.0 / rows)) * Math.PI / 180.0;
            double dx = 2 * Math.PI * RadiusMetres * Math.Max(Math.Cos(latitude), 0.02) / width;
            float fade = (float)Math.Clamp((90 - Math.Abs(latitude * 180 / Math.PI)) / 8.0, 0, 1);
            int up = Math.Max(0, y - 1) * width;
            int down = Math.Min(rows - 1, y + 1) * width;
            int r = y * width;
            Span<Rgba32> row = relief.DangerousGetPixelRowMemory(y).Span;
            for (int x = 0; x < width; x++)
            {
                int xl = x == 0 ? width - 1 : x - 1;
                int xr = x == width - 1 ? 0 : x + 1;
                float east = (float)((h[r + xr] - h[r + xl]) / (2 * dx)) * fade;
                float north = (float)((h[up + x] - h[down + x]) / (2 * dy)) * fade;
                row[x] = new Rgba32(EncodeSlope(east), EncodeSlope(north), 0, 255);
            }
        });
        SaveAtomically(path, p => relief.Save(p, new PngEncoder()));
    }

    private static byte EncodeSlope(float s)
    {
        float e = MathF.Sign(s) * MathF.Sqrt(Math.Min(MathF.Abs(s) * 4f, 1f));
        return (byte)Math.Clamp(MathF.Round(127.5f + (127.5f * e)), 0, 255);
    }

    private static Rgb24[] BuildRamp(int size)
    {
        Rgb24[] ramp = new Rgb24[size];
        int stops = Stops.GetLength(0);
        for (int i = 0; i < size; i++)
        {
            float e = HeightLow + ((HeightHigh - HeightLow) * i / (size - 1));
            int k = 0;
            while (k < stops - 2 && e > Stops[k + 1, 0]) k++;
            float t = Math.Clamp((e - Stops[k, 0]) / (Stops[k + 1, 0] - Stops[k, 0]), 0, 1);
            byte C(int c) => (byte)MathF.Round(Stops[k, c] + ((Stops[k + 1, c] - Stops[k, c]) * t));
            ramp[i] = new Rgb24(C(1), C(2), C(3));
        }

        return ramp;
    }

    private static void SaveAtomically(string path, Action<string> save)
    {
        string temporary = path + ".building" + Path.GetExtension(path);
        save(temporary);
        File.Move(temporary, path, overwrite: true);
    }
}
