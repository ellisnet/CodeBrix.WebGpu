using System;
using System.Numerics;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>Render pass commands: indirect and multi-draws, blend constant, stencil reference, immediates, debug groups, wrapped passes.</summary>
public class RenderPassTests : IClassFixture<FeatureGpuFixture>
{
    private const string VertexColorQuad = @"
@vertex fn vs_main(@location(0) p: vec2<f32>) -> @builtin(position) vec4<f32> { return vec4<f32>(p, 0.0, 1.0); }
@fragment fn fs_main() -> @location(0) vec4<f32> { return vec4<f32>(0.0, 1.0, 0.0, 1.0); }";

    private readonly FeatureGpuFixture gpu;

    public RenderPassTests(FeatureGpuFixture fixture)
    {
        gpu = fixture;
    }

    [Fact]
    public void DrawIndirect_reads_its_arguments_from_the_buffer()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        using GpuShader shader = context.CreateShader(TestShaders.LeftHalfRed);
        using GpuRenderPipeline pipeline = context.CreateRenderPipeline(shader).WithColorTarget(TextureFormat.RGBA8Unorm).Build();
        using GpuBuffer arguments = context.CreateIndirectBuffer<DrawIndirectArgs>(new[] { new DrawIndirectArgs(0), new DrawIndirectArgs(6) });
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(8, 8, withDepth: false);

        //Act
        Render(target, pass =>
        {
            pass.SetPipeline(pipeline);
            pass.DrawIndirect(arguments, DrawIndirectArgs.Size);
        });
        byte[] rgba = target.ReadRgba();

