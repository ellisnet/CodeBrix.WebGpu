using System;
using System.IO;
using System.Numerics;
using CodeBrix.Imaging;
using CodeBrix.Imaging.Formats.Jxl;
using CodeBrix.Imaging.PixelFormats;
using CodeBrix.WebGpu.Graphics.Lighting;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>
/// The environment bake on the GPU against known answers: constant environments stay constant through every map, a
/// bright direction lands on the right cube face, irradiance of a constant is the constant, prefiltered mip 0 is the
/// radiance, the filter keeps the mean, the BRDF LUT matches a CPU integration, the procedural sky's shape and sun
/// units, panorama sources (.hdr file and stream, float images, Imaging images) and option resolution.
/// </summary>
public class EnvironmentMapTests : IClassFixture<GraphicsGpuFixture>
{
    private static readonly EnvironmentOptions Small = new EnvironmentOptions { CubeSize = 64, IrradianceSize = 8, IrradianceSourceSize = 16, PrefilteredSize = 32, PrefilteredMipLevels = 4, SpecularSampleCount = 64 };
    private readonly GraphicsGpuFixture gpu;

    public EnvironmentMapTests(GraphicsGpuFixture fixture)
    {
        gpu = fixture;
    }

    private static void AllNear(GpuTexture texture, int mip, Vector3 expected, float tolerance)
    {
        for (int face = 0; face < 6; face++)
        {
            float[] texels = TestEnvironments.ReadHalf(texture, mip, face);
            for (int index = 0; index < texels.Length; index += 4)
            {
                Vector3 actual = new Vector3(texels[index], texels[index + 1], texels[index + 2]);
                (actual - expected).Length().Should().BeLessThanOrEqualTo(tolerance, "face " + face + " mip " + mip + " texel " + (index / 4) + " = " + actual);
            }
        }
    }

    [Fact]
    public void a_constant_panorama_gives_constant_radiance_irradiance_and_prefiltered_cubes()
    {
        //Arrange
        gpu.RequireDevice();
        Vector3 value = new Vector3(0.25f, 0.5f, 1f);

        //Act
        using EnvironmentMap environment = gpu.Context.CreateEnvironment(TestEnvironments.Constant(value), Small);

        //Assert
        gpu.Context.TakeErrors().Should().BeEmpty();
        environment.Size.Should().Be(64);
        environment.IrradianceSize.Should().Be(8);
        environment.PrefilteredSize.Should().Be(32);
        environment.PrefilteredMipLevels.Should().Be(4);
        environment.IsProcedural.Should().BeFalse();
        for (int mip = 0; mip < environment.RadianceArray.MipLevelCount; mip++) AllNear(environment.RadianceArray, mip, value, 0.002f);
        AllNear(environment.IrradianceArray, 0, value, 0.002f);
        for (int mip = 0; mip < environment.PrefilteredMipLevels; mip++) AllNear(environment.PrefilteredArray, mip, value, 0.004f);
    }

