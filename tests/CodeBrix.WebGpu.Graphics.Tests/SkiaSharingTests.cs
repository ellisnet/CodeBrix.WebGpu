using System;
using System.Drawing;
using System.Numerics;
using CodeBrix.WebGpu.Native;
using CodeBrix.WebGpu.Skia;
using SilverAssertions;
using SkiaSharp;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>
/// One GPU device shared with CodeBrix.WebGpu.Skia: a <see cref="GraphicsContext" /> wraps the device a
/// <see cref="WebGpuSkiaDevice" /> owns, renders into a <see cref="WebGpuSkiaTarget" />'s texture through the target's
/// frame protocol, and the result is read back through Skia (and the other way round).
/// </summary>
public sealed class SkiaSharingTests : IDisposable
{
    private const int Width = 64;
    private const int Height = 48;
    private readonly WebGpuSkiaDevice device;
    private readonly GRContext grContext;
    private readonly GraphicsContext context;

    public SkiaSharingTests()
    {
        device = WebGpuSkiaDevice.Create();
        grContext = device.IsAvailable ? device.CreateGrContext() : null;
        context = device.IsAvailable ? GraphicsContext.Wrap(device.Device, device.Queue, device.Instance, device.Adapter) : null;
    }

    public void Dispose()
    {
        context?.Dispose();
        device.Dispose();
    }

    private void Require()
    {
        Assert.SkipWhen(!device.IsAvailable, "No WebGPU device: " + device.Reason);
        Assert.SkipWhen(grContext == null, "No Skia GRContext on the WebGPU device: " + device.GrContextReason);
    }

    private static RenderTargetView ViewOf(WebGpuSkiaTarget target) => new RenderTargetView(target.TextureView, target.Format, target.Width, target.Height);

    private void DrawRedSquare(WebGpuSkiaTarget target, SpriteBatch batch)
    {
        CommandEncoder encoder = target.BeginWebGpuPass();
        using (GraphicsFrame frame = context.BeginFrame(encoder))
        {
            using (RenderPass pass = frame.BeginRenderPass(ViewOf(target), Color4.Blue))
            {
                batch.Begin(pass);
                batch.FillRectangle(new RectangleF(8, 8, 16, 16), Color4.Red);
                batch.End();
            }

            frame.Submit();
        }

        target.EndWebGpuPass();
    }

    [Fact]
    public void Graphics_renders_into_a_zero_copy_Skia_target_and_Skia_draws_on_top()
    {
        //Arrange
        Require();
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(device, grContext, Width, Height);
        using SpriteBatch batch = new SpriteBatch(context);
        using SKPaint green = new SKPaint { Color = SKColors.Lime };

        //Act
        DrawRedSquare(target, batch);
        target.Surface.Canvas.DrawRect(new SKRect(40, 8, 56, 24), green);
        using SKBitmap bitmap = target.ReadBitmap();

        //Assert
        target.Tier.Should().Be(WebGpuSkiaTier.ZeroCopy);
        bitmap.GetPixel(16, 16).Should().Be(new SKColor(255, 0, 0));
        bitmap.GetPixel(48, 16).Should().Be(new SKColor(0, 255, 0));
        bitmap.GetPixel(32, 40).Should().Be(new SKColor(0, 0, 255));
        device.TakeUncapturedErrors().Should().BeEmpty();
    }

    [Fact]
    public void Graphics_renders_into_a_headless_Skia_target()
    {
        //Arrange
        Require();
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.CreateHeadless(device, Width, Height);
        using SpriteBatch batch = new SpriteBatch(context);

        //Act
        DrawRedSquare(target, batch);
        using SKImage image = target.ReadImage();
        using SKBitmap bitmap = SKBitmap.FromImage(image);

        //Assert
        target.Tier.Should().Be(WebGpuSkiaTier.Headless);
        bitmap.GetPixel(16, 16).Should().Be(new SKColor(255, 0, 0));
        bitmap.GetPixel(40, 40).Should().Be(new SKColor(0, 0, 255));
    }

