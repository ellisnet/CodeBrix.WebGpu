using System;
using System.Linq;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>Frame commands: buffer/texture copies, clears, in-frame uploads, finish-and-submit, submission waits, transitions.</summary>
public class GraphicsFrameTests : IClassFixture<FeatureGpuFixture>
{
    private readonly FeatureGpuFixture gpu;

    public GraphicsFrameTests(FeatureGpuFixture fixture)
    {
        gpu = fixture;
    }

    [Fact]
    public void Buffer_to_texture_and_back_round_trips_the_texels()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        using GpuTexture texture = context.CreateTexture2D(5, 3, TextureFormat.RGBA8Unorm, TextureUsage.CopyDst | TextureUsage.CopySrc);
        int aligned = TextureCopyLayout.AlignedBytesPerRow(5, TextureFormat.RGBA8Unorm);
        byte[] tight = Enumerable.Range(0, 5 * 3 * 4).Select(value => (byte)value).ToArray();
        byte[] staged = new byte[aligned * 3];
        TextureCopyLayout.PackRows(tight, 20, 3, staged, aligned);
        using GpuBuffer upload = context.CreateBuffer<byte>(staged, BufferUsage.CopySrc);
        using GpuBuffer download = context.CreateBuffer(TextureCopyLayout.BufferSize(5, 3, TextureFormat.RGBA8Unorm), BufferUsage.CopyDst);

        //Act
        using (GraphicsFrame frame = context.BeginFrame())
        {
            frame.CopyBufferToTexture(upload, texture);
            frame.CopyTextureToBuffer(texture, download);
            frame.Submit();
        }

        byte[] read = download.Read<byte>();
        byte[] unpacked = new byte[tight.Length];
        TextureCopyLayout.UnpackRows(read, aligned, 3, unpacked, 20);

