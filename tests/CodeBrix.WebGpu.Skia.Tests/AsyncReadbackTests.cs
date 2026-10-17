using System;
using System.Diagnostics;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using SkiaSharp;
using Xunit;

namespace CodeBrix.WebGpu.Skia.Tests;

/// <summary>
/// The asynchronous, multi-buffered read-back (<see cref="WebGpuSkiaTarget.BeginReadPixels" />) and the row-stride
/// handling of every copy out of a mapped read-back buffer.
/// </summary>
public class AsyncReadbackTests : IClassFixture<SkiaGpuFixture>
{
    // 37 pixels = 148 bytes per tight row; the staging rows are padded to 256 bytes.
    private const int Width = 37;
    private const int Height = 9;
    private const int TightRow = Width * 4;
    private readonly SkiaGpuFixture gpu;

    public AsyncReadbackTests(SkiaGpuFixture fixture)
    {
        gpu = fixture;
    }

    private static readonly (double R, double G, double B, SKColor Color)[] Colors =
    {
        (1, 0, 0, TestPixels.Red),
        (0, 1, 0, TestPixels.Green),
        (0, 0, 1, TestPixels.Blue),
        (1, 1, 0, TestPixels.Yellow),
    };

    private static void Render(WebGpuSkiaTarget target, int frame)
    {
        CommandEncoder encoder = target.BeginWebGpuPass();
        (double r, double g, double b, SKColor _) = Colors[frame % Colors.Length];
        WebGpuWork.Clear(encoder, target.TextureView, r, g, b);
        target.EndWebGpuPass();
    }

    private static SKColor ExpectedColor(int frame) => Colors[frame % Colors.Length].Color;