    [Fact]
    public void Skia_texture_wrapped_by_Graphics_is_drawn_as_a_sprite()
    {
        //Arrange
        Require();
        using WebGpuSkiaTarget skia = WebGpuSkiaTarget.Create(device, grContext, Width, Height);
        using SpriteBatch batch = new SpriteBatch(context);
        using HeadlessRenderTarget output = context.CreateHeadlessTarget(Width, Height, withDepth: false);
        using GpuTexture wrapped = context.WrapTexture(skia.Texture, skia.TextureView, skia.Format, Width, Height, "skia");
        DrawRedSquare(skia, batch);
        using (SKPaint yellow = new SKPaint { Color = SKColors.Yellow })
        {
            skia.Surface.Canvas.DrawRect(new SKRect(40, 30, 60, 46), yellow);
        }

        //Act
        CommandEncoder encoder = skia.BeginWebGpuPass();
        using (GraphicsFrame frame = context.BeginFrame(encoder))
        {
            using (RenderPass pass = frame.BeginRenderPass(output.View, Color4.Black))
            {
                batch.Begin(pass);
                batch.Draw(wrapped, new RectangleF(0, 0, Width, Height), Color4.White);
                batch.End();
            }

            frame.Submit();
        }

        skia.EndWebGpuPass();
        byte[] rgba = output.ReadRgba();

        //Assert
        TestPixels.At(rgba, Width, 16, 16).Should().Be(((byte)255, (byte)0, (byte)0, (byte)255));
        TestPixels.At(rgba, Width, 50, 38).Should().Be(((byte)255, (byte)255, (byte)0, (byte)255));
        TestPixels.At(rgba, Width, 4, 40).Should().Be(((byte)0, (byte)0, (byte)255, (byte)255));
        device.TakeUncapturedErrors().Should().BeEmpty();
    }

    [Fact]
    public void Lit_mesh_renders_into_a_Skia_target_with_a_Graphics_depth_buffer()
    {
        //Arrange
        Require();
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(device, grContext, Width, Height);
        using GpuTexture depth = context.CreateDepthTexture(Width, Height);
        using Mesh cube = context.CreateMesh(MeshShapes.Cube(2));
        using MeshRenderer renderer = new MeshRenderer(context);
        using LitMaterial material = renderer.CreateMaterial(Color4.Red);
        material.Unlit = true;
        Camera camera = new Camera { Position = new Vector3(0, 0, 6) };

        //Act
        CommandEncoder encoder = target.BeginWebGpuPass();
        using (GraphicsFrame frame = context.BeginFrame(encoder))
        {
            using (RenderPass pass = frame.BeginRenderPass(ViewOf(target).WithDepth(depth), Color4.Black))
            {
                renderer.Begin(pass, camera);
                renderer.Draw(cube, material, Matrix4x4.CreateRotationY(0.3f));
                renderer.End();
            }

            frame.Submit();
        }

        target.EndWebGpuPass();
        using SKBitmap bitmap = target.ReadBitmap();

        //Assert
        bitmap.GetPixel(Width / 2, Height / 2).Should().Be(new SKColor(255, 0, 0));
        bitmap.GetPixel(1, 1).Should().Be(new SKColor(0, 0, 0));
    }

    [Fact]
    public void Wrap_from_a_Skia_device_shares_its_handles_and_error_handler()
    {
        //Arrange
        Require();

        //Act
        using GraphicsContext shared = GraphicsContext.Wrap(device);

        //Assert
        shared.IsWrapped.Should().BeTrue();
        shared.Device.Should().Be(device.Device);
        shared.Queue.Should().Be(device.Queue);
        shared.Instance.Should().Be(device.Instance);
        shared.Adapter.Should().Be(device.Adapter);
        shared.Backend.Should().Be(((IWebGpuDeviceSource)device).Backend);
        shared.HasUncapturedErrorHandler.Should().BeTrue();
    }