    [Theory]
    [InlineData(0, 1f, 0f, 0f)]
    [InlineData(1, -1f, 0f, 0f)]
    [InlineData(2, 0f, 1f, 0f)]
    [InlineData(3, 0f, -1f, 0f)]
    [InlineData(4, 0f, 0f, 1f)]
    [InlineData(5, 0f, 0f, -1f)]
    public void a_bright_direction_lands_in_the_centre_of_the_matching_cube_face(int face, float x, float y, float z)
    {
        //Arrange - black everywhere except a 10-degree cap around the direction
        gpu.RequireDevice();
        Vector3 direction = new Vector3(x, y, z);
        FloatImage panorama = TestEnvironments.Panorama(d => Vector3.Dot(d, direction) > MathF.Cos(0.17f) ? new Vector3(8f) : Vector3.Zero, 512);

        //Act
        using EnvironmentMap environment = gpu.Context.CreateEnvironment(panorama, Small);

        //Assert - the face's centre texels are bright, every other face's centre is black
        for (int layer = 0; layer < 6; layer++)
        {
            float[] texels = TestEnvironments.ReadHalf(environment.RadianceArray, 0, layer);
            Vector3 centre = TestEnvironments.Texel(texels, 64, 32, 32);
            if (layer == face) centre.X.Should().BeGreaterThan(7.5f, "face " + layer);
            else centre.X.Should().BeLessThan(0.01f, "face " + layer);
        }

        // ... and the face direction convention matches: texels whose CPU direction is inside the cap are bright.
        float[] own = TestEnvironments.ReadHalf(environment.RadianceArray, 0, face);
        int agree = 0;
        int total = 0;
        for (int ty = 0; ty < 64; ty += 3)
        {
            for (int tx = 0; tx < 64; tx += 3)
            {
                float dot = Vector3.Dot(TestEnvironments.FaceDirection(face, (tx + 0.5f) / 64f, (ty + 0.5f) / 64f), direction);
                if (Math.Abs(dot - MathF.Cos(0.17f)) < 0.01f) continue;
                bool bright = TestEnvironments.Texel(own, 64, tx, ty).X > 4f;
                if (bright == dot > MathF.Cos(0.17f)) agree++;
                total++;
            }
        }

        agree.Should().Be(total);
    }

    [Fact]
    public void an_off_axis_direction_lands_where_the_cpu_face_mapping_says()
    {
        //Arrange - a cap around (0.6, 0.3, -0.74): mostly -Z, right of centre and up
        gpu.RequireDevice();
        Vector3 direction = Vector3.Normalize(new Vector3(0.6f, 0.3f, -0.74f));
        FloatImage panorama = TestEnvironments.Panorama(d => Vector3.Dot(d, direction) > MathF.Cos(0.12f) ? new Vector3(8f, 4f, 2f) : Vector3.Zero, 512);

        //Act
        using EnvironmentMap environment = gpu.Context.CreateEnvironment(panorama, Small);

        //Assert - the brightest texel of -Z (layer 5) points along the direction
        float[] texels = TestEnvironments.ReadHalf(environment.RadianceArray, 0, 5);
        float best = -1f;
        Vector3 bestDirection = Vector3.Zero;
        for (int ty = 0; ty < 64; ty++)
        {
            for (int tx = 0; tx < 64; tx++)
            {
                float value = TestEnvironments.Texel(texels, 64, tx, ty).X;
                if (value <= best) continue;
                best = value;
                bestDirection = TestEnvironments.FaceDirection(5, (tx + 0.5f) / 64f, (ty + 0.5f) / 64f);
            }
        }

        best.Should().BeGreaterThan(7.5f);
        Vector3.Dot(bestDirection, direction).Should().BeGreaterThan(MathF.Cos(0.12f));
    }

    [Fact]
    public void prefiltered_mip_zero_is_the_radiance_at_the_same_resolution()
    {
        //Arrange - a smooth, structured environment
        gpu.RequireDevice();
        FloatImage panorama = TestEnvironments.Panorama(d => new Vector3(1f + d.X, 1f + d.Y, 1f + (0.5f * d.Z)) * 0.5f, 512);

        //Act
        using EnvironmentMap environment = gpu.Context.CreateEnvironment(panorama, Small);

        //Assert - prefiltered (32) mip 0 == radiance mip 1 (32), texel by texel
        for (int face = 0; face < 6; face++)
        {
            float[] prefiltered = TestEnvironments.ReadHalf(environment.PrefilteredArray, 0, face);
            float[] radiance = TestEnvironments.ReadHalf(environment.RadianceArray, 1, face);
            for (int index = 0; index < prefiltered.Length; index++)
            {
                if (index % 4 == 3) continue;
                prefiltered[index].Should().BeApproximately(radiance[index], 0.002f);
            }
        }
    }

