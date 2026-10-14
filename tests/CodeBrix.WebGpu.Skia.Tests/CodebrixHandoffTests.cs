using System.Runtime.InteropServices;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using SkiaSharp;
using Xunit;

namespace CodeBrix.WebGpu.Skia.Tests;

/// <summary>
/// The native-module hand-offs filled from the Skia types: a <see cref="WebGpuSkiaDevice" /> as the device source
/// and a <see cref="WebGpuSkiaTarget" /> as the texture target, and a "module" that clears the target through the
/// function table only - Skia sees its pixels.
/// </summary>
public unsafe class CodebrixHandoffTests : IClassFixture<SkiaGpuFixture>
{
    private const int Width = 32;
    private const int Height = 16;
    private readonly SkiaGpuFixture gpu;

    public CodebrixHandoffTests(SkiaGpuFixture fixture)
    {
        gpu = fixture;
    }

    [Fact]
    public void a_device_hand_off_carries_the_skia_devices_handles()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        CodebrixDeviceHandoff handoff = CodebrixDeviceHandoff.From(gpu.Device);

        //Assert
        handoff.Size.Should().Be((uint)sizeof(CodebrixDeviceHandoff));
        handoff.Version.Should().Be(CodebrixDeviceHandoff.CurrentVersion);
        handoff.ApiVersion.Should().Be(Constants.ApiVersion);
        handoff.Instance.Should().Be(gpu.Device.Instance);
        handoff.Adapter.Should().Be(gpu.Device.Adapter);
        handoff.Device.Should().Be(gpu.Device.Device);
        handoff.Queue.Should().Be(gpu.Device.Queue);
        handoff.Backend.Should().Be(((IWebGpuDeviceSource)gpu.Device).Backend);
        handoff.Backend.Should().Be(Native.WebGpu.CodebrixDeviceGetBackend(gpu.Device.Device));
        handoff.Flags.Should().Be(CodebrixDeviceHandoff.FlagUncapturedErrorHandler);
        ((nint)handoff.Procs).Should().Be((nint)CodeBrixWebGpuNative.GetProcTable());
    }

    [Fact]
    public void a_frame_hand_off_carries_the_skia_targets_texture_and_pass_encoder()
    {
        //Arrange
        gpu.RequireGrContext();
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu.Device, gpu.GrContext, Width, Height);
        CodebrixFrameHandoff* frame = (CodebrixFrameHandoff*)NativeMemory.AllocZeroed((nuint)sizeof(CodebrixFrameHandoff));

        //Act
        CodebrixFrameHandoff outside = CodebrixFrameHandoff.From(target, CommandEncoder.Null);
        CommandEncoder encoder = CodebrixFrameHandoff.BeginWebGpuPass(target, frame);
        CodebrixFrameHandoff inside = *frame;
        target.EndWebGpuPass();
        NativeMemory.Free(frame);

        //Assert
        outside.CommandEncoder.IsNull.Should().BeTrue();
        inside.CommandEncoder.Should().Be(encoder);
        encoder.IsNull.Should().BeFalse();
        inside.Texture.Should().Be(target.Texture);
        inside.TextureView.Should().Be(target.TextureView);
        inside.Format.Should().Be(target.Format);
        inside.Width.Should().Be((uint)Width);
        inside.Height.Should().Be((uint)Height);
        inside.SampleCount.Should().Be(1u);
        inside.Usage.Should().Be(target.Usage);
        inside.Version.Should().Be(CodebrixFrameHandoff.CurrentVersion);
    }

    [Fact]
    public void a_module_clears_the_skia_target_through_the_function_table()
    {
        //Arrange
        gpu.RequireGrContext();
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(gpu.Device, gpu.GrContext, Width, Height);
        using SKPaint red = new SKPaint { Color = TestPixels.Red };
        CodebrixDeviceHandoff device = CodebrixDeviceHandoff.From(gpu.Device);
        CodebrixFrameHandoff frame = default;

        //Act
        CodebrixFrameHandoff.BeginWebGpuPass(target, &frame);
        ModuleClear(&device, &frame);
        target.EndWebGpuPass();
        target.Surface.Canvas.DrawRect(new SKRect(0, 0, 4, 4), red);
        using SKBitmap bitmap = target.ReadBitmap();

        //Assert
        bitmap.GetPixel(Width - 1, Height - 1).Should().Be(TestPixels.Blue);
        bitmap.GetPixel(1, 1).Should().Be(TestPixels.Red);
        gpu.Device.TakeUncapturedErrors().Should().BeEmpty();
    }

    // What a native module does: only the two hand-offs and the function table.
    private static void ModuleClear(CodebrixDeviceHandoff* device, CodebrixFrameHandoff* frame)
    {
        CodebrixProcTable* t = device->Procs;
        RenderPassColorAttachment attachment = new RenderPassColorAttachment
        {
            View = frame->TextureView,
            DepthSlice = Constants.DepthSliceUndefined,
            LoadOp = LoadOp.Clear,
            StoreOp = StoreOp.Store,
            ClearValue = new Color { R = 0, G = 0, B = 1, A = 1 },
        };
        RenderPassDescriptor descriptor = new RenderPassDescriptor { ColorAttachmentCount = 1, ColorAttachments = &attachment };
        RenderPassEncoder pass = t->CommandEncoderBeginRenderPass(frame->CommandEncoder, &descriptor);
        t->RenderPassEncoderEnd(pass);
        t->RenderPassEncoderRelease(pass);
    }
}
