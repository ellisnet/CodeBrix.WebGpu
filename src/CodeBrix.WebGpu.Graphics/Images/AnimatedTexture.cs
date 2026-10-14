using System;
using CodeBrix.Imaging;
using CodeBrix.Imaging.Formats.Gif;
using CodeBrix.Imaging.Formats.Jxl;
using CodeBrix.Imaging.PixelFormats;
using CodeBrix.WebGpu.Graphics.Images;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// An animated image on the GPU (an animated GIF or JPEG XL, or any multi-frame CodeBrix.Imaging image, or a list of frames)
/// with per-frame durations. <see cref="GetFrameTexture(TimeSpan)" /> gives the frame to show at an elapsed time as an
/// ordinary 2D <see cref="GpuTexture" /> - draw it with a <see cref="SpriteBatch" /> (or
/// <see cref="SpriteBatch.Draw(AnimatedTexture, TimeSpan, System.Drawing.RectangleF, Color4)" />), bind it, or use it
/// as a material texture. Picking a frame allocates nothing.
/// </summary>
/// <remarks>
/// <para>
/// Storage (<see cref="Storage" />): normally every frame is uploaded once into its own layer of a 2D texture array
/// (<see cref="AnimatedTextureStorage.TextureArray" />, with optional mipmaps) and each layer has a ready 2D view. When
/// the array would exceed <see cref="AnimatedTextureOptions.MaxArrayBytes" /> or the device's MaxTextureArrayLayers,
/// the decoded frames stay in memory and the current frame is uploaded into a small ring of layers when it changes
/// (<see cref="AnimatedTextureStorage.Streaming" />); queue writes are ordered with submissions, so a frame recorded
/// before the next change still shows the right picture.
/// </para>
/// <para>
/// Timing: GIF frame delays are read from the file (a delay of 0 or 1 hundredths of a second plays as 100 ms, as web
/// browsers do), and so are JPEG XL frame durations (ticks at the file's ticks-per-second rate; a zero duration plays
/// as <see cref="AnimatedTextureOptions.DefaultFrameDuration" />); sources without timing use <see cref="AnimatedTextureOptions.DefaultFrameDuration" />, and
/// <see cref="AnimatedTextureOptions.FrameDurations" /> overrides both.
/// </para>
/// </remarks>
public sealed class AnimatedTexture : GraphicsResource
{
    private readonly long[] frameEnds;
    private readonly TimeSpan[] durations;
    private readonly GpuTexture[] views;
    private readonly int[] slotFrames;
    private Image<Rgba32> retained;
    private Memory<Rgba32>[] retainedPixels;
    private int nextSlot;

    private AnimatedTexture(GraphicsContext context, Image<Rgba32> image, bool owned, AnimatedTextureOptions options)
        : base(context, options.Label)
    {
        int count = image.Frames.Count;
        FrameCount = count;
        Width = image.Width;
        Height = image.Height;
        LoopMode = options.LoopMode;
        Format = options.ColorSpace == TextureColorSpace.Srgb ? TextureFormat.RGBA8UnormSrgb : TextureFormat.RGBA8Unorm;
        durations = ReadDurations(image, options);
        frameEnds = new long[count];
        long total = 0;
        for (int index = 0; index < count; index++)
        {
            total += durations[index].Ticks;
            frameEnds[index] = total;
        }

        TotalDuration = TimeSpan.FromTicks(total);
        Storage = ChooseStorage(context, Width, Height, count, options);
        try
        {
            if (Storage == AnimatedTextureStorage.TextureArray)
            {
                int mips = options.Mipmaps ? GpuTexture.FullMipChainLength(Width, Height) : 1;
                TextureUsage usage = TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.CopySrc | (mips > 1 ? TextureUsage.RenderAttachment : TextureUsage.None);
                ArrayTexture = context.CreateTextureArray(Width, Height, count, Format, usage, mips, Label == null ? "animated frames" : Label + " frames");
                for (int index = 0; index < count; index++) ImageUpload.Upload(ArrayTexture, image.Frames[index], index, options.PremultiplyAlpha);
                if (mips > 1) context.GenerateMipmaps(ArrayTexture);
                views = new GpuTexture[count];
                for (int index = 0; index < count; index++) views[index] = ArrayTexture.CreateLayerView(index);
                if (owned) image.Dispose();
            }
            else
            {
                int slots = Math.Clamp(options.StreamingSlots, 2, 16);
                retained = owned ? image : image.Clone(ImageUpload.Decoding);
                if (options.PremultiplyAlpha) PremultiplyAll(retained);

                // Each frame's pixel block is looked up once here: CodeBrix.Imaging's single-block lookup enumerates
                // (allocates), and streaming uploads happen during playback. Empty = a multi-block frame, uploaded
                // segment by segment.
                retainedPixels = new Memory<Rgba32>[count];
                for (int index = 0; index < count; index++)
                {
                    if (retained.Frames[index].DangerousTryGetSinglePixelMemory(out Memory<Rgba32> pixels)) retainedPixels[index] = pixels;
                }

                ArrayTexture = context.CreateTextureArray(Width, Height, slots, Format, TextureUsage.TextureBinding | TextureUsage.CopyDst | TextureUsage.CopySrc, 1, Label == null ? "animated ring" : Label + " ring");
                views = new GpuTexture[slots];
                slotFrames = new int[slots];
                for (int slot = 0; slot < slots; slot++)
                {
                    views[slot] = ArrayTexture.CreateLayerView(slot);
                    slotFrames[slot] = -1;
                }
            }
        }
        catch
        {
            Dispose();
            if (owned) image.Dispose();
            throw;
        }
    }

