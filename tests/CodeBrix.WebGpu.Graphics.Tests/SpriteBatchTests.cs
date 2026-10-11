using System;
using System.Drawing;
using System.Numerics;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>The 2D batch rendered to a headless target and checked pixel by pixel.</summary>
public class SpriteBatchTests : IClassFixture<GraphicsGpuFixture>
{
    private const int Width = 64;
    private const int Height = 48;
    private readonly GraphicsGpuFixture gpu;

    public SpriteBatchTests(GraphicsGpuFixture fixture)
    {
        gpu = fixture;
    }

    private byte[] Render(Action<SpriteBatch> draw, Action<SpriteBatch> configure = null, Color4? clear = null)
    {
        GraphicsContext context = gpu.Context;
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(Width, Height);
        using SpriteBatch batch = new SpriteBatch(context);
        configure?.Invoke(batch);
        using (GraphicsFrame frame = context.BeginFrame())
        {
            using (RenderPass pass = frame.BeginRenderPass(target.View, clear ?? Color4.Blue))
            {
                batch.Begin(pass);
                draw(batch);
                batch.End();
                LastDrawCalls = batch.LastDrawCallCount;
                LastQuads = batch.LastQuadCount;
            }

            frame.Submit();
        }

        context.TakeErrors().Should().BeEmpty();
        return target.ReadRgba();
    }

    private int LastDrawCalls { get; set; }

    private int LastQuads { get; set; }

