using System;
using System.IO;
using CodeBrix.Imaging;
using CodeBrix.Imaging.PixelFormats;
using CodeBrix.WebGpu.Graphics.Images;
using CodeBrix.WebGpu.Graphics.Lighting;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>Environments for image-based lighting and sky backgrounds, the default sky and the BRDF LUT.</summary>
public sealed partial class GraphicsContext
{
    private EnvironmentBaker environmentBaker;
    private EnvironmentMap defaultEnvironment;

    /// <summary>
    /// The environment the model renderer and the skybox use when <see cref="SceneLighting.Environment" /> is null: a
    /// procedural sky with <see cref="ProceduralSkyOptions" /> defaults (256-texel cube), built on first use and owned by
    /// the context (do not dispose it; it is rebuilt if you do).
    /// </summary>
    public EnvironmentMap DefaultEnvironment
    {
        get
        {
            ThrowIfDisposed();
            if (defaultEnvironment == null || defaultEnvironment.IsDisposed)
            {
                defaultEnvironment = CreateProceduralSky(null, new EnvironmentOptions { CubeSize = 256, PrefilteredSize = 128, Label = "default sky" });
            }

            return defaultEnvironment;
        }
    }

    /// <summary>
    /// The split-sum BRDF lookup table every environment shares (128 x 128 RG16Float; u = N.V, v = perceptual
    /// roughness; R = scale and G = bias of F0, so specular = prefiltered x (F0 x R + G)). Rendered on first use, owned
    /// by the context. For your own image-based-lighting shaders.
    /// </summary>
    public GpuTexture BrdfLut
    {
        get
        {
            ThrowIfDisposed();
            return Baker.BrdfLut;
        }
    }

    private EnvironmentBaker Baker => environmentBaker ??= new EnvironmentBaker(this);

    /// <summary>Creates a skybox renderer (see <see cref="SkyboxRenderer" />).</summary>
    /// <returns>The renderer.</returns>
    public SkyboxRenderer CreateSkyboxRenderer() => new SkyboxRenderer(this);

    /// <summary>Creates a tone mapper for the HDR rendering path (see <see cref="ToneMapper" />).</summary>
    /// <returns>The tone mapper.</returns>
    public ToneMapper CreateToneMapper() => new ToneMapper(this);

    /// <summary>
    /// Builds an environment from an equirectangular (2:1 latitude-longitude) panorama file: Radiance <c>.hdr</c>
    /// (linear HDR; the usual HDRI download), JPEG XL <c>.jxl</c> (HDR kept: float, linear-light, PQ and HLG files are
    /// converted to linear sRGB radiance with values above 1 preserved; 8/16-bit files too), or any other image
    /// CodeBrix.Imaging decodes (PNG, JPEG ...; treated as sRGB and linearised, so the light it gives is
    /// low-dynamic-range). OpenEXR is not supported.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="options">The options (null for <see cref="EnvironmentOptions.Default" />).</param>
    /// <returns>The environment (dispose it).</returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="NotSupportedException">The file is OpenEXR.</exception>
    /// <exception cref="InvalidDataException">The file is not a readable image.</exception>
    public EnvironmentMap CreateEnvironment(string path, EnvironmentOptions options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ThrowIfDisposed();
        if (!File.Exists(path)) throw new FileNotFoundException("The environment image does not exist.", path);
        options ??= EnvironmentOptions.Default;
        if (options.Label == null) options = WithLabel(options, Path.GetFileName(path));
        return CreateEnvironmentCore(File.ReadAllBytes(path), options, "'" + path + "'");
    }

    /// <summary>Builds an environment from a panorama in a stream (see <see cref="CreateEnvironment(string, EnvironmentOptions)" />).</summary>
    /// <param name="stream">The stream, positioned at the file's first byte (read to its end).</param>
    /// <param name="options">The options (null for the defaults).</param>
    /// <returns>The environment.</returns>
    /// <exception cref="NotSupportedException">The data is OpenEXR.</exception>
    /// <exception cref="InvalidDataException">The data is not a readable image.</exception>
    public EnvironmentMap CreateEnvironment(Stream stream, EnvironmentOptions options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ThrowIfDisposed();
        using MemoryStream copy = new MemoryStream();
        stream.CopyTo(copy);
        return CreateEnvironmentCore(new ReadOnlySpan<byte>(copy.GetBuffer(), 0, (int)copy.Length), options ?? EnvironmentOptions.Default, "stream");
    }