        //Assert
        TestPixels.Near(TestPixels.At(rgba, 8, 1, 4), 255, 0, 0).Should().BeTrue();
        TestPixels.Near(TestPixels.At(rgba, 8, 6, 4), 0, 0, 0).Should().BeTrue();
    }

    [Fact]
    public void DrawIndexedIndirect_and_MultiDrawIndexedIndirect_draw_from_index_buffers()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        using GpuShader shader = context.CreateShader(VertexColorQuad);
        using GpuRenderPipeline pipeline = context.CreateRenderPipeline(shader).WithVertexBuffer(VertexLayout.Describe().Add(VertexFormat.Float32x2).Build()).WithColorTarget(TextureFormat.RGBA8Unorm).Build();
        using GpuBuffer vertices = context.CreateVertexBuffer<float>(new float[] { -1, -1, 0, -1, 0, 1, -1, 1, 0, -1, 1, -1, 1, 1, 0, 1 });
        using GpuBuffer indices = context.CreateIndexBuffer(new uint[] { 0, 1, 2, 0, 2, 3 });
        using GpuBuffer arguments = context.CreateIndirectBuffer<DrawIndexedIndirectArgs>(new[]
        {
            new DrawIndexedIndirectArgs(6),
            new DrawIndexedIndirectArgs(6, 1, 0, 4),
        });
        using HeadlessRenderTarget left = context.CreateHeadlessTarget(8, 8, withDepth: false);
        using HeadlessRenderTarget both = context.CreateHeadlessTarget(8, 8, withDepth: false);

        //Act
        Render(left, pass =>
        {
            pass.SetPipeline(pipeline);
            pass.SetVertexBuffer(0, vertices);
            pass.SetIndexBuffer(indices, IndexFormat.Uint32);
            pass.DrawIndexedIndirect(arguments);
        });
        Render(both, pass =>
        {
            pass.SetPipeline(pipeline);
            pass.SetVertexBuffer(0, vertices);
            pass.SetIndexBuffer(indices, IndexFormat.Uint32);
            pass.MultiDrawIndexedIndirect(arguments, 0, 2);
        });
        byte[] leftPixels = left.ReadRgba();
        byte[] bothPixels = both.ReadRgba();

        //Assert
        TestPixels.Near(TestPixels.At(leftPixels, 8, 1, 4), 0, 255, 0).Should().BeTrue();
        TestPixels.Near(TestPixels.At(leftPixels, 8, 6, 4), 0, 0, 0).Should().BeTrue();
        TestPixels.Near(TestPixels.At(bothPixels, 8, 6, 4), 0, 255, 0).Should().BeTrue();
    }

    [Fact]
    public void MultiDrawIndirect_issues_every_record()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        using GpuShader shader = context.CreateShader(VertexColorQuad);
        using GpuRenderPipeline pipeline = context.CreateRenderPipeline(shader).WithVertexBuffer(VertexLayout.Describe().Add(VertexFormat.Float32x2).Build()).WithColorTarget(TextureFormat.RGBA8Unorm).Build();
        using GpuBuffer vertices = context.CreateVertexBuffer<float>(new float[] { -1, -1, 0, -1, 0, 1, 0, -1, 1, -1, 1, 1 });
        using GpuBuffer arguments = context.CreateIndirectBuffer<DrawIndirectArgs>(new[] { new DrawIndirectArgs(3), new DrawIndirectArgs(3, 1, 3) });
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(8, 8, withDepth: false);

        //Act
        Render(target, pass =>
        {
            pass.SetPipeline(pipeline);
            pass.SetVertexBuffer(0, vertices);
            pass.MultiDrawIndirect(arguments, 0, 2);
        });
        byte[] rgba = target.ReadRgba();

        //Assert
        TestPixels.Near(TestPixels.At(rgba, 8, 3, 6), 0, 255, 0).Should().BeTrue();
        TestPixels.Near(TestPixels.At(rgba, 8, 7, 6), 0, 255, 0).Should().BeTrue();
    }

    [Fact]
    public void MultiDrawIndirectCount_reads_the_count_on_the_gpu()
    {
        //Arrange
        gpu.Require(NativeFeature.MultiDrawIndirectCount);
        GraphicsContext context = gpu.Context;
        using GpuShader shader = context.CreateShader(VertexColorQuad);
        using GpuRenderPipeline pipeline = context.CreateRenderPipeline(shader).WithVertexBuffer(VertexLayout.Describe().Add(VertexFormat.Float32x2).Build()).WithColorTarget(TextureFormat.RGBA8Unorm).Build();
        using GpuBuffer vertices = context.CreateVertexBuffer<float>(new float[] { -1, -1, 0, -1, 0, 1, 0, -1, 1, -1, 1, 1 });
        using GpuBuffer indices = context.CreateIndexBuffer(new uint[] { 0, 1, 2, 3, 4, 5 });
        using GpuBuffer arguments = context.CreateIndirectBuffer<DrawIndirectArgs>(new[] { new DrawIndirectArgs(3), new DrawIndirectArgs(3, 1, 3) });
        using GpuBuffer indexed = context.CreateIndirectBuffer<DrawIndexedIndirectArgs>(new[] { new DrawIndexedIndirectArgs(3), new DrawIndexedIndirectArgs(3, 1, 3) });
        using GpuBuffer count = context.CreateIndirectBuffer<uint>(new uint[] { 1 });
        using HeadlessRenderTarget plain = context.CreateHeadlessTarget(8, 8, withDepth: false);
        using HeadlessRenderTarget withIndices = context.CreateHeadlessTarget(8, 8, withDepth: false);

        //Act
        Render(plain, pass =>
        {
            pass.SetPipeline(pipeline);
            pass.SetVertexBuffer(0, vertices);
            pass.MultiDrawIndirectCount(arguments, 0, count, 0, 2);
        });
        Render(withIndices, pass =>
        {
            pass.SetPipeline(pipeline);
            pass.SetVertexBuffer(0, vertices);
            pass.SetIndexBuffer(indices, IndexFormat.Uint32);
            pass.MultiDrawIndexedIndirectCount(indexed, 0, count, 0, 2);
        });
        byte[] plainPixels = plain.ReadRgba();
        byte[] indexedPixels = withIndices.ReadRgba();

        //Assert
        TestPixels.Near(TestPixels.At(plainPixels, 8, 3, 6), 0, 255, 0).Should().BeTrue();
        TestPixels.Near(TestPixels.At(plainPixels, 8, 7, 6), 0, 0, 0).Should().BeTrue();
        TestPixels.Near(TestPixels.At(indexedPixels, 8, 3, 6), 0, 255, 0).Should().BeTrue();
        TestPixels.Near(TestPixels.At(indexedPixels, 8, 7, 6), 0, 0, 0).Should().BeTrue();
    }

    [Fact]
    public void Indirect_commands_check_usage_alignment_and_range()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuBuffer plain = gpu.Context.CreateStorageBuffer(64);
        using GpuBuffer arguments = gpu.Context.CreateIndirectBuffer(32);
        using HeadlessRenderTarget target = gpu.Context.CreateHeadlessTarget(4, 4, withDepth: false);
        using GraphicsFrame frame = gpu.Context.BeginFrame();
        using RenderPass pass = frame.BeginRenderPass(target.View, Color4.Black);

        //Act
        Action wrongUsage = () => pass.DrawIndirect(plain);
        Action misaligned = () => pass.DrawIndirect(arguments, 2);
        Action overrun = () => pass.MultiDrawIndirect(arguments, 0, 3);

        //Assert
        wrongUsage.Should().Throw<ArgumentException>().WithMessage("*Indirect*");
        misaligned.Should().Throw<ArgumentOutOfRangeException>();
        overrun.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Blend_constant_drives_a_constant_blend_factor()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        BlendComponent constant = new BlendComponent { Operation = BlendOperation.Add, SrcFactor = BlendFactor.Constant, DstFactor = BlendFactor.Zero };
        using GpuShader shader = context.CreateShader(@"
@vertex fn vs_main(@builtin(vertex_index) i: u32) -> @builtin(position) vec4<f32> {
    var p = array<vec2<f32>, 3>(vec2<f32>(-1.0, -1.0), vec2<f32>(3.0, -1.0), vec2<f32>(-1.0, 3.0));
    return vec4<f32>(p[i], 0.0, 1.0);
}
@fragment fn fs_main() -> @location(0) vec4<f32> { return vec4<f32>(1.0, 1.0, 1.0, 1.0); }");
        using GpuRenderPipeline pipeline = context.CreateRenderPipeline(shader).WithColorTarget(TextureFormat.RGBA8Unorm, new BlendState { Color = constant, Alpha = constant }).Build();
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(4, 4, withDepth: false);

        //Act
        Render(target, pass =>
        {
            pass.SetPipeline(pipeline);
            pass.SetBlendConstant(new Color4(0, 1, 0, 1));
            pass.Draw(3);
        });

        //Assert
        TestPixels.Near(TestPixels.At(target.ReadRgba(), 4, 2, 2), 0, 255, 0).Should().BeTrue();
    }

    [Fact]
    public void Stencil_reference_selects_where_the_second_draw_lands()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        StencilFaceState write = new StencilFaceState { Compare = CompareFunction.Always, FailOp = StencilOperation.Keep, DepthFailOp = StencilOperation.Keep, PassOp = StencilOperation.Replace };
        StencilFaceState test = new StencilFaceState { Compare = CompareFunction.Equal, FailOp = StencilOperation.Keep, DepthFailOp = StencilOperation.Keep, PassOp = StencilOperation.Keep };
        using GpuShader mark = context.CreateShader(TestShaders.LeftHalfRed);
        using GpuShader fill = context.CreateShader(@"
@vertex fn vs_main(@builtin(vertex_index) i: u32) -> @builtin(position) vec4<f32> {
    var p = array<vec2<f32>, 3>(vec2<f32>(-1.0, -1.0), vec2<f32>(3.0, -1.0), vec2<f32>(-1.0, 3.0));
    return vec4<f32>(p[i], 0.0, 1.0);
}
@fragment fn fs_main() -> @location(0) vec4<f32> { return vec4<f32>(0.0, 0.0, 1.0, 1.0); }");
        using GpuRenderPipeline marker = context.CreateRenderPipeline(mark).WithColorTarget(TextureFormat.RGBA8Unorm).WithDepth(TextureFormat.Depth24PlusStencil8, DepthPreset.None).WithStencil(write, write).Build();
        using GpuRenderPipeline filler = context.CreateRenderPipeline(fill).WithColorTarget(TextureFormat.RGBA8Unorm).WithDepth(TextureFormat.Depth24PlusStencil8, DepthPreset.None).WithStencil(test, test).Build();
        using GpuTexture color = context.CreateRenderTexture(8, 8);
        using GpuTexture depth = context.CreateDepthTexture(8, 8, TextureFormat.Depth24PlusStencil8);
        RenderTargetView view = color.AsRenderTarget().WithDepth(depth);

        //Act
        using (GraphicsFrame frame = context.BeginFrame())
        {
            RenderPassOptions options = new RenderPassOptions { StencilClearValue = 0 };
            using (RenderPass pass = frame.BeginRenderPass(view, Color4.Black, 1f, in options))
            {
                pass.SetPipeline(marker);
                pass.SetStencilReference(7);
                pass.Draw(6);
                pass.SetPipeline(filler);
                pass.SetStencilReference(7);
                pass.Draw(3);
            }

            frame.Submit();
        }

        byte[] rgba = color.ReadRgba();

        //Assert
        TestPixels.Near(TestPixels.At(rgba, 8, 1, 4), 0, 0, 255).Should().BeTrue();
        TestPixels.Near(TestPixels.At(rgba, 8, 6, 4), 0, 0, 0).Should().BeTrue();
    }

    [Fact]
    public void Immediates_set_the_colour_of_each_draw()
    {
        //Arrange
        gpu.Require(NativeFeature.Immediates);
        GraphicsContext context = gpu.Context;
        Assert.SkipUnless(context.Limits.MaxImmediateSize >= 16, "The device has no room for 16 bytes of immediates.");
        using GpuShader shader = context.CreateShader(TestShaders.FullScreenImmediateColor);
        using GpuRenderPipeline pipeline = context.CreateRenderPipeline(shader).WithColorTarget(TextureFormat.RGBA8Unorm).WithImmediateSize(16).Build();
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(4, 4, withDepth: false);

        //Act
        Render(target, pass =>
        {
            pass.SetPipeline(pipeline);
            pass.SetImmediates(new Vector4(0, 1, 1, 1));
            pass.Draw(3);
        });

        //Assert
        pipeline.ImmediateSize.Should().Be(16u);
        TestPixels.Near(TestPixels.At(target.ReadRgba(), 4, 2, 2), 0, 255, 255).Should().BeTrue();
    }

    [Fact]
    public void Immediates_must_be_four_byte_aligned()
    {
        //Arrange
        gpu.RequireDevice();
        using HeadlessRenderTarget target = gpu.Context.CreateHeadlessTarget(4, 4, withDepth: false);
        using GraphicsFrame frame = gpu.Context.BeginFrame();
        using RenderPass pass = frame.BeginRenderPass(target.View, Color4.Black);

        //Act
        Action oddSize = () => pass.SetImmediates(new byte[3]);
        Action oddOffset = () => pass.SetImmediates(new byte[4], 2);

        //Assert
        oddSize.Should().Throw<ArgumentOutOfRangeException>();
        oddOffset.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Debug_groups_and_markers_record_in_balanced_pairs()
    {
        //Arrange
        gpu.RequireDevice();
        using HeadlessRenderTarget target = gpu.Context.CreateHeadlessTarget(4, 4, withDepth: false);

        //Act
        using (GraphicsFrame frame = gpu.Context.BeginFrame())
        {
            using (frame.DebugGroup("frame"))
            {
                frame.InsertDebugMarker("before the pass");
                using (RenderPass pass = frame.BeginRenderPass(target.View, new Color4(0, 1, 0, 1)))
                {
                    using (pass.DebugGroup("pass " + new string('x', 400)))
                    {
                        pass.InsertDebugMarker("inside");
                    }
                }
            }

            frame.Submit();
        }

        //Assert
        TestPixels.Near(TestPixels.At(target.ReadRgba(), 4, 0, 0), 0, 255, 0, 1).Should().BeTrue();
    }

    [Fact]
    public void Wrapped_pass_records_into_a_pass_someone_else_began()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        using GpuShader shader = context.CreateShader(TestShaders.FullScreenRed);
        using GpuRenderPipeline pipeline = context.CreateRenderPipeline(shader).WithColorTarget(TextureFormat.RGBA8Unorm).Build();
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(4, 4, withDepth: false);

        //Act
        using (GraphicsFrame frame = context.BeginFrame())
        {
            RenderPassEncoder raw = BeginRawPass(frame.Encoder, target.View.ColorView);
            using (RenderPass pass = frame.WrapRenderPass(raw, target.View))
            {
                pass.IsWrapped.Should().BeTrue();
                pass.SetPipeline(pipeline);
                pass.Draw(3);
            }

            Native.WebGpu.RenderPassEncoderEnd(raw);
            Native.WebGpu.RenderPassEncoderRelease(raw);
            frame.Submit();
        }

        //Assert
        TestPixels.Near(TestPixels.At(target.ReadRgba(), 4, 2, 2), 255, 0, 0).Should().BeTrue();
    }

    [Fact]
    public void Invalid_frame_throws_instead_of_reaching_the_queue()
    {
        //Arrange
        gpu.RequireDevice();
        using HeadlessRenderTarget target = gpu.Context.CreateHeadlessTarget(4, 4, withDepth: false);
        using GraphicsFrame frame = gpu.Context.BeginFrame();
        using (RenderPass pass = frame.BeginRenderPass(target.View, Color4.Black))
        {
            pass.Draw(3);
        }

        //Act
        Action act = () => frame.Submit();

        //Assert
        act.Should().Throw<GraphicsException>().WithMessage("*validation*");
        frame.IsSubmitted.Should().BeTrue();
        frame.SubmissionIndex.Should().Be(0UL);
    }

    private static unsafe RenderPassEncoder BeginRawPass(CommandEncoder encoder, TextureView view)
    {
        RenderPassColorAttachment color = new RenderPassColorAttachment
        {
            View = view,
            DepthSlice = Constants.DepthSliceUndefined,
            LoadOp = LoadOp.Clear,
            StoreOp = StoreOp.Store,
            ClearValue = new Color { A = 1 },
        };
        RenderPassDescriptor descriptor = new RenderPassDescriptor { ColorAttachmentCount = 1, ColorAttachments = &color };
        return Native.WebGpu.CommandEncoderBeginRenderPass(encoder, &descriptor);
    }

    private void Render(HeadlessRenderTarget target, Action<RenderPass> draw)
    {
        using GraphicsFrame frame = gpu.Context.BeginFrame();
        using (RenderPass pass = frame.BeginRenderPass(target.View, Color4.Black))
        {
            draw(pass);
        }

        frame.Submit();
    }
}