    [Fact]
    public void Wrap_from_a_disposed_Skia_device_is_refused()
    {
        //Arrange
        Require();
        WebGpuSkiaDevice disposed = WebGpuSkiaDevice.Create();
        disposed.Dispose();

        //Act
        Action act = () => GraphicsContext.Wrap(disposed);

        //Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void One_call_frame_renders_a_lit_mesh_with_depth_into_a_zero_copy_Skia_target_and_Skia_sees_it()
    {
        //Arrange
        Require();
        using GraphicsContext shared = GraphicsContext.Wrap(device);
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(device, grContext, Width, Height);
        using GpuTexture depth = shared.CreateDepthTexture(Width, Height);
        using Mesh cube = shared.CreateMesh(MeshShapes.Cube(2));
        using MeshRenderer renderer = new MeshRenderer(shared);
        using LitMaterial material = renderer.CreateMaterial(Color4.Red);
        using SKPaint green = new SKPaint { Color = SKColors.Lime };
        material.Unlit = true;
        Camera camera = new Camera { Position = new Vector3(0, 0, 6) };

        //Act
        using (GraphicsFrame frame = shared.BeginFrame(target, depth))
        {
            frame.Target.HasDepth.Should().BeTrue();
            target.IsInWebGpuPass.Should().BeTrue();
            using (RenderPass pass = frame.BeginRenderPass(Color4.Blue))
            {
                renderer.Begin(pass, camera);
                renderer.Draw(cube, material, Matrix4x4.CreateRotationY(0.3f));
                renderer.End();
            }

            frame.Submit();
        }

        bool passEnded = !target.IsInWebGpuPass;
        target.Surface.Canvas.DrawRect(new SKRect(0, 0, 8, 8), green);
        using SKBitmap throughWebGpu = target.ReadBitmap();
        target.Surface.Flush();
        using SKBitmap throughSkia = new SKBitmap(new SKImageInfo(Width, Height, target.ColorType, SKAlphaType.Premul));
        bool skiaRead = target.Surface.ReadPixels(throughSkia.Info, throughSkia.GetPixels(), throughSkia.RowBytes, 0, 0);

        //Assert
        target.Tier.Should().Be(WebGpuSkiaTier.ZeroCopy);
        passEnded.Should().BeTrue();
        throughWebGpu.GetPixel(Width / 2, Height / 2).Should().Be(new SKColor(255, 0, 0));
        throughWebGpu.GetPixel(Width - 2, Height - 2).Should().Be(new SKColor(0, 0, 255));
        throughWebGpu.GetPixel(2, 2).Should().Be(new SKColor(0, 255, 0));
        skiaRead.Should().BeTrue();
        throughSkia.GetPixel(Width / 2, Height / 2).Should().Be(new SKColor(255, 0, 0));
        throughSkia.GetPixel(Width - 2, Height - 2).Should().Be(new SKColor(0, 0, 255));
        throughSkia.GetPixel(2, 2).Should().Be(new SKColor(0, 255, 0));
        device.TakeUncapturedErrors().Should().BeEmpty();
        shared.TakeErrors().Should().BeEmpty();
    }

    [Fact]
    public void One_call_frame_renders_into_a_headless_Skia_target()
    {
        //Arrange
        Require();
        using GraphicsContext shared = GraphicsContext.Wrap(device);
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.CreateHeadless(device, Width, Height);
        using SpriteBatch batch = new SpriteBatch(shared);

        //Act
        using (GraphicsFrame frame = shared.BeginFrame(target))
        {
            using (RenderPass pass = frame.BeginRenderPass(Color4.Blue))
            {
                batch.Begin(pass);
                batch.FillRectangle(new RectangleF(8, 8, 16, 16), Color4.Red);
                batch.End();
            }

            frame.Submit();
        }

        using SKBitmap bitmap = target.ReadBitmap();

        //Assert
        target.FrameCount.Should().Be(1);
        bitmap.GetPixel(16, 16).Should().Be(new SKColor(255, 0, 0));
        bitmap.GetPixel(40, 40).Should().Be(new SKColor(0, 0, 255));
    }

    [Fact]
    public void Skia_target_wrapped_from_the_interface_is_sampled_inside_its_own_frame()
    {
        //Arrange
        Require();
        using GraphicsContext shared = GraphicsContext.Wrap(device);
        using WebGpuSkiaTarget skia = WebGpuSkiaTarget.Create(device, grContext, Width, Height);
        using SpriteBatch batch = new SpriteBatch(shared);
        using HeadlessRenderTarget output = shared.CreateHeadlessTarget(Width, Height, withDepth: false);
        using GpuTexture wrapped = shared.WrapTexture(skia, "skia");
        using (SKPaint yellow = new SKPaint { Color = SKColors.Yellow })
        {
            skia.Surface.Canvas.Clear(SKColors.Blue);
            skia.Surface.Canvas.DrawRect(new SKRect(40, 30, 60, 46), yellow);
        }

        //Act
        using (GraphicsFrame frame = shared.BeginFrame(skia))
        {
            using (RenderPass pass = frame.BeginRenderPass(output.View, Color4.Black))
            {
                batch.Begin(pass);
                batch.Draw(wrapped, new RectangleF(0, 0, Width, Height), Color4.White);
                batch.End();
            }

            frame.Submit();
        }

        byte[] rgba = output.ReadRgba();

        //Assert
        wrapped.Width.Should().Be(Width);
        wrapped.Format.Should().Be(skia.Format);
        TestPixels.At(rgba, Width, 50, 38).Should().Be(((byte)255, (byte)255, (byte)0, (byte)255));
        TestPixels.At(rgba, Width, 4, 4).Should().Be(((byte)0, (byte)0, (byte)255, (byte)255));
        device.TakeUncapturedErrors().Should().BeEmpty();
    }

    [Fact]
    public void Disposing_an_unsubmitted_target_frame_ends_the_targets_pass()
    {
        //Arrange
        Require();
        using GraphicsContext shared = GraphicsContext.Wrap(device);
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(device, grContext, Width, Height);

        //Act
        using (GraphicsFrame frame = shared.BeginFrame(target))
        {
            frame.BeginRenderPass(Color4.Red).End();
        }

        bool endedByDispose = !target.IsInWebGpuPass;
        using (GraphicsFrame again = shared.BeginFrame(target))
        {
            again.Submit();
        }

        //Assert
        endedByDispose.Should().BeTrue();
        target.IsInWebGpuPass.Should().BeFalse();
        target.FrameCount.Should().Be(2);
    }

    [Fact]
    public void A_target_frame_with_an_invalid_command_throws_at_Submit_and_the_target_takes_the_next_frame()
    {
        //Arrange
        Require();
        using GraphicsContext shared = GraphicsContext.Wrap(device);
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(device, grContext, Width, Height);
        using SpriteBatch batch = new SpriteBatch(shared);
        using GpuBuffer source = shared.CreateBuffer(16, BufferUsage.CopySrc);
        using GpuBuffer destination = shared.CreateBuffer(16, BufferUsage.CopyDst);
        using SKPaint green = new SKPaint { Color = SKColors.Lime };

        //Act - a copy at an unaligned offset is only rejected when the target finishes the encoder.
        Action submitInvalid = () =>
        {
            using GraphicsFrame frame = shared.BeginFrame(target);
            frame.CopyBuffer(source, 0, destination, 2, 4);
            frame.Submit();
        };
        WebGpuSkiaValidationException error = submitInvalid.Should().Throw<WebGpuSkiaValidationException>().Which;
        bool passClosed = !target.IsInWebGpuPass;
        target.Surface.Canvas.DrawRect(new SKRect(0, 0, 4, 4), green);
        using (GraphicsFrame frame = shared.BeginFrame(target))
        {
            using (RenderPass pass = frame.BeginRenderPass(Color4.Blue))
            {
                batch.Begin(pass);
                batch.FillRectangle(new RectangleF(8, 8, 16, 16), Color4.Red);
                batch.End();
            }

            frame.Submit();
        }

        using SKBitmap bitmap = target.ReadBitmap();

        //Assert
        error.WebGpuMessage.Should().NotBeNullOrEmpty();
        passClosed.Should().BeTrue();
        target.FrameCount.Should().Be(1);
        bitmap.GetPixel(16, 16).Should().Be(new SKColor(255, 0, 0));
        bitmap.GetPixel(40, 40).Should().Be(new SKColor(0, 0, 255));
        device.TakeUncapturedErrors().Should().BeEmpty();
    }

    [Fact]
    public void A_target_frame_refuses_an_open_pass_and_a_mismatched_depth()
    {
        //Arrange
        Require();
        using GraphicsContext shared = GraphicsContext.Wrap(device);
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(device, grContext, Width, Height);
        using GpuTexture smallDepth = shared.CreateDepthTexture(Width / 2, Height / 2);
        target.BeginWebGpuPass();

        //Act
        Action open = () => shared.BeginFrame(target);
        open.Should().Throw<InvalidOperationException>();
        target.EndWebGpuPass();
        Action mismatched = () => shared.BeginFrame(target, smallDepth);
        Action noTarget = () =>
        {
            using GraphicsFrame frame = shared.BeginFrame();
            frame.BeginRenderPass(Color4.Black);
        };

        //Assert
        mismatched.Should().Throw<ArgumentException>();
        target.IsInWebGpuPass.Should().BeFalse();
        noTarget.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void RenderTargetView_From_a_Skia_target_carries_its_view_format_and_size()
    {
        //Arrange
        Require();
        using WebGpuSkiaTarget target = WebGpuSkiaTarget.Create(device, grContext, Width, Height);
        using GpuTexture depth = context.CreateDepthTexture(Width, Height, TextureFormat.Depth24Plus);

        //Act
        RenderTargetView view = RenderTargetView.From(target, depth);

        //Assert
        view.ColorView.Should().Be(target.TextureView);
        view.ColorFormat.Should().Be(target.Format);
        view.Width.Should().Be(Width);
        view.Height.Should().Be(Height);
        view.DepthFormat.Should().Be(TextureFormat.Depth24Plus);
    }
}