        //Assert
        aligned.Should().Be(256);
        unpacked.Should().Equal(tight);
        texture.ReadPixels().Should().Equal(tight);
    }

    [Fact]
    public void Texture_to_texture_copy_moves_a_region()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        using GpuTexture source = context.CreateTexture2D(4, 4, TextureFormat.RGBA8Unorm, TextureUsage.CopyDst | TextureUsage.CopySrc);
        using GpuTexture destination = context.CreateTexture2D(4, 4, TextureFormat.RGBA8Unorm, TextureUsage.CopyDst | TextureUsage.CopySrc);
        source.Write(Enumerable.Repeat((byte)200, 64).ToArray());
        destination.Write(new byte[64]);

        //Act
        using (GraphicsFrame frame = context.BeginFrame())
        {
            frame.CopyTextureToTexture(source, new TextureRegion(0, 0, 2, 2), destination, 0, 2, 2);
            frame.Submit();
        }

        byte[] rgba = destination.ReadRgba();

        //Assert
        TestPixels.At(rgba, 4, 3, 3).Should().Be(((byte)200, (byte)200, (byte)200, (byte)200));
        TestPixels.At(rgba, 4, 1, 1).Should().Be(((byte)0, (byte)0, (byte)0, (byte)0));
    }

    [Fact]
    public void UploadTexture_orders_the_upload_with_the_frame_commands()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        using GpuTexture texture = context.CreateTexture2D(3, 2, TextureFormat.RGBA8Unorm, TextureUsage.CopyDst | TextureUsage.CopySrc);
        using GpuTexture copy = context.CreateTexture2D(3, 2, TextureFormat.RGBA8Unorm, TextureUsage.CopyDst | TextureUsage.CopySrc);
        byte[] pixels = Enumerable.Range(1, 24).Select(value => (byte)value).ToArray();
        int live = context.LiveResourceCount;

        //Act
        using (GraphicsFrame frame = context.BeginFrame())
        {
            frame.UploadTexture(texture, TextureRegion.Whole(texture), pixels);
            frame.CopyTextureToTexture(texture, copy);
            frame.Submit();
        }

        //Assert
        copy.ReadPixels().Should().Equal(pixels);
        context.LiveResourceCount.Should().Be(live);
    }

    [Fact]
    public void ClearBuffer_zeroes_a_range()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuBuffer buffer = gpu.Context.CreateStorageBuffer<uint>(new uint[] { 1, 2, 3, 4 });

        //Act
        using (GraphicsFrame frame = gpu.Context.BeginFrame())
        {
            frame.ClearBuffer(buffer, 4, 8);
            frame.Submit();
        }

        //Assert
        buffer.Read<uint>().Should().Equal(1u, 0u, 0u, 4u);
    }

    [Fact]
    public void ClearTexture_zeroes_every_level()
    {
        //Arrange
        gpu.Require(NativeFeature.ClearTexture);
        using GpuTexture texture = gpu.Context.CreateTextureFromRgba(4, 4, Enumerable.Repeat((byte)255, 64).ToArray(), mipmaps: true);

        //Act
        using (GraphicsFrame frame = gpu.Context.BeginFrame())
        {
            frame.ClearTexture(texture);
            frame.Submit();
        }

        //Assert
        texture.ReadPixels().Should().OnlyContain(value => value == 0);
        byte[] level1 = new byte[16];
        texture.ReadPixels(level1, 1);
        level1.Should().OnlyContain(value => value == 0);
    }

    [Fact]
    public void Finished_frames_submit_together_and_can_be_waited_for()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        using GpuBuffer first = context.CreateStorageBuffer<uint>(new uint[] { 1 });
        using GpuBuffer second = context.CreateStorageBuffer<uint>(new uint[] { 0 });
        using GpuBuffer third = context.CreateStorageBuffer<uint>(new uint[] { 0 });
        GraphicsFrame a = context.BeginFrame();
        a.CopyBuffer(first, 0, second, 0, 4);
        using GpuCommandBuffer commandsA = a.Finish("a");
        GraphicsFrame b = context.BeginFrame();
        b.CopyBuffer(second, 0, third, 0, 4);
        using GpuCommandBuffer commandsB = b.Finish();

        //Act
        ulong index = context.Submit(commandsA, commandsB);
        context.WaitForSubmission(index);
        Action again = () => context.Submit(commandsA);

        //Assert
        index.Should().BeGreaterThan(0UL);
        commandsA.IsSubmitted.Should().BeTrue();
        third.Read<uint>().Should().Equal(1u);
        again.Should().Throw<ArgumentException>().WithMessage("*already been submitted*");
        a.IsSubmitted.Should().BeTrue();
    }

    [Fact]
    public void Submit_reports_a_growing_submission_index()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsFrame first = gpu.Context.BeginFrame();
        GraphicsFrame second = gpu.Context.BeginFrame();

        //Act
        first.Submit();
        second.Submit();
        gpu.Context.WaitForSubmission(second.SubmissionIndex);

        //Assert
        first.SubmissionIndex.Should().BeGreaterThan(0UL);
        second.SubmissionIndex.Should().BeGreaterThan(first.SubmissionIndex);
    }

    [Fact]
    public void Borrowed_frames_cannot_be_finished()
    {
        //Arrange
        gpu.RequireDevice();
        CommandEncoder encoder = CreateEncoder(gpu.Context.Device);
        GraphicsFrame frame = gpu.Context.BeginFrame(encoder);

        //Act
        Action act = () => frame.Finish();
        frame.Submit();
        Native.WebGpu.CommandEncoderRelease(encoder);

        //Assert
        act.Should().Throw<InvalidOperationException>();
        frame.SubmissionIndex.Should().Be(0UL);
    }

    [Fact]
    public void Wrapped_command_buffer_is_submitted_like_a_finished_frame()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        using GpuBuffer from = context.CreateStorageBuffer<uint>(new uint[] { 9 });
        using GpuBuffer to = context.CreateStorageBuffer<uint>(new uint[] { 0 });
        CommandBuffer raw = RecordCopy(context.Device, from.Handle, to.Handle);
        using GpuCommandBuffer wrapped = context.WrapCommandBuffer(raw, "raw copy");
        Native.WebGpu.CommandBufferRelease(raw);

        //Act
        context.Submit(wrapped);

        //Assert
        to.Read<uint>().Should().Equal(9u);
    }

    [Fact]
    public void Transitions_report_whether_the_texture_usage_allows_the_state()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuTexture renderable = gpu.Context.CreateRenderTexture(4, 4);
        using GpuTexture copyOnly = gpu.Context.CreateTexture2D(4, 4, TextureFormat.RGBA8Unorm, TextureUsage.CopyDst);
        using GraphicsFrame frame = gpu.Context.BeginFrame();

        //Act
        bool toTarget = frame.TransitionTexture(renderable, CodebrixTextureState.ColorTarget);
        bool toShader = frame.TransitionTexture(renderable, CodebrixTextureState.ShaderResource);
        bool refused = frame.TransitionTexture(copyOnly, CodebrixTextureState.ColorTarget);
        frame.Submit();

        //Assert
        toTarget.Should().BeTrue();
        toShader.Should().BeTrue();
        refused.Should().BeFalse();
    }

    [Fact]
    public void Copy_arguments_are_checked_before_recording()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuTexture texture = gpu.Context.CreateTexture2D(4, 4, TextureFormat.RGBA8Unorm, TextureUsage.CopyDst);
        using GpuBuffer buffer = gpu.Context.CreateBuffer(4096, BufferUsage.CopySrc);
        using GraphicsFrame frame = gpu.Context.BeginFrame();

        //Act
        Action unaligned = () => frame.CopyBufferToTexture(buffer, 0, 16, texture, TextureRegion.Whole(texture));
        Action outside = () => frame.CopyBufferToTexture(buffer, 0, 256, texture, new TextureRegion(2, 2, 4, 4));
        Action wrongMip = () => frame.CopyBufferToTexture(buffer, 0, 256, texture, new TextureRegion(0, 0, 1, 1, 3));
        Action clearMisaligned = () => frame.ClearBuffer(buffer, 2);

        //Assert
        unaligned.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*multiple of 256*");
        outside.Should().Throw<ArgumentOutOfRangeException>();
        wrongMip.Should().Throw<ArgumentOutOfRangeException>();
        clearMisaligned.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static unsafe CommandEncoder CreateEncoder(Device device) => Native.WebGpu.DeviceCreateCommandEncoder(device, null);

    private static unsafe CommandBuffer RecordCopy(Device device, Native.Buffer from, Native.Buffer to)
    {
        CommandEncoder encoder = Native.WebGpu.DeviceCreateCommandEncoder(device, null);
        Native.WebGpu.CommandEncoderCopyBufferToBuffer(encoder, from, 0, to, 0, 4);
        CommandBuffer commands = Native.WebGpu.CommandEncoderFinish(encoder, null);
        Native.WebGpu.CommandEncoderRelease(encoder);
        return commands;
    }
}