    [Fact]
    public void every_prefiltered_mip_keeps_the_mean_radiance_and_rough_mips_are_smoother()
    {
        //Arrange - a hard-edged environment: bright upper hemisphere, dark lower
        gpu.RequireDevice();
        FloatImage panorama = TestEnvironments.Panorama(d => d.Y > 0f ? new Vector3(2f) : new Vector3(0.1f), 512);

        //Act
        using EnvironmentMap environment = gpu.Context.CreateEnvironment(panorama, Small);

        //Assert - the mean over all texels (solid-angle weighted) stays ~1.05 per mip; the +X face's vertical contrast shrinks with roughness
        double previousContrast = double.MaxValue;
        for (int mip = 0; mip < environment.PrefilteredMipLevels; mip++)
        {
            int size = environment.PrefilteredSize >> mip;
            double sum = 0;
            double weights = 0;
            for (int face = 0; face < 6; face++)
            {
                float[] texels = TestEnvironments.ReadHalf(environment.PrefilteredArray, mip, face);
                for (int ty = 0; ty < size; ty++)
                {
                    for (int tx = 0; tx < size; tx++)
                    {
                        double s = (((tx + 0.5) / size) * 2) - 1;
                        double t = (((ty + 0.5) / size) * 2) - 1;
                        double r2 = 1 + (s * s) + (t * t);
                        double w = 1 / (r2 * Math.Sqrt(r2));
                        sum += TestEnvironments.Texel(texels, size, tx, ty).X * w;
                        weights += w;
                    }
                }
            }

            (sum / weights).Should().BeApproximately(1.05, 0.06, "mip " + mip);
            float[] side = TestEnvironments.ReadHalf(environment.PrefilteredArray, mip, 0);
            double contrast = TestEnvironments.Texel(side, size, size / 2, size / 8).X - TestEnvironments.Texel(side, size, size / 2, size - 1 - (size / 8)).X;
            contrast.Should().BeLessThanOrEqualTo(previousContrast + 0.01, "mip " + mip);
            previousContrast = contrast;
        }

        previousContrast.Should().BeLessThan(1.2);
    }

    [Fact]
    public void irradiance_of_a_half_bright_sky_matches_the_analytic_cosine_integral()
    {
        //Arrange - radiance 1 above the horizon, 0 below: E/PI = (1 + n.y) / 2 for a normal n
        gpu.RequireDevice();
        FloatImage panorama = TestEnvironments.Panorama(d => d.Y > 0f ? Vector3.One : Vector3.Zero, 1024);

        //Act
        using EnvironmentMap environment = gpu.Context.CreateEnvironment(panorama, new EnvironmentOptions { CubeSize = 128, IrradianceSize = 16, IrradianceSourceSize = 32 });

        //Assert
        for (int face = 0; face < 6; face++)
        {
            float[] texels = TestEnvironments.ReadHalf(environment.IrradianceArray, 0, face);
            for (int ty = 0; ty < 16; ty += 5)
            {
                for (int tx = 0; tx < 16; tx += 5)
                {
                    Vector3 n = TestEnvironments.FaceDirection(face, (tx + 0.5f) / 16f, (ty + 0.5f) / 16f);
                    TestEnvironments.Texel(texels, 16, tx, ty).X.Should().BeApproximately((1f + n.Y) / 2f, 0.03f, "face " + face + " normal " + n);
                }
            }
        }
    }