    /// <summary>The number of frames.</summary>
    public int FrameCount { get; }

    /// <summary>The width in pixels.</summary>
    public int Width { get; }

    /// <summary>The height in pixels.</summary>
    public int Height { get; }

    /// <summary>The texture format (RGBA8, sRGB unless the options chose linear).</summary>
    public TextureFormat Format { get; }

    /// <summary>Where the frames live.</summary>
    public AnimatedTextureStorage Storage { get; }

    /// <summary>
    /// The texture array: one layer per frame (<see cref="AnimatedTextureStorage.TextureArray" />), or the ring of
    /// streaming layers. Use <see cref="GetLayer(TimeSpan)" /> to index it from your own shaders.
    /// </summary>
    public GpuTexture ArrayTexture { get; private set; }

    /// <summary>How playback continues after the last frame (may be changed at any time).</summary>
    public AnimationLoopMode LoopMode { get; set; }

    /// <summary>The duration of one forward pass through every frame.</summary>
    public TimeSpan TotalDuration { get; }

    /// <summary>The duration of one frame.</summary>
    /// <param name="frame">The frame index.</param>
    /// <returns>The duration.</returns>
    public TimeSpan GetFrameDuration(int frame)
    {
        if ((uint)frame >= (uint)FrameCount) throw new ArgumentOutOfRangeException(nameof(frame));
        return durations[frame];
    }

    /// <summary>The frame shown after <paramref name="elapsed" /> of playback (looping, ping-pong or once per <see cref="LoopMode" />).</summary>
    /// <param name="elapsed">The playback time (negative counts as zero).</param>
    /// <returns>The frame index.</returns>
    public int GetFrameIndex(TimeSpan elapsed)
    {
        long total = frameEnds[^1];
        int last = FrameCount - 1;
        if (last == 0 || total <= 0) return 0;
        long time = Math.Max(0, elapsed.Ticks);
        switch (LoopMode)
        {
            case AnimationLoopMode.Once:
                return time >= total ? last : Find(time);
            case AnimationLoopMode.PingPong:
                long back = total - durations[0].Ticks - durations[last].Ticks;
                if (back <= 0) return Find(time % total);
                long position = time % (total + back);
                if (position < total) return Find(position);
                return Find(frameEnds[last - 1] - 1 - (position - total));
            default:
                return Find(time % total);
        }
    }

    /// <summary>
    /// The 2D texture of the frame shown after <paramref name="elapsed" />. With streaming storage the frame is
    /// uploaded into the next ring layer when it is not resident (a queue write; no allocation).
    /// </summary>
    /// <param name="elapsed">The playback time.</param>
    /// <returns>A 2D view owned by this object (do not dispose it).</returns>
    public GpuTexture GetFrameTexture(TimeSpan elapsed) => GetFrameTextureAt(GetFrameIndex(elapsed));

    /// <summary>The 2D texture of one frame (see <see cref="GetFrameTexture(TimeSpan)" />).</summary>
    /// <param name="frame">The frame index.</param>
    /// <returns>A 2D view owned by this object.</returns>
    public GpuTexture GetFrameTextureAt(int frame) => views[Resident(frame)];

    /// <summary>The layer of <see cref="ArrayTexture" /> holding the frame shown after <paramref name="elapsed" /> (uploading it first when streaming).</summary>
    /// <param name="elapsed">The playback time.</param>
    /// <returns>The layer index.</returns>
    public int GetLayer(TimeSpan elapsed) => Resident(GetFrameIndex(elapsed));

    /// <summary>Creates an animated texture from a file (decoded by CodeBrix.Imaging; see <see cref="GraphicsContext.CreateAnimatedTexture(string, AnimatedTextureOptions)" />).</summary>
    /// <param name="context">The context.</param>
    /// <param name="image">The decoded image.</param>
    /// <param name="owned">True when the texture may keep or dispose the image.</param>
    /// <param name="options">The options.</param>
    /// <returns>The texture.</returns>
    internal static AnimatedTexture Create(GraphicsContext context, Image<Rgba32> image, bool owned, AnimatedTextureOptions options) =>
        new AnimatedTexture(context, image, owned, options ?? new AnimatedTextureOptions());

