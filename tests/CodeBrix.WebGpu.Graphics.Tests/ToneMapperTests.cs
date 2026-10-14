using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>The tone-map operators: known values on the CPU, and the GPU pass against the CPU curve for every operator and target kind.</summary>
public class ToneMapperTests : IClassFixture<GraphicsGpuFixture>
{
    private readonly GraphicsGpuFixture gpu;

    public ToneMapperTests(GraphicsGpuFixture fixture)
    {
        gpu = fixture;
    }

    [Fact]
    public void known_values_of_each_operator()
    {
        //Assert - Reinhard 1 -> 1/2; Narkowicz 1 -> 2.54 / 3.16; Hill's ACES fit maps grey 1 to 0.6191
        ToneMapper.Map(Vector3.One, ToneMapOperator.Reinhard).X.Should().BeApproximately(0.5f, 1e-6f);
        ToneMapper.Map(Vector3.One, ToneMapOperator.AcesNarkowicz).X.Should().BeApproximately(2.54f / 3.16f, 1e-5f);
        ToneMapper.Map(Vector3.One, ToneMapOperator.AcesFitted).X.Should().BeApproximately(0.61911f, 2e-4f);
        ToneMapper.Map(new Vector3(0.25f), ToneMapOperator.Clamp).X.Should().Be(0.25f);
        ToneMapper.Map(new Vector3(7f), ToneMapOperator.Clamp).X.Should().Be(1f);

        // Khronos PBR Neutral: below the compression start only the toe offset applies (0.04, or x - 6.25 x^2 under 0.08).
        ToneMapper.Map(new Vector3(0.5f), ToneMapOperator.KhronosPbrNeutral).X.Should().BeApproximately(0.46f, 1e-6f);
        ToneMapper.Map(new Vector3(0.04f), ToneMapOperator.KhronosPbrNeutral).X.Should().BeApproximately(0.01f, 1e-6f);
        Vector3 hue = ToneMapper.Map(new Vector3(0.6f, 0.3f, 0.1f), ToneMapOperator.KhronosPbrNeutral);
        hue.X.Should().BeApproximately(0.56f, 1e-6f);
        hue.Y.Should().BeApproximately(0.26f, 1e-6f);
        hue.Z.Should().BeApproximately(0.06f, 1e-6f);
    }

    [Theory]
    [InlineData(ToneMapOperator.AcesFitted)]
    [InlineData(ToneMapOperator.KhronosPbrNeutral)]
    [InlineData(ToneMapOperator.Reinhard)]
    [InlineData(ToneMapOperator.AcesNarkowicz)]
    [InlineData(ToneMapOperator.Clamp)]
    public void every_operator_is_monotonic_bounded_and_black_at_zero(ToneMapOperator op)
    {
        //Act / Assert
        ToneMapper.Map(Vector3.Zero, op).Should().Be(Vector3.Zero);
        ToneMapper.Map(new Vector3(-3f), op).Should().Be(Vector3.Zero);
        float previous = 0f;
        for (float value = 0.01f; value < 200f; value *= 1.3f)
        {
            float mapped = ToneMapper.Map(new Vector3(value), op).X;
            mapped.Should().BeGreaterThanOrEqualTo(previous - 1e-6f, op + " at " + value);
            mapped.Should().BeInRange(0f, 1f);
            previous = mapped;
        }

        if (op != ToneMapOperator.Reinhard) previous.Should().BeGreaterThan(0.97f);
    }

    [Fact]
    public void exposure_multiplies_the_input_before_the_curve()
    {
        //Assert
        ToneMapper.Map(new Vector3(0.25f), ToneMapOperator.AcesFitted, 4f).Should().Be(ToneMapper.Map(Vector3.One, ToneMapOperator.AcesFitted));
    }

    private static GpuTexture Ramp(GraphicsContext context, out Vector3[] values)
    {
        const int Width = 64;
        values = new Vector3[Width];
        Half[] texels = new Half[Width * 4];
        for (int x = 0; x < Width; x++)
        {
            float v = 0.02f * MathF.Pow(1.12f, x);
            values[x] = new Vector3(v, v * 0.6f, v * 0.25f);
            texels[x * 4] = (Half)values[x].X;
            texels[(x * 4) + 1] = (Half)values[x].Y;
            texels[(x * 4) + 2] = (Half)values[x].Z;
            texels[(x * 4) + 3] = Half.One;
            values[x] = new Vector3((float)texels[x * 4], (float)texels[(x * 4) + 1], (float)texels[(x * 4) + 2]);
        }

        GpuTexture texture = context.CreateTexture2D(Width, 1, TextureFormat.RGBA16Float, TextureUsage.TextureBinding | TextureUsage.CopyDst);
        texture.Write<Half>(texels);
        return texture;
    }

