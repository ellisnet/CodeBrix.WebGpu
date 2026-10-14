using System;
using System.Numerics;
using CodeBrix.WebGpu.Graphics.Animation;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>
/// Known answers for channel evaluation (no GPU): STEP, LINEAR (slerp on the shorter arc for rotations) and
/// CUBICSPLINE (Hermite with tangents scaled by the key interval, normalised rotations), clip boundaries, morph-weight
/// channels and the key cursor.
/// </summary>
public class AnimationSamplingTests
{
    private static ModelAnimationChannel Channel(AnimationPath path, AnimationInterpolation interpolation, float[] times, float[] values, int components) =>
        new ModelAnimationChannel(0, path, interpolation, times, values, components);

    private static float[] Sample(ModelAnimationChannel channel, float time)
    {
        float[] result = new float[channel.ComponentCount];
        channel.Sample(time, result);
        return result;
    }

    private static Quaternion SampleRotation(ModelAnimationChannel channel, float time)
    {
        float[] value = Sample(channel, time);
        return new Quaternion(value[0], value[1], value[2], value[3]);
    }

    private static float[] Q(Quaternion q) => new[] { q.X, q.Y, q.Z, q.W };

    private static void ShouldBeRotation(Quaternion actual, Quaternion expected, float tolerance = 1e-5f) =>
        Math.Abs(Quaternion.Dot(actual, expected)).Should().BeApproximately(1f, tolerance, "the rotations should match (q and -q are the same rotation)");

    [Fact]
    public void linear_translation_interpolates_and_clamps_outside_the_keys()
    {
        //Arrange
        ModelAnimationChannel channel = Channel(AnimationPath.Translation, AnimationInterpolation.Linear, new[] { 1f, 3f }, new[] { 0f, 0f, 0f, 2f, 4f, 6f }, 3);

        //Act / Assert
        Sample(channel, 2f).Should().Equal(1f, 2f, 3f);
        Sample(channel, 1.5f).Should().Equal(0.5f, 1f, 1.5f);
        Sample(channel, 0f).Should().Equal(0f, 0f, 0f);
        Sample(channel, 1f).Should().Equal(0f, 0f, 0f);
        Sample(channel, 3f).Should().Equal(2f, 4f, 6f);
        Sample(channel, 99f).Should().Equal(2f, 4f, 6f);
    }

    [Fact]
    public void step_holds_each_key_until_the_next_key_time()
    {
        //Arrange
        ModelAnimationChannel channel = Channel(AnimationPath.Scale, AnimationInterpolation.Step, new[] { 0f, 1f, 2f }, new[] { 1f, 1f, 1f, 2f, 2f, 2f, 3f, 3f, 3f }, 3);

        //Act / Assert
        Sample(channel, 0f)[0].Should().Be(1f);
        Sample(channel, 0.999f)[0].Should().Be(1f);
        Sample(channel, 1f)[0].Should().Be(2f);
        Sample(channel, 1.5f)[0].Should().Be(2f);
        Sample(channel, 2f)[0].Should().Be(3f);
        Sample(channel, 7f)[0].Should().Be(3f);
        Sample(channel, -1f)[0].Should().Be(1f);
    }

