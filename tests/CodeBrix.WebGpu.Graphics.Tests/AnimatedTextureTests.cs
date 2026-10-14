using System;
using System.Collections.Generic;
using System.IO;
using CodeBrix.Imaging;
using CodeBrix.Imaging.Formats.Gif;
using CodeBrix.Imaging.PixelFormats;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using Xunit;
using RectangleF = System.Drawing.RectangleF;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>Animated textures from in-test animated GIF and JPEG XL files and from frame lists: timing, loop modes, storage, drawing.</summary>
public class AnimatedTextureTests : IClassFixture<GraphicsGpuFixture>
{
    private static readonly Rgba32[] Colors = { new Rgba32(255, 0, 0, 255), new Rgba32(0, 255, 0, 255), new Rgba32(0, 0, 255, 255) };
    private readonly GraphicsGpuFixture gpu;

    public AnimatedTextureTests(GraphicsGpuFixture fixture)
    {
        gpu = fixture;
    }

    /// <summary>A 4 x 4 animated GIF: red 100 ms, green 200 ms, blue 300 ms.</summary>
    private static MemoryStream Gif()
    {
        using Image<Rgba32> animation = new Image<Rgba32>(4, 4, Colors[0]);
        animation.Metadata.GetGifMetadata().RepeatCount = 0;
        animation.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay = 10;
        for (int index = 1; index < Colors.Length; index++)
        {
            using Image<Rgba32> frame = new Image<Rgba32>(4, 4, Colors[index]);
            ImageFrame<Rgba32> added = animation.Frames.AddFrame(frame.Frames.RootFrame);
            added.Metadata.GetGifMetadata().FrameDelay = (index + 1) * 10;
        }

        MemoryStream stream = new MemoryStream();
        animation.SaveAsGif(stream);
        stream.Position = 0;
        return stream;
    }

    private static Rgba32 ColorOf(GpuTexture texture)
    {
        using Image<Rgba32> image = texture.ToImage();
        return image[1, 1];
    }

    [Fact]
    public void gif_frames_and_delays_become_layers_and_durations()
    {
        //Arrange
        gpu.RequireDevice();
        using MemoryStream gif = Gif();

        //Act
        using AnimatedTexture texture = gpu.Context.CreateAnimatedTexture(gif, new AnimatedTextureOptions { ColorSpace = TextureColorSpace.Linear });

        //Assert
        texture.FrameCount.Should().Be(3);
        texture.Storage.Should().Be(AnimatedTextureStorage.TextureArray);
        texture.ArrayTexture.DepthOrArrayLayers.Should().Be(3);
        texture.GetFrameDuration(0).Should().Be(TimeSpan.FromMilliseconds(100));
        texture.GetFrameDuration(2).Should().Be(TimeSpan.FromMilliseconds(300));
        texture.TotalDuration.Should().Be(TimeSpan.FromMilliseconds(600));
        ColorOf(texture.GetFrameTexture(TimeSpan.FromMilliseconds(50))).Should().Be(Colors[0]);
        ColorOf(texture.GetFrameTexture(TimeSpan.FromMilliseconds(150))).Should().Be(Colors[1]);
        ColorOf(texture.GetFrameTexture(TimeSpan.FromMilliseconds(450))).Should().Be(Colors[2]);
        texture.GetLayer(TimeSpan.FromMilliseconds(299)).Should().Be(1);
        gpu.Context.TakeErrors().Should().BeEmpty();
    }