    [Fact]
    public void the_brdf_lut_matches_a_cpu_integration_and_the_known_limits()
    {
        //Arrange
        gpu.RequireDevice();
        GpuTexture lut = gpu.Context.BrdfLut;

        //Act
        float[] texels = TestEnvironments.ReadHalf(lut, 0, 0);

        //Assert
        lut.Width.Should().Be(EnvironmentBaker.LutSize);
        ReferenceEquals(lut, gpu.Context.BrdfLut).Should().BeTrue();
        int size = EnvironmentBaker.LutSize;
        for (int y = 4; y < size; y += 20)
        {
            for (int x = 4; x < size; x += 20)
            {
                double nDotV = (x + 0.5) / size;
                double roughness = (y + 0.5) / size;
                (double scale, double bias) = ReferenceLut(nDotV, roughness);
                int at = ((y * size) + x) * 2;

                // Grazing views (N.V < 0.1) are the hardest case for importance sampling: a looser bound there.
                float tolerance = nDotV < 0.1 ? 0.025f : 0.01f;
                texels[at].Should().BeApproximately((float)scale, tolerance, "scale at N.V " + nDotV + ", roughness " + roughness);
                texels[at + 1].Should().BeApproximately((float)bias, tolerance, "bias at N.V " + nDotV + ", roughness " + roughness);
            }
        }

        // Smooth and facing: F(1) = F0, so scale ~1 and bias ~0. Every texel: scale + bias <= 1 (energy loss only).
        int last = ((0 * size) + (size - 1)) * 2;
        texels[last].Should().BeGreaterThan(0.97f);
        texels[last + 1].Should().BeLessThan(0.02f);
        for (int index = 0; index < texels.Length; index += 2) (texels[index] + texels[index + 1]).Should().BeLessThanOrEqualTo(1.005f);
    }

    /// <summary>
    /// The split-sum LUT in double precision: GGX importance sampling with 65 536 Hammersley points (64 x the GPU's) for
    /// rough-to-smooth lobes alike.
    /// </summary>
    internal static (double Scale, double Bias) ReferenceLut(double nDotV, double roughness)
    {
        double alpha = Math.Max(roughness * roughness, 0.0001);
        double alpha2 = alpha * alpha;
        double vx = Math.Sqrt(1 - (nDotV * nDotV));
        const int Samples = 1 << 16;
        double scale = 0;
        double bias = 0;
        for (uint i = 0; i < Samples; i++)
        {
            double u = (double)i / Samples;
            double w = ReverseBits(i) / 4294967296.0;
            double phi = 2 * Math.PI * u;
            double cosTheta = Math.Sqrt((1 - w) / (1 + ((alpha2 - 1) * w)));
            double sinTheta = Math.Sqrt(Math.Max(0, 1 - (cosTheta * cosTheta)));
            double hx = Math.Cos(phi) * sinTheta;
            double hy = Math.Sin(phi) * sinTheta;
            double hz = cosTheta;
            double vDotH = (vx * hx) + (nDotV * hz);
            double lz = (2 * vDotH * hz) - nDotV;
            if (lz <= 0 || vDotH <= 0) continue;
            double g = Visibility(lz, nDotV, alpha2) * 4 * lz * vDotH / Math.Max(hz, 1e-9);
            double fc = Math.Pow(1 - vDotH, 5);
            scale += (1 - fc) * g;
            bias += fc * g;
        }

        return (scale / Samples, bias / Samples);
    }

    /// <summary>The same integral by brute-force quadrature over the hemisphere of l (an independent check of the reference, rough lobes only).</summary>
    internal static (double Scale, double Bias) QuadratureLut(double nDotV, double roughness)
    {
        double alpha = Math.Max(roughness * roughness, 0.0001);
        double alpha2 = alpha * alpha;
        double vx = Math.Sqrt(1 - (nDotV * nDotV));
        const int Theta = 1024;
        const int Phi = 512;
        double scale = 0;
        double bias = 0;
        for (int i = 0; i < Theta; i++)
        {
            double cosTheta = (i + 0.5) / Theta;
            double sinTheta = Math.Sqrt(1 - (cosTheta * cosTheta));
            for (int j = 0; j < Phi; j++)
            {
                double phi = (j + 0.5) / Phi * 2 * Math.PI;
                double lx = sinTheta * Math.Cos(phi);
                double ly = sinTheta * Math.Sin(phi);
                double hx = lx + vx;
                double hy = ly;
                double hz = cosTheta + nDotV;
                double length = Math.Sqrt((hx * hx) + (hy * hy) + (hz * hz));
                double nDotH = hz / length;
                double vDotH = Math.Max(((vx * hx) + (nDotV * hz)) / length, 0);
                double f = (nDotH * nDotH * (alpha2 - 1)) + 1;
                double d = alpha2 / (Math.PI * f * f);
                double integrand = d * Visibility(cosTheta, nDotV, alpha2) * cosTheta * (1.0 / Theta) * (2 * Math.PI / Phi);
                double fc = Math.Pow(1 - vDotH, 5);
                scale += (1 - fc) * integrand;
                bias += fc * integrand;
            }
        }

        return (scale, bias);
    }

