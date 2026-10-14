using System;
using System.Numerics;

namespace CodeBrix.WebGpu.Graphics.Animation;

/// <summary>
/// Allocation-free evaluation of one <see cref="ModelAnimationChannel" /> at a time (glTF 2.0 Appendix C): STEP holds
/// the key value, LINEAR interpolates (rotations by spherical linear interpolation on the shorter arc), CUBICSPLINE is
/// the Hermite spline with tangents scaled by the key interval (rotations normalised). Times before the first key give
/// the first value, times after the last key the last value. Key lookup uses a caller-kept cursor (the interval found
/// last time) and falls back to a binary search, so playback that moves forward costs O(1) per channel.
/// </summary>
internal static class AnimationSampling
{
    /// <summary>
    /// The key interval holding <paramref name="time" />: <c>k</c> with <c>times[k] &lt;= time &lt; times[k + 1]</c>,
    /// -1 before the first key, the last index at or after the last key.
    /// </summary>
    /// <param name="times">The ascending key times (at least one).</param>
    /// <param name="time">The time.</param>
    /// <param name="cursor">The interval found last time (updated).</param>
    /// <returns>The key index.</returns>
    public static int FindKey(ReadOnlySpan<float> times, float time, ref int cursor)
    {
        int last = times.Length - 1;
        if (time < times[0])
        {
            cursor = 0;
            return -1;
        }

        if (time >= times[last])
        {
            cursor = last;
            return last;
        }

        // Here 0 <= key < last. Try the cached interval and the next two before searching.
        int key = cursor;
        if ((uint)key < (uint)last && times[key] <= time)
        {
            for (int step = 0; step < 3 && key < last; step++, key++)
            {
                if (time < times[key + 1])
                {
                    cursor = key;
                    return key;
                }
            }
        }

        int low = 0;
        int high = last - 1;
        while (low < high)
        {
            int middle = (low + high + 1) >> 1;
            if (times[middle] <= time) low = middle;
            else high = middle - 1;
        }

        cursor = low;
        return low;
    }

    /// <summary>Evaluates a channel at a time into <paramref name="destination" /> (<see cref="ModelAnimationChannel.ComponentCount" /> floats).</summary>
    /// <param name="channel">The channel.</param>
    /// <param name="time">The clip time in seconds.</param>
    /// <param name="cursor">The channel's key cursor (updated).</param>
    /// <param name="destination">At least <see cref="ModelAnimationChannel.ComponentCount" /> floats.</param>
    public static void Sample(ModelAnimationChannel channel, float time, ref int cursor, Span<float> destination)
    {
        ReadOnlySpan<float> times = channel.TimeArray;
        ReadOnlySpan<float> values = channel.ValueArray;
        int components = channel.ComponentCount;
        bool cubic = channel.Interpolation == AnimationInterpolation.CubicSpline;
        int stride = cubic ? components * 3 : components;
        int valueOffset = cubic ? components : 0;
        int key = FindKey(times, time, ref cursor);
        int last = times.Length - 1;
        if (key < 0 || key >= last || channel.Interpolation == AnimationInterpolation.Step)
        {
            int index = key < 0 ? 0 : key;
            values.Slice((index * stride) + valueOffset, components).CopyTo(destination);
            return;
        }

        float start = times[key];
        float interval = times[key + 1] - start;
        float t = interval > 0f ? (time - start) / interval : 0f;
        bool rotation = channel.Path == AnimationPath.Rotation;
        if (!cubic)
        {
            ReadOnlySpan<float> a = values.Slice(key * components, components);
            ReadOnlySpan<float> b = values.Slice((key + 1) * components, components);
            if (rotation)
            {
                Quaternion q = Slerp(new Quaternion(a[0], a[1], a[2], a[3]), new Quaternion(b[0], b[1], b[2], b[3]), t);
                destination[0] = q.X;
                destination[1] = q.Y;
                destination[2] = q.Z;
                destination[3] = q.W;
                return;
            }

            for (int component = 0; component < components; component++) destination[component] = a[component] + ((b[component] - a[component]) * t);
            return;
        }

        // Hermite: p(t) = (2t^3 - 3t^2 + 1) v_k + td (t^3 - 2t^2 + t) b_k + (-2t^3 + 3t^2) v_k+1 + td (t^3 - t^2) a_k+1,
        // where b_k is key k's out-tangent and a_k+1 key k+1's in-tangent (glTF layout per key: in, value, out).
        float t2 = t * t;
        float t3 = t2 * t;
        float h00 = (2f * t3) - (3f * t2) + 1f;
        float h10 = (t3 - (2f * t2) + t) * interval;
        float h01 = (-2f * t3) + (3f * t2);
        float h11 = (t3 - t2) * interval;
        int first = key * stride;
        int next = (key + 1) * stride;
        for (int component = 0; component < components; component++)
        {
            float v0 = values[first + components + component];
            float out0 = values[first + (2 * components) + component];
            float in1 = values[next + component];
            float v1 = values[next + components + component];
            destination[component] = (h00 * v0) + (h10 * out0) + (h01 * v1) + (h11 * in1);
        }

        if (rotation) NormalizeQuaternion(destination);
    }

    /// <summary>Spherical linear interpolation on the shorter arc (normalised linear interpolation when the keys are nearly equal).</summary>
    /// <param name="a">The start (unit).</param>
    /// <param name="b">The end (unit).</param>
    /// <param name="t">The fraction.</param>
    /// <returns>The unit result.</returns>
    public static Quaternion Slerp(Quaternion a, Quaternion b, float t)
    {
        float dot = Quaternion.Dot(a, b);
        if (dot < 0f)
        {
            b = -b;
            dot = -dot;
        }

        float wa;
        float wb;
        if (dot > 0.9995f)
        {
            wa = 1f - t;
            wb = t;
        }
        else
        {
            float angle = MathF.Acos(dot);
            float sin = MathF.Sin(angle);
            wa = MathF.Sin((1f - t) * angle) / sin;
            wb = MathF.Sin(t * angle) / sin;
        }

        return Quaternion.Normalize((a * wa) + (b * wb));
    }

    /// <summary>Normalised linear interpolation on the shorter arc.</summary>
    /// <param name="a">The start (unit).</param>
    /// <param name="b">The end (unit).</param>
    /// <param name="t">The fraction.</param>
    /// <returns>The unit result.</returns>
    public static Quaternion Nlerp(Quaternion a, Quaternion b, float t)
    {
        if (Quaternion.Dot(a, b) < 0f) b = -b;
        Quaternion result = (a * (1f - t)) + (b * t);
        float length = result.Length();
        return length > 1e-12f ? result * (1f / length) : a;
    }

    /// <summary>Normalises a quaternion stored as four floats (identity when it is zero).</summary>
    /// <param name="value">x, y, z, w.</param>
    public static void NormalizeQuaternion(Span<float> value)
    {
        float length = MathF.Sqrt((value[0] * value[0]) + (value[1] * value[1]) + (value[2] * value[2]) + (value[3] * value[3]));
        if (length <= 1e-12f)
        {
            value[0] = 0f;
            value[1] = 0f;
            value[2] = 0f;
            value[3] = 1f;
            return;
        }

        float scale = 1f / length;
        value[0] *= scale;
        value[1] *= scale;
        value[2] *= scale;
        value[3] *= scale;
    }
}