    [Fact]
    public void loop_ping_pong_and_once_pick_the_right_frames()
    {
        //Arrange
        gpu.RequireDevice();
        using MemoryStream gif = Gif();
        using AnimatedTexture texture = gpu.Context.CreateAnimatedTexture(gif);

        //Act
        int Frame(AnimationLoopMode mode, int milliseconds)
        {
            texture.LoopMode = mode;
            return texture.GetFrameIndex(TimeSpan.FromMilliseconds(milliseconds));
        }

        //Assert
        Frame(AnimationLoopMode.Loop, 650).Should().Be(0);
        Frame(AnimationLoopMode.Loop, 720).Should().Be(1);
        Frame(AnimationLoopMode.Loop, -5).Should().Be(0);
        Frame(AnimationLoopMode.Once, 5000).Should().Be(2);
        Frame(AnimationLoopMode.Once, 599).Should().Be(2);
        Frame(AnimationLoopMode.PingPong, 650).Should().Be(1);
        Frame(AnimationLoopMode.PingPong, 790).Should().Be(1);
        Frame(AnimationLoopMode.PingPong, 810).Should().Be(0);
        Frame(AnimationLoopMode.PingPong, 950).Should().Be(1);
    }

    [Fact]
    public void streaming_storage_uploads_frames_into_a_ring_on_demand()
    {
        //Arrange
        gpu.RequireDevice();
        using MemoryStream gif = Gif();
        using AnimatedTexture texture = gpu.Context.CreateAnimatedTexture(gif, new AnimatedTextureOptions { Storage = AnimatedTextureStorage.Streaming, StreamingSlots = 2, ColorSpace = TextureColorSpace.Linear });

        //Act
        Rgba32 first = ColorOf(texture.GetFrameTextureAt(0));
        Rgba32 second = ColorOf(texture.GetFrameTextureAt(1));
        Rgba32 third = ColorOf(texture.GetFrameTextureAt(2));
        Rgba32 again = ColorOf(texture.GetFrameTextureAt(1));

        //Assert
        texture.Storage.Should().Be(AnimatedTextureStorage.Streaming);
        texture.ArrayTexture.DepthOrArrayLayers.Should().Be(2);
        first.Should().Be(Colors[0]);
        second.Should().Be(Colors[1]);
        third.Should().Be(Colors[2]);
        again.Should().Be(Colors[1]);
    }

    [Fact]
    public void storage_streams_when_the_array_would_be_too_large()
    {
        //Arrange
        gpu.RequireDevice();
        using MemoryStream gif = Gif();

        //Act
        using AnimatedTexture texture = gpu.Context.CreateAnimatedTexture(gif, new AnimatedTextureOptions { MaxArrayBytes = 100 });

        //Assert
        texture.Storage.Should().Be(AnimatedTextureStorage.Streaming);
    }

    [Fact]
    public void frame_lists_take_their_durations_from_the_options_and_mipmap_every_layer()
    {
        //Arrange
        gpu.RequireDevice();
        using Image<Rgba32> a = new Image<Rgba32>(8, 8, Colors[0]);
        using Image<Rgb24> b = new Image<Rgb24>(8, 8, new Rgb24(0, 255, 0));

        //Act
        using AnimatedTexture texture = gpu.Context.CreateAnimatedTexture(new List<Image> { a, b }, new AnimatedTextureOptions { FrameDurations = new[] { TimeSpan.FromSeconds(1) }, Mipmaps = true, ColorSpace = TextureColorSpace.Linear });
        using Image<Rgba32> smallest = texture.GetFrameTextureAt(1).ToImage(3);

        //Assert
        texture.FrameCount.Should().Be(2);
        texture.TotalDuration.Should().Be(TimeSpan.FromSeconds(2));
        texture.ArrayTexture.MipLevelCount.Should().Be(4);
        smallest[0, 0].Should().Be(new Rgba32(0, 255, 0, 255));
        Action mismatch = () => gpu.Context.CreateAnimatedTexture(new List<Image> { a, new Image<Rgba32>(2, 2) });
        mismatch.Should().Throw<ArgumentException>().WithMessage("*first frame is 8 x 8*");
    }