    private static double Visibility(double nDotL, double nDotV, double alpha2)
    {
        double ggxV = nDotL * Math.Sqrt((nDotV * nDotV * (1 - alpha2)) + alpha2);
        double ggxL = nDotV * Math.Sqrt((nDotL * nDotL * (1 - alpha2)) + alpha2);
        return 0.5 / (ggxV + ggxL);
    }

    private static uint ReverseBits(uint value)
    {
        value = ((value >> 1) & 0x55555555u) | ((value & 0x55555555u) << 1);
        value = ((value >> 2) & 0x33333333u) | ((value & 0x33333333u) << 2);
        value = ((value >> 4) & 0x0F0F0F0Fu) | ((value & 0x0F0F0F0Fu) << 4);
        value = ((value >> 8) & 0x00FF00FFu) | ((value & 0x00FF00FFu) << 8);
        return (value >> 16) | (value << 16);
    }

    [Theory]
    [InlineData(0.5, 0.7)]
    [InlineData(0.9, 0.5)]
    [InlineData(0.2, 1.0)]
    public void the_lut_reference_agrees_with_brute_force_quadrature(double nDotV, double roughness)
    {
        //Act
        (double scale, double bias) = ReferenceLut(nDotV, roughness);
        (double quadratureScale, double quadratureBias) = QuadratureLut(nDotV, roughness);

        //Assert
        scale.Should().BeApproximately(quadratureScale, 0.004);
        bias.Should().BeApproximately(quadratureBias, 0.004);
    }

    [Fact]
    public void the_procedural_sky_is_blue_above_bright_at_the_horizon_dark_below_and_glows_towards_the_sun()
    {
        //Arrange
        gpu.RequireDevice();
        ProceduralSkyOptions sky = new ProceduralSkyOptions();
        Vector3 toSun = -Vector3.Normalize(sky.SunDirection);

        //Act
        using EnvironmentMap environment = gpu.Context.CreateProceduralSky(sky, Small);

        //Assert
        environment.IsProcedural.Should().BeTrue();
        Vector3 zenith = TestEnvironments.Texel(TestEnvironments.ReadHalf(environment.RadianceArray, 0, 2), 64, 32, 32);
        Vector3 nadir = TestEnvironments.Texel(TestEnvironments.ReadHalf(environment.RadianceArray, 0, 3), 64, 32, 32);
        float[] side = TestEnvironments.ReadHalf(environment.RadianceArray, 0, 1);
        Vector3 horizon = TestEnvironments.Texel(side, 64, 32, 31);
        zenith.Z.Should().BeGreaterThan(zenith.X * 2f);
        (horizon.X + horizon.Y + horizon.Z).Should().BeGreaterThan(zenith.X + zenith.Y + zenith.Z);
        (nadir.X + nadir.Y + nadir.Z).Should().BeLessThan(horizon.X + horizon.Y + horizon.Z);
        nadir.X.Should().BeGreaterThanOrEqualTo(nadir.Z);

        // The brightest radiance texel points at the sun.
        float best = 0f;
        Vector3 bestDirection = Vector3.Zero;
        for (int face = 0; face < 6; face++)
        {
            float[] texels = TestEnvironments.ReadHalf(environment.RadianceArray, 0, face);
            for (int ty = 0; ty < 64; ty++)
            {
                for (int tx = 0; tx < 64; tx++)
                {
                    float value = TestEnvironments.Texel(texels, 64, tx, ty).X;
                    if (value <= best) continue;
                    best = value;
                    bestDirection = TestEnvironments.FaceDirection(face, (tx + 0.5f) / 64f, (ty + 0.5f) / 64f);
                }
            }
        }

        best.Should().BeGreaterThan(2f);
        Vector3.Dot(bestDirection, toSun).Should().BeGreaterThan(0.995f);

        // Upward surfaces receive more (and bluer) light than downward ones.
        Vector3 up = TestEnvironments.Texel(TestEnvironments.ReadHalf(environment.IrradianceArray, 0, 2), 8, 4, 4);
        Vector3 down = TestEnvironments.Texel(TestEnvironments.ReadHalf(environment.IrradianceArray, 0, 3), 8, 4, 4);
        (up.X + up.Y + up.Z).Should().BeGreaterThan((down.X + down.Y + down.Z) * 1.5f);
        up.Z.Should().BeGreaterThan(up.X);
    }