    [Theory]
    [InlineData(ToneMapOperator.AcesFitted, TextureFormat.RGBA8UnormSrgb)]
    [InlineData(ToneMapOperator.AcesFitted, TextureFormat.RGBA8Unorm)]
    [InlineData(ToneMapOperator.KhronosPbrNeutral, TextureFormat.RGBA8UnormSrgb)]
    [InlineData(ToneMapOperator.Reinhard, TextureFormat.BGRA8Unorm)]
    [InlineData(ToneMapOperator.AcesNarkowicz, TextureFormat.RGBA8UnormSrgb)]
    [InlineData(ToneMapOperator.Clamp, TextureFormat.RGBA8Unorm)]
    public void the_gpu_pass_matches_the_cpu_curve(ToneMapOperator op, TextureFormat format)
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        using GpuTexture ramp = Ramp(context, out Vector3[] values);
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(ramp.Width, 1, format, false);
        using ToneMapper toneMapper = context.CreateToneMapper();
        toneMapper.Operator = op;
        toneMapper.Exposure = 1.3f;

        //Act
        using (GraphicsFrame frame = context.BeginFrame())
        {
            toneMapper.Apply(frame, ramp, target.View);
            frame.Submit();
        }

        //Assert
        byte[] pixels = target.ReadRgba();
        for (int x = 0; x < values.Length; x++)
        {
            Vector3 expected = ToneMapper.Map(values[x], op, 1.3f);
            (byte R, byte G, byte B, byte A) actual = TestPixels.At(pixels, ramp.Width, x, 0);
            TestPixels.Near(actual, ModelPbrTests.Encode(expected.X), ModelPbrTests.Encode(expected.Y), ModelPbrTests.Encode(expected.Z), 1).Should().BeTrue(op + " texel " + x + " = " + actual + " vs " + expected);
        }

        context.TakeErrors().Should().BeEmpty();
    }

    [Fact]
    public void a_float_destination_receives_the_linear_curve_and_a_pass_with_depth_works_too()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        using GpuTexture ramp = Ramp(context, out Vector3[] values);
        using HeadlessRenderTarget linear = context.CreateHeadlessTarget(ramp.Width, 1, TextureFormat.RGBA16Float, false);
        using HeadlessRenderTarget withDepth = context.CreateHeadlessTarget(ramp.Width, 1, TextureFormat.RGBA8UnormSrgb, true);
        using ToneMapper toneMapper = context.CreateToneMapper();
        toneMapper.Operator = ToneMapOperator.KhronosPbrNeutral;

        //Act
        using (GraphicsFrame frame = context.BeginFrame())
        {
            toneMapper.Apply(frame, ramp, linear.View);
            using (RenderPass pass = frame.BeginRenderPass(withDepth.View, Color4.Black)) toneMapper.Apply(pass, ramp);
            frame.Submit();
        }

        //Assert
        ReadOnlySpan<Half> halves = MemoryMarshal.Cast<byte, Half>(linear.ReadPixels());
        byte[] encoded = withDepth.ReadRgba();
        for (int x = 0; x < values.Length; x++)
        {
            Vector3 expected = ToneMapper.Map(values[x], ToneMapOperator.KhronosPbrNeutral);
            ((float)halves[x * 4]).Should().BeApproximately(expected.X, 0.002f + (expected.X * 0.002f));
            TestPixels.At(encoded, ramp.Width, x, 0).R.Should().BeInRange((byte)Math.Max(0, ModelPbrTests.Encode(expected.X) - 1), (byte)Math.Min(255, ModelPbrTests.Encode(expected.X) + 1));
        }

        context.TakeErrors().Should().BeEmpty();
    }

    [Fact]
    public void a_source_of_another_size_is_scaled_to_the_destination()
    {
        //Arrange - a 2 x 2 source, left column 0, right column 4 (Clamp -> 0 and 1)
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        using GpuTexture source = context.CreateTexture2D(2, 2, TextureFormat.RGBA16Float, TextureUsage.TextureBinding | TextureUsage.CopyDst);
        Half zero = Half.Zero;
        Half four = (Half)4f;
        source.Write<Half>(new[] { zero, zero, zero, Half.One, four, four, four, Half.One, zero, zero, zero, Half.One, four, four, four, Half.One });
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(32, 16, TextureFormat.RGBA8Unorm, false);
        using ToneMapper toneMapper = context.CreateToneMapper();
        toneMapper.Operator = ToneMapOperator.Clamp;

        //Act
        using (GraphicsFrame frame = context.BeginFrame())
        {
            toneMapper.Apply(frame, source, target.View);
            frame.Submit();
        }

        //Assert
        byte[] pixels = target.ReadRgba();
        TestPixels.At(pixels, 32, 1, 8).R.Should().Be(0);
        TestPixels.At(pixels, 32, 30, 8).R.Should().Be(255);
    }
    [Fact]
    public void an_unfilterable_rgba32float_source_is_refused_with_webgpus_message()
    {
        //Arrange - the documented limit: the pass samples its source with a filtering sampler
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        Assert.SkipWhen(context.HasFeature(FeatureName.Float32Filterable), "The device filters RGBA32Float.");
        using GpuTexture source = context.CreateTexture2D(4, 4, TextureFormat.RGBA32Float, TextureUsage.TextureBinding | TextureUsage.CopyDst);
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(4, 4, TextureFormat.RGBA8Unorm, false);
        using ToneMapper toneMapper = context.CreateToneMapper();

        //Act
        Action apply = () =>
        {
            using GraphicsFrame frame = context.BeginFrame();
            toneMapper.Apply(frame, source, target.View);
            frame.Submit();
        };

        //Assert
        apply.Should().Throw<GraphicsException>();
        context.TakeErrors();
    }
}