    private static void AssertUniform(byte[] pixels, int rowBytes, SKColor expected)
    {
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int offset = (y * rowBytes) + (x * 4);
                new SKColor(pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]).Should().Be(expected);
            }
        }
    }

    [Fact]
    public void Double_buffered_frames_read_back_in_order_while_the_next_frame_renders()
    {
        //Arrange
        gpu.RequireDevice();
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.CreateHeadless(gpu.Device, Width, Height);
        byte[] pixels = new byte[TightRow * Height];
        const int Frames = 8;
        WebGpuSkiaReadback previous = default;

        //Act + Assert
        for (int frame = 0; frame < Frames; frame++)
        {
            Render(target, frame);
            WebGpuSkiaReadback current = target.BeginReadPixels();
            current.IsValid.Should().BeTrue(target.LastError);
            current.Sequence.Should().Be(frame + 1);
            current.Slot.Should().Be(frame % 2);
            if (frame > 0)
            {
                target.PendingReadbackCount.Should().Be(2);
                target.EndReadPixels(previous, pixels).Should().BeTrue(target.LastError);
                AssertUniform(pixels, TightRow, ExpectedColor(frame - 1));
            }

            previous = current;
        }

        target.EndReadPixels(previous, pixels).Should().BeTrue(target.LastError);
        AssertUniform(pixels, TightRow, ExpectedColor(Frames - 1));
        target.PendingReadbackCount.Should().Be(0);
        target.ReadbackCount.Should().Be(Frames);
    }

    [Fact]
    public void N_slots_hold_N_frames_in_flight_and_one_more_begin_throws()
    {
        //Arrange
        gpu.RequireDevice();
        const int Slots = 3;
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.CreateHeadless(gpu.Device, Width, Height, new WebGpuSkiaTargetOptions { ReadbackSlots = Slots });
        WebGpuSkiaReadback[] pending = new WebGpuSkiaReadback[Slots];
        byte[] pixels = new byte[TightRow * Height];

        //Act
        for (int frame = 0; frame < Slots; frame++)
        {
            Render(target, frame);
            pending[frame] = target.BeginReadPixels();
        }

        Action oneMore = () => target.BeginReadPixels();

        //Assert
        oneMore.Should().Throw<InvalidOperationException>().WithMessage("*3 read-back slots are pending*");
        target.PendingReadbackCount.Should().Be(Slots);
        for (int frame = 0; frame < Slots; frame++)
        {
            target.EndReadPixels(pending[frame], pixels).Should().BeTrue(target.LastError);
            AssertUniform(pixels, TightRow, ExpectedColor(frame));
        }

        Render(target, 3);
        WebGpuSkiaReadback reused = target.BeginReadPixels();
        reused.Slot.Should().Be(0);
        target.EndReadPixels(reused, pixels).Should().BeTrue(target.LastError);
        AssertUniform(pixels, TightRow, ExpectedColor(3));
    }

    [Fact]
    public void A_readback_becomes_ready_without_blocking_and_ends_with_a_padded_destination_stride()
    {
        //Arrange
        gpu.RequireDevice();
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.CreateHeadless(gpu.Device, Width, Height);
        Render(target, 2);
        const int Stride = TightRow + 20;
        byte[] pixels = new byte[Stride * Height];
        Array.Fill(pixels, (byte)0xAB);

        //Act
        WebGpuSkiaReadback readback = target.BeginReadPixels();
        Stopwatch clock = Stopwatch.StartNew();
        bool ready = false;
        while (!ready && clock.Elapsed < TimeSpan.FromSeconds(10)) ready = target.IsReadbackReady(readback);
        bool ended = target.EndReadPixels(readback, pixels, Stride, TimeSpan.FromSeconds(10));

        //Assert
        ready.Should().BeTrue();
        ended.Should().BeTrue(target.LastError);
        AssertUniform(pixels, Stride, ExpectedColor(2));
        for (int y = 0; y < Height - 1; y++)
        {
            for (int gap = TightRow; gap < Stride; gap++) pixels[(y * Stride) + gap].Should().Be((byte)0xAB);
        }
    }

    [Fact]
    public void Ending_twice_a_default_value_or_another_targets_readback_is_refused()
    {
        //Arrange
        gpu.RequireDevice();
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.CreateHeadless(gpu.Device, Width, Height);
        using WebGpuSkiaTarget other = WebGpuSkiaTarget.CreateHeadless(gpu.Device, Width, Height);
        Render(target, 0);
        byte[] pixels = new byte[TightRow * Height];
        WebGpuSkiaReadback readback = target.BeginReadPixels();
        WebGpuSkiaReadback foreign = other.BeginReadPixels();
        target.EndReadPixels(readback, pixels).Should().BeTrue(target.LastError);

        //Act
        Action again = () => target.EndReadPixels(readback, pixels);
        Action invalid = () => target.EndReadPixels(default, pixels);
        Action wrongTarget = () => target.EndReadPixels(foreign, pixels);
        Action tooSmall = () => other.EndReadPixels(foreign, new byte[10]);

        //Assert
        again.Should().Throw<ArgumentException>().WithMessage("*no longer pending*");
        invalid.Should().Throw<ArgumentException>();
        wrongTarget.Should().Throw<ArgumentException>().WithMessage("*another target*");
        tooSmall.Should().Throw<ArgumentException>();
        other.PendingReadbackCount.Should().Be(1);
        default(WebGpuSkiaReadback).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Cancel_frees_the_slot_and_dispose_with_readbacks_in_flight_is_safe()
    {
        //Arrange
        gpu.RequireDevice();
        byte[] pixels = new byte[TightRow * Height];
        WebGpuSkiaTarget target = WebGpuSkiaTarget.CreateHeadless(gpu.Device, Width, Height, new WebGpuSkiaTargetOptions { ReadbackSlots = 1 });
        Render(target, 0);
        WebGpuSkiaReadback cancelled = target.BeginReadPixels();
        target.CancelReadPixels(cancelled);
        target.CancelReadPixels(cancelled);
        Render(target, 1);
        WebGpuSkiaReadback kept = target.BeginReadPixels();
        bool keptEnded = target.EndReadPixels(kept, pixels);
        SKColor keptColor = TestPixels.At(pixels, Width, 3, 3);
        Render(target, 2);
        target.BeginReadPixels();

        //Act - dispose with one read-back mapped-or-pending, then a second target with two in flight, never polled.
        target.Dispose();
        WebGpuSkiaTarget second = WebGpuSkiaTarget.CreateHeadless(gpu.Device, Width, Height);
        Render(second, 0);
        second.BeginReadPixels();
        Render(second, 1);
        second.BeginReadPixels();
        second.Dispose();
        using WebGpuSkiaTarget after = WebGpuSkiaTarget.CreateHeadless(gpu.Device, Width, Height);
        Render(after, 3);
        bool read = after.ReadPixels(pixels);

        //Assert
        keptEnded.Should().BeTrue(target.LastError);
        keptColor.Should().Be(ExpectedColor(1));
        target.ReadbackCount.Should().Be(1);
        read.Should().BeTrue(after.LastError);
        AssertUniform(pixels, TightRow, ExpectedColor(3));
        gpu.Device.TakeUncapturedErrors().Should().BeEmpty();
    }

    [Fact]
    public void Zero_copy_readback_includes_what_skia_drew()
    {
        //Arrange
        gpu.RequireGrContext();
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu.Device, gpu.GrContext, Width, Height);
        using SKPaint blue = new SKPaint { Color = TestPixels.Blue };
        Render(target, 0);
        target.Surface.Canvas.DrawRect(0, 0, 10, Height, blue);
        byte[] pixels = new byte[TightRow * Height];

        //Act
        WebGpuSkiaReadback readback = target.BeginReadPixels();
        bool ended = target.EndReadPixels(readback, pixels);

        //Assert
        target.Tier.Should().Be(WebGpuSkiaTier.ZeroCopy);
        ended.Should().BeTrue(target.LastError);
        TestPixels.At(pixels, Width, 2, 4).Should().Be(TestPixels.Blue);
        TestPixels.At(pixels, Width, 30, 4).Should().Be(TestPixels.Red);
        Render(target, 1);
        TestPixels.ReadWithSkia(target.Surface, Width, Height).Should().NotBeNull();
    }

    [Fact]
    public void An_unavailable_target_issues_no_readback_and_an_invalid_slot_count_is_refused()
    {
        //Arrange
        gpu.RequireDevice();
        using WebGpuSkiaTarget unavailable = WebGpuSkiaTarget.CreateHeadless(gpu.Device, 0, 0);
        using WebGpuSkiaTarget badSlots = WebGpuSkiaTarget.CreateHeadless(gpu.Device, Width, Height, new WebGpuSkiaTargetOptions { ReadbackSlots = 0 });

        //Act
        WebGpuSkiaReadback none = unavailable.BeginReadPixels();
        Action refused = () => badSlots.BeginReadPixels();

        //Assert
        none.IsValid.Should().BeFalse();
        refused.Should().Throw<InvalidOperationException>().WithMessage("*ReadbackSlots*");
    }

    [Fact]
    public void ReadPixels_with_a_row_stride_wider_than_the_width_leaves_the_gaps_untouched()
    {
        //Arrange
        gpu.RequireDevice();
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.CreateHeadless(gpu.Device, Width, Height);
        using WebGpuWork work = new WebGpuWork(gpu.Device, target.Format);
        CommandEncoder encoder = target.BeginWebGpuPass();
        WebGpuWork.Clear(encoder, target.TextureView, 1, 0, 0);
        work.FillRectangle(encoder, target.TextureView, 0, 0, Width, 3, 0, 1, 0);
        target.EndWebGpuPass();
        const int Stride = TightRow + 12;
        byte[] pixels = new byte[(Stride * (Height - 1)) + TightRow];
        Array.Fill(pixels, (byte)0xCD);

        //Act
        bool read = target.ReadPixels(pixels, Stride);
        Action tooShort = () => target.ReadPixels(pixels, TightRow - 4);

        //Assert
        read.Should().BeTrue(target.LastError);
        for (int y = 0; y < Height; y++)
        {
            SKColor expected = y < 3 ? TestPixels.Green : TestPixels.Red;
            int offset = y * Stride;
            new SKColor(pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3]).Should().Be(expected);
            int last = offset + TightRow - 4;
            new SKColor(pixels[last], pixels[last + 1], pixels[last + 2], pixels[last + 3]).Should().Be(expected);
            if (y < Height - 1) pixels[offset + TightRow].Should().Be((byte)0xCD);
        }

        tooShort.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ReadBitmap_reads_into_the_bitmaps_own_stride()
    {
        //Arrange
        gpu.RequireDevice();
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.CreateHeadless(gpu.Device, Width, Height);
        Render(target, 1);

        //Act
        using SKBitmap bitmap = target.ReadBitmap();

        //Assert
        bitmap.Should().NotBeNull(target.LastError);
        bitmap.GetPixel(0, 0).Should().Be(TestPixels.Green);
        bitmap.GetPixel(Width - 1, Height - 1).Should().Be(TestPixels.Green);
    }
}