    [Fact]
    public void the_sun_disc_intensity_is_in_directional_light_units()
    {
        //Arrange - a black sky with only the sun disc, intensity 2, from straight above
        gpu.RequireDevice();
        ProceduralSkyOptions sky = new ProceduralSkyOptions
        {
            SunDirection = -Vector3.UnitY,
            SunIntensity = 2f,
            SunColor = Color4.White,
            SunGlow = 0f,
            ZenithColor = Color4.Black,
            HorizonColor = Color4.Black,
            GroundColor = Color4.Black,
            SunAngularRadius = 0.05f,
        };

        //Act
        using EnvironmentMap environment = gpu.Context.CreateProceduralSky(sky, new EnvironmentOptions { CubeSize = 256, IrradianceSize = 8, IrradianceSourceSize = 32 });

        //Assert - a white matte surface facing the sun shows 2 (irradiance / PI x PI); one facing sideways ~0
        Vector3 up = TestEnvironments.Texel(TestEnvironments.ReadHalf(environment.IrradianceArray, 0, 2), 8, 4, 4);
        up.X.Should().BeApproximately(2f, 0.15f);
        Vector3 down = TestEnvironments.Texel(TestEnvironments.ReadHalf(environment.IrradianceArray, 0, 3), 8, 4, 4);
        down.X.Should().BeLessThan(0.01f);
    }

    [Fact]
    public void the_committed_hdri_builds_from_a_file_and_from_a_stream_alike()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        using EnvironmentMap fromFile = gpu.Context.CreateEnvironment(TestEnvironments.SmallCathedral, Small);
        using FileStream stream = File.OpenRead(TestEnvironments.SmallCathedral);
        using EnvironmentMap fromStream = gpu.Context.CreateEnvironment(stream, Small);

        //Assert
        gpu.Context.TakeErrors().Should().BeEmpty();
        fromFile.Label.Should().Be("small_cathedral_1k.hdr");
        for (int face = 0; face < 6; face++)
        {
            TestEnvironments.ReadHalf(fromFile.IrradianceArray, 0, face).Should().Equal(TestEnvironments.ReadHalf(fromStream.IrradianceArray, 0, face));
        }