    private protected override void ReleaseNative()
    {
        if (views != null)
        {
            foreach (GpuTexture view in views) view?.Dispose();
        }

        ArrayTexture?.Dispose();
        ArrayTexture = null;
        retained?.Dispose();
        retained = null;
        retainedPixels = null;
    }

    private int Resident(int frame)
    {
        ThrowIfDisposed();
        if ((uint)frame >= (uint)FrameCount) throw new ArgumentOutOfRangeException(nameof(frame));
        if (Storage == AnimatedTextureStorage.TextureArray) return frame;
        for (int slot = 0; slot < slotFrames.Length; slot++)
        {
            if (slotFrames[slot] == frame) return slot;
        }

        int target = nextSlot;
        nextSlot = (nextSlot + 1) % slotFrames.Length;
        Memory<Rgba32> pixels = retainedPixels[frame];
        if (pixels.Length == Width * Height) ArrayTexture.WriteRegion(new TextureRegion(0, 0, Width, Height, 0, target), System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.Span));
        else ImageUpload.Upload(ArrayTexture, retained.Frames[frame], target, false);
        slotFrames[target] = frame;
        return target;
    }

    private int Find(long time)
    {
        int low = 0;
        int high = frameEnds.Length - 1;
        while (low < high)
        {
            int middle = (low + high) >> 1;
            if (frameEnds[middle] > time) high = middle;
            else low = middle + 1;
        }

        return low;
    }

    /// <summary>
    /// Reads the per-frame durations: the one place format-specific timing is read (GIF delays, JPEG XL tick
    /// durations); options override, sources without timing use the default duration.
    /// </summary>
    /// <param name="image">The decoded image.</param>
    /// <param name="options">The options.</param>
    /// <returns>One duration per frame (all positive).</returns>
    private static TimeSpan[] ReadDurations(Image<Rgba32> image, AnimatedTextureOptions options)
    {
        int count = image.Frames.Count;
        TimeSpan[] result = new TimeSpan[count];
        bool gif = image.Format is GifFormat;
        bool jxl = image.Format is JxlFormat;
        double jxlTicksToSeconds = 0;
        if (jxl)
        {
            JxlMetadata metadata = image.Metadata.GetJxlMetadata();
            if (metadata.TicksPerSecondNumerator != 0) jxlTicksToSeconds = (double)metadata.TicksPerSecondDenominator / metadata.TicksPerSecondNumerator;
        }

        TimeSpan fallback = options.DefaultFrameDuration > TimeSpan.Zero ? options.DefaultFrameDuration : TimeSpan.FromMilliseconds(100);
        for (int index = 0; index < count; index++)
        {
            TimeSpan duration = fallback;
            if (options.FrameDurations != null && options.FrameDurations.Length > 0)
            {
                duration = options.FrameDurations[Math.Min(index, options.FrameDurations.Length - 1)];
            }
            else if (gif)
            {
                int delay = image.Frames[index].Metadata.GetGifMetadata().FrameDelay;
                duration = delay <= 1 ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromMilliseconds(delay * 10.0);
            }
            else if (jxl && count > 1 && jxlTicksToSeconds > 0)
            {
                uint ticks = image.Frames[index].Metadata.GetJxlMetadata().Duration;
                if (ticks > 0) duration = TimeSpan.FromTicks((long)Math.Round(ticks * jxlTicksToSeconds * TimeSpan.TicksPerSecond));
            }

            result[index] = duration > TimeSpan.Zero ? duration : fallback;
        }

        return result;
    }

    /// <summary>Chooses the storage for a size and frame count.</summary>
    /// <param name="context">The context.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="count">The frame count.</param>
    /// <param name="options">The options.</param>
    /// <returns>The storage.</returns>
    internal static AnimatedTextureStorage ChooseStorage(GraphicsContext context, int width, int height, int count, AnimatedTextureOptions options)
    {
        if (options.Storage.HasValue) return options.Storage.Value;
        uint maxLayers = context.Limits.MaxTextureArrayLayers;
        if (maxLayers != 0 && count > maxLayers) return AnimatedTextureStorage.Streaming;
        double bytes = (double)width * height * 4 * count * (options.Mipmaps ? 4.0 / 3.0 : 1.0);
        return bytes > options.MaxArrayBytes ? AnimatedTextureStorage.Streaming : AnimatedTextureStorage.TextureArray;
    }

    private static void PremultiplyAll(Image<Rgba32> image)
    {
        for (int index = 0; index < image.Frames.Count; index++)
        {
            image.Frames[index].ProcessPixelRows(accessor =>
            {
                for (int row = 0; row < accessor.Height; row++)
                {
                    ImageUpload.Premultiply(System.Runtime.InteropServices.MemoryMarshal.AsBytes(accessor.GetRowSpan(row)));
                }
            });
        }
    }
}