    /// <summary>Builds an environment from a linear equirectangular panorama in memory (for example from <see cref="RadianceHdrDecoder" />).</summary>
    /// <param name="panorama">The panorama (not modified).</param>
    /// <param name="options">The options (null for the defaults).</param>
    /// <returns>The environment.</returns>
    public EnvironmentMap CreateEnvironment(FloatImage panorama, EnvironmentOptions options = null)
    {
        ArgumentNullException.ThrowIfNull(panorama);
        ThrowIfDisposed();
        return Baker.FromPanorama(panorama, options ?? EnvironmentOptions.Default);
    }

    /// <summary>
    /// Builds an environment from a CodeBrix.Imaging image holding an equirectangular panorama (its first frame).
    /// Float pixel types (<c>RgbaVector</c>, <c>HalfVector4</c>) are taken as linear; every other pixel type is taken as
    /// sRGB-encoded and linearised. The image is not modified or disposed. (A float image decoded from a JPEG XL file
    /// carries the file's own transfer curve - often sRGB - so pass HDR <c>.jxl</c> files to the path or stream
    /// overloads, which decode them to linear light.)
    /// </summary>
    /// <param name="panorama">The image.</param>
    /// <param name="options">The options (null for the defaults).</param>
    /// <returns>The environment.</returns>
    public EnvironmentMap CreateEnvironment(Image panorama, EnvironmentOptions options = null)
    {
        ArgumentNullException.ThrowIfNull(panorama);
        ThrowIfDisposed();
        return Baker.FromPanorama(ToFloatImage(panorama), options ?? EnvironmentOptions.Default);
    }

    /// <summary>
    /// Builds an analytic sky environment (gradient, ground, haze, sun glow and an optional sun disc) - the look of
    /// <see cref="DefaultEnvironment" />, with your parameters.
    /// </summary>
    /// <param name="sky">The sky (null for <see cref="ProceduralSkyOptions.Default" />).</param>
    /// <param name="options">The sizes (null for <see cref="EnvironmentOptions.Default" />).</param>
    /// <returns>The environment (dispose it).</returns>
    public EnvironmentMap CreateProceduralSky(ProceduralSkyOptions sky = null, EnvironmentOptions options = null)
    {
        ThrowIfDisposed();
        return Baker.FromSky(sky ?? ProceduralSkyOptions.Default, options ?? EnvironmentOptions.Default);
    }

    /// <summary>Converts an Imaging image to a linear float panorama.</summary>
    /// <param name="image">The image.</param>
    /// <returns>The panorama.</returns>
    internal static FloatImage ToFloatImage(Image image)
    {
        bool linear = image is Image<RgbaVector> || image is Image<HalfVector4>;
        Image<RgbaVector> vector = image as Image<RgbaVector>;
        Image<RgbaVector> converted = vector == null ? image.CloneAs<RgbaVector>(ImageUpload.Decoding) : null;
        try
        {
            Image<RgbaVector> source = vector ?? converted;
            int width = source.Width;
            int height = source.Height;
            RgbaVector[] pixels = new RgbaVector[width * height];
            source.Frames.RootFrame.CopyPixelDataTo(pixels);
            float[] rgb = new float[width * height * 3];
            for (int index = 0; index < pixels.Length; index++)
            {
                RgbaVector pixel = pixels[index];
                rgb[index * 3] = linear ? pixel.R : SrgbToLinear(pixel.R);
                rgb[(index * 3) + 1] = linear ? pixel.G : SrgbToLinear(pixel.G);
                rgb[(index * 3) + 2] = linear ? pixel.B : SrgbToLinear(pixel.B);
            }

            return new FloatImage(width, height, rgb);
        }
        finally
        {
            converted?.Dispose();
        }
    }