        float[] up = TestEnvironments.ReadHalf(fromFile.IrradianceArray, 0, 2);
        TestEnvironments.Texel(up, 8, 4, 4).X.Should().BeGreaterThan(0.01f);
    }

    [Fact]
    public void ldr_imaging_images_are_linearised_and_float_images_are_taken_as_linear()
    {
        //Arrange - sRGB 188 = linear ~0.5
        gpu.RequireDevice();
        using Image<Rgba32> ldr = new Image<Rgba32>(16, 8, new Rgba32(188, 188, 188, 255));
        using Image<RgbaVector> hdr = new Image<RgbaVector>(16, 8, new RgbaVector(3f, 2f, 1f, 1f));

        //Act
        using EnvironmentMap fromLdr = gpu.Context.CreateEnvironment(ldr, Small);
        using EnvironmentMap fromHdr = gpu.Context.CreateEnvironment(hdr, Small);

        //Assert
        AllNear(fromLdr.IrradianceArray, 0, new Vector3(0.5029f), 0.004f);
        AllNear(fromHdr.IrradianceArray, 0, new Vector3(3f, 2f, 1f), 0.01f);
    }

    [Fact]
    public void jpeg_xl_panoramas_keep_their_high_dynamic_range_in_linear_light()
    {
        //Arrange - a linear-light float file (3, 2, 1); an Imaging float file on the sRGB curve (encoded 1.5 = linear
        //2.5372); an 8-bit sRGB file (188 = linear 0.5029)
        gpu.RequireDevice();
        byte[] linearFloat = TestJxl.LinearFloat(32, 16, 3f, 2f, 1f);
        byte[] srgbFloat;
        using (Image<RgbaVector> image = new Image<RgbaVector>(32, 16, new RgbaVector(1.5f, 1.5f, 1.5f, 1f))) srgbFloat = TestJxl.Encode(image);
        byte[] eightBit;
        using (Image<Rgba32> image = new Image<Rgba32>(32, 16, new Rgba32(188, 188, 188, 255))) eightBit = TestJxl.Encode(image);
        string folder = Directory.CreateTempSubdirectory("webgpu-env-jxl-").FullName;
        try
        {
            string path = Path.Combine(folder, "pano.jxl");
            File.WriteAllBytes(path, linearFloat);

            //Act
            using EnvironmentMap fromFile = gpu.Context.CreateEnvironment(path, Small);
            using EnvironmentMap fromSrgbFloat = gpu.Context.CreateEnvironment(new MemoryStream(srgbFloat), Small);
            using EnvironmentMap fromEightBit = gpu.Context.CreateEnvironment(new MemoryStream(eightBit), Small);
            FloatImage decoded = GraphicsContext.DecodeJpegXlPanorama(linearFloat, "test");

            //Assert
            gpu.Context.TakeErrors().Should().BeEmpty();
            fromFile.Label.Should().Be("pano.jxl");
            decoded.GetPixel(5, 5, out float r, out float g, out float b);
            r.Should().BeApproximately(3f, 0.001f);
            g.Should().BeApproximately(2f, 0.001f);
            b.Should().BeApproximately(1f, 0.001f);
            AllNear(fromFile.IrradianceArray, 0, new Vector3(3f, 2f, 1f), 0.01f);
            AllNear(fromSrgbFloat.IrradianceArray, 0, new Vector3(2.5372f), 0.01f);
            AllNear(fromEightBit.IrradianceArray, 0, new Vector3(0.5029f), 0.004f);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void png_panoramas_load_through_imaging_and_exr_files_are_refused()
    {
        //Arrange
        gpu.RequireDevice();
        string folder = Path.Combine(Path.GetTempPath(), "codebrix-webgpu-env-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string png = Path.Combine(folder, "pano.png");
            using (Image<Rgba32> image = new Image<Rgba32>(32, 16, new Rgba32(255, 255, 255, 255))) image.SaveAsPng(png);
            string exr = Path.Combine(folder, "pano.exr");
            File.WriteAllBytes(exr, new byte[] { 0x76, 0x2F, 0x31, 0x01, 2, 0, 0, 0 });

            //Act
            using EnvironmentMap fromPng = gpu.Context.CreateEnvironment(png, Small);
            Action openExr = () => gpu.Context.CreateEnvironment(exr, Small);
            Action missing = () => gpu.Context.CreateEnvironment(Path.Combine(folder, "missing.hdr"));
            Action garbage = () => gpu.Context.CreateEnvironment(new MemoryStream(new byte[] { 1, 2, 3, 4, 5 }));

            //Assert
            AllNear(fromPng.IrradianceArray, 0, Vector3.One, 0.004f);
            openExr.Should().Throw<NotSupportedException>().Which.Message.Should().Contain("OpenEXR");
            missing.Should().Throw<FileNotFoundException>();
            garbage.Should().Throw<InvalidDataException>();
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void panoramas_wider_than_the_limit_are_halved_on_the_cpu_first()
    {
        //Arrange
        FloatImage image = TestEnvironments.Panorama(d => new Vector3(d.Y + 1f), 64);

        //Act
        FloatImage half = EnvironmentBaker.Halve(image);

        //Assert
        half.Width.Should().Be(32);
        half.Height.Should().Be(16);
        image.GetPixel(0, 0, out float a, out _, out _);
        image.GetPixel(1, 0, out float b, out _, out _);
        image.GetPixel(0, 1, out float c, out _, out _);
        image.GetPixel(1, 1, out float d, out _, out _);
        half.GetPixel(0, 0, out float averaged, out _, out _);
        averaged.Should().BeApproximately((a + b + c + d) / 4f, 1e-6f);

        // ... and through the GPU path with a tiny limit.
        gpu.RequireDevice();
        using EnvironmentMap environment = gpu.Context.CreateEnvironment(TestEnvironments.Constant(new Vector3(0.75f)), new EnvironmentOptions { CubeSize = 32, MaxSourceWidth = 16, PrefilteredSize = 16, IrradianceSourceSize = 8 });
        AllNear(environment.IrradianceArray, 0, new Vector3(0.75f), 0.003f);
    }

    [Fact]
    public void half_conversion_clamps_negative_nan_and_huge_values()
    {
        //Arrange
        FloatImage image = new FloatImage(2, 1, new float[] { -1f, float.NaN, 1e9f, 0.5f, float.PositiveInfinity, 2f });

        //Act
        byte[] bytes = EnvironmentBaker.ToHalfRgba(image);
        ReadOnlySpan<Half> halves = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(bytes);

        //Assert
        ((float)halves[0]).Should().Be(0f);
        ((float)halves[1]).Should().Be(0f);
        ((float)halves[2]).Should().Be(65504f);
        ((float)halves[3]).Should().Be(1f);
        ((float)halves[4]).Should().Be(0.5f);
        ((float)halves[5]).Should().Be(65504f);
        ((float)halves[6]).Should().Be(2f);
    }

    [Theory]
    [InlineData(500, 32, 32, 0, 6, 128, 512, 32, 32, 256, 6, 128)]
    [InlineData(1024, 64, 64, 0, 9, 9999, 1024, 64, 64, 256, 7, 4096)]
    [InlineData(16, 2, 128, 512, 1, 1, 16, 4, 16, 16, 2, 8)]
    [InlineData(100000, 1000, 1000, 100, 6, 64, 4096, 128, 64, 128, 6, 64)]
    public void options_resolve_to_powers_of_two_within_limits(int cube, int irradiance, int source, int prefiltered, int levels, int samples, int expectedCube, int expectedIrradiance, int expectedSource, int expectedPrefiltered, int expectedLevels, int expectedSamples)
    {
        //Act
        EnvironmentBaker.Sizes sizes = EnvironmentBaker.Resolve(new EnvironmentOptions { CubeSize = cube, IrradianceSize = irradiance, IrradianceSourceSize = source, PrefilteredSize = prefiltered, PrefilteredMipLevels = levels, SpecularSampleCount = samples }, 8192);

        //Assert
        sizes.Should().Be(new EnvironmentBaker.Sizes(expectedCube, expectedIrradiance, expectedSource, expectedPrefiltered, expectedLevels, expectedSamples));
    }

    [Fact]
    public void the_default_environment_is_a_cached_procedural_sky_rebuilt_after_disposal()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;

        //Act
        EnvironmentMap first = context.DefaultEnvironment;
        EnvironmentMap again = context.DefaultEnvironment;
        first.Dispose();
        EnvironmentMap rebuilt = context.DefaultEnvironment;

        //Assert
        ReferenceEquals(first, again).Should().BeTrue();
        first.IsDisposed.Should().BeTrue();
        rebuilt.IsDisposed.Should().BeFalse();
        rebuilt.IsProcedural.Should().BeTrue();
        rebuilt.Size.Should().Be(256);
    }

}