    [Fact]
    public void linear_rotation_is_a_slerp_on_the_shorter_arc()
    {
        //Arrange
        Quaternion start = Quaternion.Identity;
        Quaternion end = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        float[] values = new float[8];
        Q(start).CopyTo(values, 0);
        Q(end).CopyTo(values, 4);
        float[] flipped = new float[8];
        Q(start).CopyTo(flipped, 0);
        Q(-end).CopyTo(flipped, 4);
        ModelAnimationChannel channel = Channel(AnimationPath.Rotation, AnimationInterpolation.Linear, new[] { 0f, 1f }, values, 4);
        ModelAnimationChannel opposite = Channel(AnimationPath.Rotation, AnimationInterpolation.Linear, new[] { 0f, 1f }, flipped, 4);

        //Act
        Quaternion quarter = SampleRotation(channel, 0.25f);
        Quaternion half = SampleRotation(channel, 0.5f);
        Quaternion halfFlipped = SampleRotation(opposite, 0.5f);

        //Assert - slerp keeps constant angular speed: a quarter of the time is a quarter of the angle.
        ShouldBeRotation(quarter, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 8f));
        ShouldBeRotation(half, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4f));
        ShouldBeRotation(halfFlipped, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4f));
        half.Length().Should().BeApproximately(1f, 1e-6f);
    }

    [Fact]
    public void slerp_handles_nearly_equal_and_nearly_opposite_keys()
    {
        //Arrange
        Quaternion a = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f);
        Quaternion b = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f + 1e-5f);
        Quaternion c = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI - 0.01f);

        //Act
        Quaternion near = AnimationSampling.Slerp(a, b, 0.5f);
        Quaternion wide = AnimationSampling.Slerp(Quaternion.Identity, c, 0.5f);

        //Assert
        ShouldBeRotation(near, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f + 5e-6f));
        ShouldBeRotation(wide, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (MathF.PI - 0.01f) / 2f), 1e-4f);
        float.IsNaN(near.W).Should().BeFalse();
    }

    [Fact]
    public void cubic_spline_reproduces_a_cubic_with_tangents_scaled_by_uneven_key_intervals()
    {
        //Arrange - x(t) = t^3 with exact derivatives as tangents at keys 0, 2 and 3 (intervals 2 and 1).
        float[] times = { 0f, 2f, 3f };
        float[] values = new float[times.Length * 9];
        for (int key = 0; key < times.Length; key++)
        {
            float t = times[key];
            float slope = 3f * t * t;
            values[(key * 9) + 0] = slope;
            values[(key * 9) + 3] = t * t * t;
            values[(key * 9) + 6] = slope;
            values[(key * 9) + 4] = 7f;
            values[(key * 9) + 5] = -2f * t;
            values[(key * 9) + 8] = -2f;
            values[(key * 9) + 2] = -2f;
        }

        ModelAnimationChannel channel = Channel(AnimationPath.Translation, AnimationInterpolation.CubicSpline, times, values, 3);

        //Act / Assert - a Hermite spline with exact tangents is exact for cubics, linear functions and constants.
        foreach (float t in new[] { 0.5f, 1f, 1.5f, 2.5f, 2.9f })
        {
            float[] value = Sample(channel, t);
            value[0].Should().BeApproximately(t * t * t, 1e-4f, "x at " + t);
            value[1].Should().BeApproximately(7f, 1e-5f, "y at " + t);
            value[2].Should().BeApproximately(-2f * t, 1e-4f, "z at " + t);
        }

        Sample(channel, -1f).Should().Equal(0f, 7f, -0f);
        Sample(channel, 3f)[0].Should().Be(27f);
        Sample(channel, 10f)[0].Should().Be(27f);
    }

    [Fact]
    public void cubic_spline_rotations_are_normalised_and_hit_the_keys()
    {
        //Arrange - zero tangents: the spline eases between the keys; the raw blend is not unit length.
        Quaternion start = Quaternion.Identity;
        Quaternion end = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        float[] values = new float[24];
        Q(start).CopyTo(values, 4);
        Q(end).CopyTo(values, 16);
        ModelAnimationChannel channel = Channel(AnimationPath.Rotation, AnimationInterpolation.CubicSpline, new[] { 0f, 1f }, values, 4);

        //Act
        Quaternion half = SampleRotation(channel, 0.5f);
        Quaternion atEnd = SampleRotation(channel, 1f);

        //Assert - with zero tangents at t = 0.5 both keys weigh 0.5, so the result is their normalised sum (45 degrees).
        half.Length().Should().BeApproximately(1f, 1e-6f);
        ShouldBeRotation(half, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4f));
        ShouldBeRotation(atEnd, end);
    }

    [Fact]
    public void morph_weight_channels_interpolate_every_target()
    {
        //Arrange
        ModelAnimationChannel linear = Channel(AnimationPath.Weights, AnimationInterpolation.Linear, new[] { 0f, 1f }, new[] { 0f, 1f, 1f, 0f }, 2);
        ModelAnimationChannel step = Channel(AnimationPath.Weights, AnimationInterpolation.Step, new[] { 0f, 1f }, new[] { 0f, 1f, 1f, 0f }, 2);

        //Act / Assert
        Sample(linear, 0.25f).Should().Equal(0.25f, 0.75f);
        Sample(step, 0.25f).Should().Equal(0f, 1f);
        Sample(step, 1f).Should().Equal(1f, 0f);
    }

    [Fact]
    public void key_cursor_finds_the_same_interval_as_a_search_in_any_order()
    {
        //Arrange
        float[] times = new float[50];
        for (int index = 0; index < times.Length; index++) times[index] = index * 0.1f + (index % 3) * 0.01f;
        Random random = new Random(7);
        int cursor = 0;

        //Act / Assert - forward steps, jumps back and random seeks all agree with a plain search.
        for (int step = 0; step < 2000; step++)
        {
            float time = step < 1000 ? step * 0.006f - 0.5f : (float)(random.NextDouble() * 6 - 0.5);
            int expected = -1;
            for (int index = 0; index < times.Length; index++)
            {
                if (times[index] <= time) expected = index;
            }

            AnimationSampling.FindKey(times, time, ref cursor).Should().Be(expected, "time " + time);
        }
    }

    [Fact]
    public void sample_rejects_a_short_destination()
    {
        //Arrange
        ModelAnimationChannel channel = Channel(AnimationPath.Rotation, AnimationInterpolation.Linear, new[] { 0f }, Q(Quaternion.Identity), 4);

        //Act
        Action act = () => channel.Sample(0f, new float[3]);

        //Assert
        act.Should().Throw<ArgumentException>();
    }
}
