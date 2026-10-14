using System;
using System.Collections.Generic;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>Which node property an animation channel drives.</summary>
public enum AnimationPath
{
    /// <summary>The translation (3 floats per key).</summary>
    Translation,

    /// <summary>The rotation (4 floats per key: x, y, z, w).</summary>
    Rotation,

    /// <summary>The scale (3 floats per key).</summary>
    Scale,

    /// <summary>The morph weights (one float per morph target per key).</summary>
    Weights,
}

/// <summary>How an animation channel interpolates between keys.</summary>
public enum AnimationInterpolation
{
    /// <summary>Hold each key's value until the next key.</summary>
    Step,

    /// <summary>Linear (rotations: spherical linear).</summary>
    Linear,

    /// <summary>Cubic Hermite spline; each key stores in-tangent, value and out-tangent.</summary>
    CubicSpline,
}

/// <summary>
/// One decoded animation channel (immutable): the target node and property, the key times and the packed key values.
/// <see cref="Values" /> holds <see cref="ComponentCount" /> floats per key (x3 for
/// <see cref="AnimationInterpolation.CubicSpline" />: in-tangent, value, out-tangent of each key in turn).
/// </summary>
public sealed class ModelAnimationChannel
{
    internal ModelAnimationChannel(int node, AnimationPath path, AnimationInterpolation interpolation, float[] times, float[] values, int componentCount)
    {
        Node = node;
        Path = path;
        Interpolation = interpolation;
        TimeArray = times;
        ValueArray = values;
        ComponentCount = componentCount;
    }

    /// <summary>The target node index.</summary>
    public int Node { get; }

    /// <summary>The animated property.</summary>
    public AnimationPath Path { get; }

    /// <summary>The interpolation.</summary>
    public AnimationInterpolation Interpolation { get; }

    /// <summary>The key times in seconds (ascending).</summary>
    public ReadOnlyMemory<float> Times => TimeArray;

    /// <summary>The packed key values.</summary>
    public ReadOnlyMemory<float> Values => ValueArray;

    /// <summary>Floats per value (3 translation / scale, 4 rotation, the morph-target count for weights).</summary>
    public int ComponentCount { get; }

    /// <summary>The number of keys.</summary>
    public int KeyCount => Times.Length;

    /// <summary>The time of the last key.</summary>
    public float EndTime => TimeArray.Length == 0 ? 0f : TimeArray[^1];

    /// <summary>The key times (shared, never written).</summary>
    internal float[] TimeArray { get; }

    /// <summary>The packed key values (shared, never written).</summary>
    internal float[] ValueArray { get; }

    /// <summary>
    /// Evaluates the channel at a clip time (glTF 2.0 rules: STEP holds the key value; LINEAR interpolates, rotations
    /// by spherical linear interpolation on the shorter arc; CUBICSPLINE is the Hermite spline with the tangents scaled
    /// by the key interval, rotations normalised). Before the first key the first value, after the last key the last
    /// value. Allocation-free; binary search per call (<see cref="AnimationPlayer" /> keeps a key cursor per channel
    /// instead).
    /// </summary>
    /// <param name="time">The clip time in seconds.</param>
    /// <param name="destination">Receives <see cref="ComponentCount" /> floats (x, y, z[, w] or one weight per morph target).</param>
    /// <exception cref="ArgumentException"><paramref name="destination" /> is shorter than <see cref="ComponentCount" />.</exception>
    public void Sample(float time, Span<float> destination)
    {
        if (destination.Length < ComponentCount) throw new ArgumentException("The destination needs " + ComponentCount + " floats.", nameof(destination));
        int cursor = 0;
        Animation.AnimationSampling.Sample(this, time, ref cursor, destination);
    }
}

/// <summary>A glTF animation clip: a name, a duration and its channels (immutable).</summary>
public sealed class ModelAnimation
{
    internal ModelAnimation(int index, string name, ModelAnimationChannel[] channels)
    {
        Index = index;
        Name = name;
        ChannelArray = channels;
        float duration = 0f;
        foreach (ModelAnimationChannel channel in channels) duration = Math.Max(duration, channel.EndTime);
        Duration = duration;
    }

    /// <summary>The index in <see cref="Model.Animations" />.</summary>
    public int Index { get; }

    /// <summary>The name, or null.</summary>
    public string Name { get; }

    /// <summary>The length in seconds (the last key time of any channel).</summary>
    public float Duration { get; }

    /// <summary>The channels (node-property channels only; KHR_animation_pointer channels are skipped).</summary>
    public IReadOnlyList<ModelAnimationChannel> Channels => ChannelArray;

    /// <summary>The channels as an array (shared, never written).</summary>
    internal ModelAnimationChannel[] ChannelArray { get; }

    /// <summary>The name and duration.</summary>
    /// <returns>For example <c>ModelAnimation 0 'walk' 1.25 s</c>.</returns>
    public override string ToString() => "ModelAnimation " + Index + (Name == null ? string.Empty : " '" + Name + "'") + " " + Duration.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " s";
}