    [Fact]
    public void picking_frames_allocates_nothing()
    {
        //Arrange
        gpu.RequireDevice();
        using MemoryStream gif = Gif();
        using AnimatedTexture texture = gpu.Context.CreateAnimatedTexture(gif);
        texture.GetFrameTexture(TimeSpan.Zero);

        //Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        int sum = 0;
        for (int index = 0; index < 1000; index++) sum += texture.GetFrameTexture(TimeSpan.FromMilliseconds(index * 7)).Width;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        //Assert
        sum.Should().Be(4000);
        allocated.Should().Be(0);
    }

    [Fact]
    public void sprite_batch_draws_the_current_frame()
    {
        //Arrange
        gpu.RequireDevice();
        using MemoryStream gif = Gif();
        using AnimatedTexture texture = gpu.Context.CreateAnimatedTexture(gif, new AnimatedTextureOptions { ColorSpace = TextureColorSpace.Linear });
        using HeadlessRenderTarget target = gpu.Context.CreateHeadlessTarget(8, 8, TextureFormat.RGBA8Unorm, false);
        using SpriteBatch batch = gpu.Context.CreateSpriteBatch();

        //Act
        using (GraphicsFrame frame = gpu.Context.BeginFrame())
        {
            using (RenderPass pass = frame.BeginRenderPass(target.View, Color4.Black))
            {
                batch.Begin(pass);
                batch.Draw(texture, TimeSpan.FromMilliseconds(400), new RectangleF(0, 0, 8, 8), Color4.White);
                batch.End();
            }

            frame.Submit();
        }

        byte[] pixels = target.ReadRgba();

        //Assert
        TestPixels.At(pixels, 8, 4, 4).Should().Be(((byte)0, (byte)0, (byte)255, (byte)255));
    }

    [Fact]
    public void jpeg_xl_frames_and_tick_durations_become_layers_and_durations()
    {
        //Arrange - 100 ticks per second: 50, 100, 250 ms; 1000 ticks per second: 5, 10, 25 ms
        gpu.RequireDevice();
        byte[] centiseconds = TestJxl.Animation(4, Colors, new uint[] { 5, 10, 25 }, 100);
        byte[] milliseconds = TestJxl.Animation(4, Colors, new uint[] { 5, 10, 25 }, 1000);
        AnimatedTextureOptions linear = new AnimatedTextureOptions { ColorSpace = TextureColorSpace.Linear };

        //Act
        using AnimatedTexture texture = gpu.Context.CreateAnimatedTexture(new MemoryStream(centiseconds), linear);
        using AnimatedTexture fast = gpu.Context.CreateAnimatedTexture(new MemoryStream(milliseconds), linear);

        //Assert
        texture.FrameCount.Should().Be(3);
        texture.Storage.Should().Be(AnimatedTextureStorage.TextureArray);
        texture.GetFrameDuration(0).Should().Be(TimeSpan.FromMilliseconds(50));
        texture.GetFrameDuration(1).Should().Be(TimeSpan.FromMilliseconds(100));
        texture.GetFrameDuration(2).Should().Be(TimeSpan.FromMilliseconds(250));
        texture.TotalDuration.Should().Be(TimeSpan.FromMilliseconds(400));
        ColorOf(texture.GetFrameTexture(TimeSpan.FromMilliseconds(49))).Should().Be(Colors[0]);
        ColorOf(texture.GetFrameTexture(TimeSpan.FromMilliseconds(149))).Should().Be(Colors[1]);
        ColorOf(texture.GetFrameTexture(TimeSpan.FromMilliseconds(150))).Should().Be(Colors[2]);
        fast.GetFrameDuration(0).Should().Be(TimeSpan.FromMilliseconds(5));
        fast.GetFrameDuration(2).Should().Be(TimeSpan.FromMilliseconds(25));
        fast.TotalDuration.Should().Be(TimeSpan.FromMilliseconds(40));
        fast.GetFrameIndex(TimeSpan.FromMilliseconds(14)).Should().Be(1);
        gpu.Context.TakeErrors().Should().BeEmpty();
    }