    /// <summary>
    /// Decodes a JPEG XL panorama to linear light: CodeBrix.Imaging converts any source encoding (float, linear, PQ,
    /// HLG, wide gamut) to the sRGB curve with values outside 0..1 kept, and the extended inverse curve undoes it.
    /// Negative (out-of-gamut) and NaN components become 0; radiance is capped at the half-float maximum.
    /// </summary>
    /// <param name="data">The file bytes.</param>
    /// <param name="what">What is decoded (for messages).</param>
    /// <returns>The panorama.</returns>
    /// <exception cref="InvalidDataException">The file could not be decoded.</exception>
    internal static FloatImage DecodeJpegXlPanorama(ReadOnlySpan<byte> data, string what)
    {
        Image<RgbaVector> image;
        try
        {
            image = ImageUpload.DecodeJpegXl<RgbaVector>(ImageUpload.JxlHdrDecoder, data);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new InvalidDataException("The environment image " + what + " could not be decoded: " + exception.Message, exception);
        }

        using (image)
        {
            int width = image.Width;
            int height = image.Height;
            float[] rgb = new float[width * height * 3];
            image.Frames.RootFrame.ProcessPixelRows(accessor =>
            {
                for (int row = 0; row < accessor.Height; row++)
                {
                    Span<RgbaVector> pixels = accessor.GetRowSpan(row);
                    int target = row * width * 3;
                    for (int column = 0; column < pixels.Length; column++, target += 3)
                    {
                        rgb[target] = ToRadiance(pixels[column].R);
                        rgb[target + 1] = ToRadiance(pixels[column].G);
                        rgb[target + 2] = ToRadiance(pixels[column].B);
                    }
                }
            });
            return new FloatImage(width, height, rgb);
        }
    }

    /// <summary>Linearises one extended-sRGB sample into a radiance the half-float cubes hold (negative and NaN to 0, at most 65504).</summary>
    /// <param name="encoded">The sRGB-encoded sample.</param>
    /// <returns>The radiance.</returns>
    private static float ToRadiance(float encoded)
    {
        float linear = ImageUpload.SrgbToLinearExtended(encoded);
        return linear > 0f ? Math.Min(linear, 65504f) : 0f;
    }

    private static float SrgbToLinear(float value)
    {
        if (value <= 0.04045f) return Math.Max(value, 0f) / 12.92f;
        return MathF.Pow((Math.Min(value, 1f) + 0.055f) / 1.055f, 2.4f);
    }

    private static EnvironmentOptions WithLabel(EnvironmentOptions options, string label) => new EnvironmentOptions
    {
        CubeSize = options.CubeSize,
        IrradianceSize = options.IrradianceSize,
        IrradianceSourceSize = options.IrradianceSourceSize,
        PrefilteredSize = options.PrefilteredSize,
        PrefilteredMipLevels = options.PrefilteredMipLevels,
        SpecularSampleCount = options.SpecularSampleCount,
        MaxSourceWidth = options.MaxSourceWidth,
        Label = label,
    };

    private EnvironmentMap CreateEnvironmentCore(ReadOnlySpan<byte> data, EnvironmentOptions options, string what)
    {
        if (data.Length >= 4 && data[0] == 0x76 && data[1] == 0x2F && data[2] == 0x31 && data[3] == 0x01)
        {
            throw new NotSupportedException("The environment image " + what + " is OpenEXR, which is not supported; convert it to Radiance .hdr.");
        }

        FloatImage panorama;
        if (RadianceHdrDecoder.IsRadianceHdr(data))
        {
            panorama = RadianceHdrDecoder.Decode(data);
        }
        else if (ImageUpload.IsJpegXl(data))
        {
            panorama = DecodeJpegXlPanorama(data, what);
        }
        else
        {
            using Image<Rgba32> image = ImageUpload.Decode(data, what);
            panorama = ToFloatImage(image);
        }

        return Baker.FromPanorama(panorama, options);
    }
}
