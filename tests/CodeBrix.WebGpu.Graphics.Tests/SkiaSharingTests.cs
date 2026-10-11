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
}