    [Fact]
    public void jpeg_xl_animations_stream_and_pick_frames_without_allocating()
    {
        //Arrange
        gpu.RequireDevice();
        string folder = Directory.CreateTempSubdirectory("webgpu-jxl-anim-").FullName;
        try
        {
            string path = Path.Combine(folder, "spinner.jxl");
            File.WriteAllBytes(path, TestJxl.Animation(4, Colors, new uint[] { 10, 10, 10 }, 100));
            using AnimatedTexture texture = gpu.Context.CreateAnimatedTexture(path, new AnimatedTextureOptions { Storage = AnimatedTextureStorage.Streaming, StreamingSlots = 2, ColorSpace = TextureColorSpace.Linear });
            texture.GetFrameTexture(TimeSpan.Zero);

            //Act
            long before = GC.GetAllocatedBytesForCurrentThread();
            int sum = 0;
            for (int index = 0; index < 1000; index++) sum += texture.GetFrameTexture(TimeSpan.FromMilliseconds(index * 7)).Width;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            //Assert
            texture.Label.Should().Be("spinner.jxl");
            texture.Storage.Should().Be(AnimatedTextureStorage.Streaming);
            sum.Should().Be(4000);
            allocated.Should().Be(0);
            ColorOf(texture.GetFrameTexture(TimeSpan.FromMilliseconds(250))).Should().Be(Colors[2]);
            ColorOf(texture.GetFrameTexture(TimeSpan.FromMilliseconds(5))).Should().Be(Colors[0]);
            gpu.Context.TakeErrors().Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void a_still_jpeg_xl_is_a_one_frame_animation_and_option_durations_override_the_file()
    {
        //Arrange
        gpu.RequireDevice();
        using Image<Rgba32> still = new Image<Rgba32>(4, 4, Colors[1]);
        byte[] stillJxl = TestJxl.Encode(still);
        byte[] animation = TestJxl.Animation(4, Colors, new uint[] { 5, 10, 25 }, 100);

        //Act
        using AnimatedTexture single = gpu.Context.CreateAnimatedTexture(new MemoryStream(stillJxl), new AnimatedTextureOptions { DefaultFrameDuration = TimeSpan.FromMilliseconds(70) });
        using AnimatedTexture overridden = gpu.Context.CreateAnimatedTexture(new MemoryStream(animation), new AnimatedTextureOptions { FrameDurations = new[] { TimeSpan.FromMilliseconds(30) } });

        //Assert
        single.FrameCount.Should().Be(1);
        single.TotalDuration.Should().Be(TimeSpan.FromMilliseconds(70));
        overridden.TotalDuration.Should().Be(TimeSpan.FromMilliseconds(90));
    }

    [Fact]
    public void sprite_batch_draws_the_current_jpeg_xl_frame()
    {
        //Arrange
        gpu.RequireDevice();
        byte[] animation = TestJxl.Animation(4, Colors, new uint[] { 5, 10, 25 }, 100);
        using AnimatedTexture texture = gpu.Context.CreateAnimatedTexture(new MemoryStream(animation), new AnimatedTextureOptions { ColorSpace = TextureColorSpace.Linear });
        using HeadlessRenderTarget target = gpu.Context.CreateHeadlessTarget(8, 8, TextureFormat.RGBA8Unorm, false);
        using SpriteBatch batch = gpu.Context.CreateSpriteBatch();

        //Act
        using (GraphicsFrame frame = gpu.Context.BeginFrame())
        {
            using (RenderPass pass = frame.BeginRenderPass(target.View, Color4.Black))
            {
                batch.Begin(pass);
                batch.Draw(texture, TimeSpan.FromMilliseconds(120), new RectangleF(0, 0, 8, 8), Color4.White);
                batch.End();
            }

            frame.Submit();
        }

        byte[] pixels = target.ReadRgba();

        //Assert
        TestPixels.At(pixels, 8, 4, 4).Should().Be(((byte)0, (byte)255, (byte)0, (byte)255));
    }
}