    private GpuTexture Atlas()
    {
        byte[] rgba = new byte[4 * 2 * 4];
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                int index = ((y * 4) + x) * 4;
                rgba[index] = (byte)(x < 2 ? 255 : 0);
                rgba[index + 1] = 0;
                rgba[index + 2] = 0;
                rgba[index + 3] = 255;
                if (x >= 2) rgba[index + 1] = 255;
            }
        }

        return gpu.Context.CreateTextureFromRgba(4, 2, rgba);
    }

    [Fact]
    public void FillRectangle_covers_exactly_its_pixels()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        byte[] rgba = Render(batch => batch.FillRectangle(new RectangleF(10, 10, 20, 10), Color4.Red));

        //Assert
        TestPixels.At(rgba, Width, 10, 10).Should().Be(((byte)255, (byte)0, (byte)0, (byte)255));
        TestPixels.At(rgba, Width, 29, 19).Should().Be(((byte)255, (byte)0, (byte)0, (byte)255));
        TestPixels.At(rgba, Width, 30, 15).Should().Be(((byte)0, (byte)0, (byte)255, (byte)255));
        TestPixels.At(rgba, Width, 15, 20).Should().Be(((byte)0, (byte)0, (byte)255, (byte)255));
        LastDrawCalls.Should().Be(1);
    }

    [Fact]
    public void Atlas_region_draws_the_chosen_cell()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuTexture atlas = Atlas();

        //Act
        byte[] rgba = Render(
            batch =>
            {
                batch.Draw(atlas, new RectangleF(0, 0, 32, 48), new RectangleF(0, 0, 2, 2), Color4.White);
                batch.Draw(atlas, new RectangleF(32, 0, 32, 48), new RectangleF(2, 0, 2, 2), Color4.White);
            },
            batch => batch.UseNearestFiltering = true);

        //Assert
        TestPixels.At(rgba, Width, 16, 24).Should().Be(((byte)255, (byte)0, (byte)0, (byte)255));
        TestPixels.At(rgba, Width, 48, 24).Should().Be(((byte)0, (byte)255, (byte)0, (byte)255));
        LastDrawCalls.Should().Be(1);
    }

    [Fact]
    public void Shapes_do_not_break_a_texture_run_but_a_texture_switch_does()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuTexture atlas = Atlas();
        using GpuTexture other = gpu.Context.CreateTextureFromRgba(1, 1, new byte[] { 255, 255, 255, 255 });

        //Act
        Render(batch =>
        {
            batch.Draw(atlas, new RectangleF(0, 0, 8, 8), Color4.White);
            batch.FillRectangle(new RectangleF(10, 0, 8, 8), Color4.Green);
            batch.DrawLine(new Vector2(0, 30), new Vector2(60, 30), Color4.Yellow, 2);
            batch.Draw(atlas, new RectangleF(20, 0, 8, 8), Color4.White);
        });
        int oneRun = LastDrawCalls;
        Render(batch =>
        {
            batch.Draw(atlas, new RectangleF(0, 0, 8, 8), Color4.White);
            batch.Draw(other, new RectangleF(10, 0, 8, 8), Color4.White);
        });
        int twoRuns = LastDrawCalls;

        //Assert
        oneRun.Should().Be(1);
        twoRuns.Should().Be(2);
    }

    [Fact]
    public void Tint_multiplies_the_texture()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuTexture white = gpu.Context.CreateTextureFromRgba(1, 1, new byte[] { 255, 255, 255, 255 });

        //Act
        byte[] rgba = Render(batch => batch.Draw(white, new RectangleF(0, 0, Width, Height), new Color4(1, 0.5f, 0)));

        //Assert
        TestPixels.Near(TestPixels.At(rgba, Width, 5, 5), 255, 128, 0, 1).Should().BeTrue();
    }

    [Fact]
    public void Lines_circles_and_triangles_land_where_expected()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        byte[] rgba = Render(batch =>
        {
            batch.DrawLine(new Vector2(4, 40.5f), new Vector2(60, 40.5f), Color4.White, 3);
            batch.FillCircle(new Vector2(32, 18), 10, Color4.Red);
            batch.DrawCircle(new Vector2(32, 18), 14, Color4.Green, 2);
            batch.FillTriangle(new Vector2(2, 2), new Vector2(12, 2), new Vector2(2, 12), Color4.Yellow);
        });

        //Assert
        TestPixels.At(rgba, Width, 30, 40).Should().Be(((byte)255, (byte)255, (byte)255, (byte)255));
        TestPixels.At(rgba, Width, 32, 18).Should().Be(((byte)255, (byte)0, (byte)0, (byte)255));
        TestPixels.At(rgba, Width, 46, 18).Should().Be(((byte)0, (byte)255, (byte)0, (byte)255));
        TestPixels.At(rgba, Width, 32 + 12, 18).Should().Be(((byte)0, (byte)0, (byte)255, (byte)255));
        TestPixels.At(rgba, Width, 3, 3).Should().Be(((byte)255, (byte)255, (byte)0, (byte)255));
        TestPixels.At(rgba, Width, 11, 11).Should().Be(((byte)0, (byte)0, (byte)255, (byte)255));
    }

    [Fact]
    public void PushTransform_moves_what_follows_and_PopTransform_restores()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        byte[] rgba = Render(batch =>
        {
            batch.PushTransform(Matrix3x2.CreateTranslation(20, 0));
            batch.FillRectangle(new RectangleF(0, 0, 10, 10), Color4.Red);
            batch.PopTransform();
            batch.FillRectangle(new RectangleF(0, 20, 10, 10), Color4.Green);
        });

        //Assert
        TestPixels.At(rgba, Width, 25, 5).Should().Be(((byte)255, (byte)0, (byte)0, (byte)255));
        TestPixels.At(rgba, Width, 5, 5).Should().Be(((byte)0, (byte)0, (byte)255, (byte)255));
        TestPixels.At(rgba, Width, 5, 25).Should().Be(((byte)0, (byte)255, (byte)0, (byte)255));
    }

    [Fact]
    public void Translucent_colours_blend_with_the_background()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        byte[] rgba = Render(batch => batch.FillRectangle(new RectangleF(0, 0, Width, Height), new Color4(1, 0, 0, 0.5f)));

        //Assert
        TestPixels.Near(TestPixels.At(rgba, Width, 8, 8), 128, 0, 127, 2).Should().BeTrue();
    }

    [Fact]
    public void Two_batches_in_one_frame_keep_their_own_data()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(Width, Height);
        using SpriteBatch batch = new SpriteBatch(context);

        //Act
        using (GraphicsFrame frame = context.BeginFrame())
        {
            using (RenderPass pass = frame.BeginRenderPass(target.View, Color4.Black))
            {
                batch.Begin(pass);
                batch.FillRectangle(new RectangleF(0, 0, 10, 10), Color4.Red);
                batch.End();
                batch.Begin(pass, Matrix3x2.CreateTranslation(30, 0));
                batch.FillRectangle(new RectangleF(0, 0, 10, 10), Color4.Green);
                batch.End();
            }

            using (RenderPass second = frame.BeginRenderPass(target.View, null))
            {
                batch.Begin(second);
                batch.FillRectangle(new RectangleF(0, 30, 10, 10), Color4.Yellow);
                batch.End();
            }

            frame.Submit();
        }

        byte[] rgba = target.ReadRgba();

        //Assert
        TestPixels.At(rgba, Width, 5, 5).Should().Be(((byte)255, (byte)0, (byte)0, (byte)255));
        TestPixels.At(rgba, Width, 35, 5).Should().Be(((byte)0, (byte)255, (byte)0, (byte)255));
        TestPixels.At(rgba, Width, 5, 35).Should().Be(((byte)255, (byte)255, (byte)0, (byte)255));
    }

    [Fact]
    public void Many_quads_grow_the_buffers_within_one_draw()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        byte[] rgba = Render(batch =>
        {
            for (int index = 0; index < 5000; index++) batch.FillRectangle(new RectangleF(index % Width, 0, 1, 1), Color4.Red);
            batch.FillRectangle(new RectangleF(0, 40, 8, 8), Color4.Green);
        });

        //Assert
        LastQuads.Should().Be(5001);
        LastDrawCalls.Should().Be(1);
        TestPixels.At(rgba, Width, 63, 0).Should().Be(((byte)255, (byte)0, (byte)0, (byte)255));
        TestPixels.At(rgba, Width, 4, 44).Should().Be(((byte)0, (byte)255, (byte)0, (byte)255));
    }

    [Fact]
    public void Drawing_without_Begin_throws()
    {
        //Arrange
        gpu.RequireDevice();
        using SpriteBatch batch = new SpriteBatch(gpu.Context);

        //Act
        Action act = () => batch.FillRectangle(new RectangleF(0, 0, 1, 1), Color4.Red);

        //Assert
        act.Should().Throw<InvalidOperationException>();
    }
}
