using System;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>How an animation continues after its last frame.</summary>
public enum AnimationLoopMode
{
    /// <summary>Start again from the first frame (forever).</summary>
    Loop,

    /// <summary>Play forwards, then backwards, then forwards again (the end frames are not repeated).</summary>
    PingPong,

    /// <summary>Play once and hold the last frame.</summary>
    Once,
}

/// <summary>Where the frames of an <see cref="AnimatedTexture" /> live on the GPU.</summary>
public enum AnimatedTextureStorage
{
    /// <summary>Every frame is uploaded once into its own layer of a texture array; drawing a frame is free.</summary>
    TextureArray,

    /// <summary>
    /// The decoded frames stay in memory and the current frame is uploaded into a small ring of texture layers when it
    /// changes - for animations too large (or with too many frames) for one array. No mipmaps.
    /// </summary>
    Streaming,
}

/// <summary>Options for <see cref="GraphicsContext.CreateAnimatedTexture(string, AnimatedTextureOptions)" /> and its siblings.</summary>
public sealed class AnimatedTextureOptions
{
    /// <summary>The default: 256 MiB (frames x width x height x 4 bytes, plus a third for mipmaps).</summary>
    public const long DefaultMaxArrayBytes = 256L * 1024 * 1024;

    /// <summary>How playback continues after the last frame. Default <see cref="AnimationLoopMode.Loop" />.</summary>
    public AnimationLoopMode LoopMode { get; set; } = AnimationLoopMode.Loop;

    /// <summary>The colour space of the frames. Default <see cref="TextureColorSpace.Srgb" />.</summary>
    public TextureColorSpace ColorSpace { get; set; } = TextureColorSpace.Srgb;

    /// <summary>True for GPU-generated mipmaps on every layer (texture-array storage only). Default false.</summary>
    public bool Mipmaps { get; set; }

    /// <summary>True to premultiply colour by alpha once, while loading. Default false.</summary>
    public bool PremultiplyAlpha { get; set; }

    /// <summary>The duration of frames whose file carries no timing (and of every frame of a non-GIF source). Default 100 ms.</summary>
    public TimeSpan DefaultFrameDuration { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Per-frame durations that replace the file's timing (null = use the file's). Shorter arrays repeat their last value.</summary>
    public TimeSpan[] FrameDurations { get; set; }

    /// <summary>
    /// The largest texture array (bytes, including mipmaps) the texture-array storage may use; above it, or above the
    /// device's MaxTextureArrayLayers, the animation streams. Default <see cref="DefaultMaxArrayBytes" />.
    /// </summary>
    public long MaxArrayBytes { get; set; } = DefaultMaxArrayBytes;

    /// <summary>Forces a storage instead of choosing by size (null = choose). Default null.</summary>
    public AnimatedTextureStorage? Storage { get; set; }

    /// <summary>The number of ring layers of the streaming storage (2 to 16). Default 3.</summary>
    public int StreamingSlots { get; set; } = 3;

    /// <summary>A label, or null.</summary>
    public string Label { get; set; }
}
