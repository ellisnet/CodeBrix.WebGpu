using System;
using CodeBrix.WebGpu.Native;
using SilverAssertions;
using Xunit;

namespace CodeBrix.WebGpu.Graphics.Tests;

/// <summary>Occlusion, timestamp and pipeline-statistics query sets, resolved into buffers on the GPU.</summary>
public class GpuQuerySetTests : IClassFixture<FeatureGpuFixture>
{
    private readonly FeatureGpuFixture gpu;

    public GpuQuerySetTests(FeatureGpuFixture fixture)
    {
        gpu = fixture;
    }

    [Fact]
    public void Occlusion_query_counts_the_samples_a_draw_covers()
    {
        //Arrange
        gpu.RequireDevice();
        GraphicsContext context = gpu.Context;
        using GpuQuerySet queries = context.CreateOcclusionQuerySet(2, "occlusion");
        using GpuBuffer results = context.CreateQueryResolveBuffer(queries);
        using GpuShader shader = context.CreateShader(TestShaders.LeftHalfRed);
        using GpuRenderPipeline pipeline = context.CreateRenderPipeline(shader).WithColorTarget(TextureFormat.RGBA8Unorm).WithDepth(TextureFormat.Depth32Float).Build();
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(16, 16);

        //Act
        using (GraphicsFrame frame = context.BeginFrame())
        {
            RenderPassOptions options = new RenderPassOptions { OcclusionQuerySet = queries };
            using (RenderPass pass = frame.BeginRenderPass(target.View, Color4.Black, 1f, in options))
            {
                pass.SetPipeline(pipeline);
                pass.BeginOcclusionQuery(0);
                pass.Draw(6);
                pass.EndOcclusionQuery();
                pass.BeginOcclusionQuery(1);
                pass.EndOcclusionQuery();
            }

            frame.ResolveQuerySet(queries, 0, 2, results);
            frame.Submit();
        }

        ulong[] counts = results.Read<ulong>();

        //Assert
        queries.Type.Should().Be(QueryType.Occlusion);
        queries.Count.Should().Be(2);
        counts[0].Should().BeGreaterThan(0UL);
        counts[1].Should().Be(0UL);
    }

    [Fact]
    public void Pass_timestamps_resolve_to_increasing_ticks()
    {
        //Arrange
        gpu.Require(FeatureName.TimestampQuery);
        GraphicsContext context = gpu.Context;
        using GpuQuerySet queries = context.CreateTimestampQuerySet(2);
        using GpuBuffer results = context.CreateQueryResolveBuffer(queries);
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(64, 64, withDepth: false);

        //Act
        using (GraphicsFrame frame = context.BeginFrame())
        {
            RenderPassOptions options = new RenderPassOptions { Timestamps = new PassTimestamps(queries, 0, 1), Label = "timed" };
            using (frame.BeginRenderPass(target.View, Color4.Black, null, in options))
            {
            }

            frame.ResolveQuerySet(queries, 0, 2, results);
            frame.Submit();
        }

        ulong[] ticks = results.Read<ulong>();

        //Assert
        queries.Type.Should().Be(QueryType.Timestamp);
        ticks[0].Should().BeGreaterThan(0UL);
        ticks[1].Should().BeGreaterThanOrEqualTo(ticks[0]);
    }

    [Fact]
    public void Encoder_and_pass_timestamps_are_written_where_the_features_allow()
    {
        //Arrange
        gpu.Require(NativeFeature.TimestampQueryInsideEncoders);
        gpu.Require(NativeFeature.TimestampQueryInsidePasses);
        GraphicsContext context = gpu.Context;
        using GpuQuerySet queries = context.CreateTimestampQuerySet(4);
        using GpuBuffer results = context.CreateQueryResolveBuffer(queries);
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(8, 8, withDepth: false);

        //Act
        using (GraphicsFrame frame = context.BeginFrame())
        {
            frame.WriteTimestamp(queries, 0);
            using (RenderPass pass = frame.BeginRenderPass(target.View, Color4.Black))
            {
                pass.WriteTimestamp(queries, 1);
            }

            using (ComputePass pass = frame.BeginComputePass())
            {
                pass.WriteTimestamp(queries, 2);
            }

            frame.WriteTimestamp(queries, 3);
            frame.ResolveQuerySet(queries, 0, 4, results);
            frame.Submit();
        }

        ulong[] ticks = results.Read<ulong>();

        //Assert
        ticks.Should().OnlyContain(tick => tick > 0UL);
        ticks[3].Should().BeGreaterThanOrEqualTo(ticks[0]);
    }

    [Fact]
    public void Pipeline_statistics_count_compute_invocations()
    {
        //Arrange
        gpu.Require(NativeFeature.PipelineStatisticsQuery);
        GraphicsContext context = gpu.Context;
        using GpuQuerySet queries = context.CreatePipelineStatisticsQuerySet(1, "stats", PipelineStatisticName.ComputeShaderInvocations, PipelineStatisticName.ComputeShaderInvocations);
        using GpuBuffer results = context.CreateQueryResolveBuffer(queries);
        using GpuShader shader = context.CreateShader(TestShaders.Doubler);
        using GpuComputePipeline pipeline = context.CreateComputePipeline(shader).Build();
        using GpuBindGroupLayout layout = pipeline.GetBindGroupLayout(0);
        using GpuBuffer data = context.CreateStorageBuffer<uint>(new uint[128]);
        using GpuBindGroup group = context.CreateBindGroup(layout, BindingEntry.ForBuffer(0, data));

        //Act
        using (GraphicsFrame frame = context.BeginFrame())
        {
            using (ComputePass pass = frame.BeginComputePass())
            {
                pass.SetPipeline(pipeline);
                pass.SetBindGroup(0, group);
                pass.BeginPipelineStatisticsQuery(queries, 0);
                pass.Dispatch(2);
                pass.EndPipelineStatisticsQuery();
            }

            frame.ResolveQuerySet(queries, 0, 1, results);
            frame.Submit();
        }

        ulong[] values = results.Read<ulong>();

        //Assert
        queries.IsPipelineStatistics.Should().BeTrue();
        queries.Statistics.Should().Equal(PipelineStatisticName.ComputeShaderInvocations);
        queries.ValuesPerQuery.Should().Be(1);
        values[0].Should().Be(128UL);
    }

    [Fact]
    public void Render_pipeline_statistics_count_vertex_invocations()
    {
        //Arrange
        gpu.Require(NativeFeature.PipelineStatisticsQuery);
        GraphicsContext context = gpu.Context;
        using GpuQuerySet queries = context.CreatePipelineStatisticsQuerySet(1, PipelineStatisticName.VertexShaderInvocations, PipelineStatisticName.FragmentShaderInvocations);
        using GpuBuffer results = context.CreateQueryResolveBuffer(queries);
        using GpuShader shader = context.CreateShader(TestShaders.FullScreenRed);
        using GpuRenderPipeline pipeline = context.CreateRenderPipeline(shader).WithColorTarget(TextureFormat.RGBA8Unorm).Build();
        using HeadlessRenderTarget target = context.CreateHeadlessTarget(8, 8, withDepth: false);

        //Act
        using (GraphicsFrame frame = context.BeginFrame())
        {
            using (RenderPass pass = frame.BeginRenderPass(target.View, Color4.Black))
            {
                pass.SetPipeline(pipeline);
                pass.BeginPipelineStatisticsQuery(queries, 0);
                pass.Draw(3);
                pass.EndPipelineStatisticsQuery();
            }

            frame.ResolveQuerySet(queries, 0, 1, results);
            frame.Submit();
        }

        ulong[] values = results.Read<ulong>();

        //Assert
        queries.ValuesPerQuery.Should().Be(2);
        values.Length.Should().Be(2);
        values[0].Should().BeGreaterThanOrEqualTo(3UL);
        values[1].Should().BeGreaterThanOrEqualTo(64UL);
    }

    [Fact]
    public void Timestamp_sets_need_the_timestamp_feature()
    {
        //Arrange
        using GraphicsContext plain = TryPlainContext();

        //Act
        Action act = () => plain.CreateTimestampQuerySet(2);

        //Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*TimestampQuery*");
    }

    [Fact]
    public void Query_count_must_fit_a_set()
    {
        //Arrange
        gpu.RequireDevice();

        //Act
        Action none = () => gpu.Context.CreateOcclusionQuerySet(0);
        Action tooMany = () => gpu.Context.CreateOcclusionQuerySet(GraphicsContext.MaxQueriesPerSet + 1);

        //Assert
        none.Should().Throw<ArgumentOutOfRangeException>();
        tooMany.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Resolve_checks_the_destination_usage_and_range()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuQuerySet queries = gpu.Context.CreateOcclusionQuerySet(4);
        using GpuBuffer plain = gpu.Context.CreateStorageBuffer(64);
        using GpuBuffer small = gpu.Context.CreateBuffer(16, BufferUsage.QueryResolve);
        using GraphicsFrame frame = gpu.Context.BeginFrame();

        //Act
        Action wrongUsage = () => frame.ResolveQuerySet(queries, 0, 1, plain);
        Action overrun = () => frame.ResolveQuerySet(queries, 0, 4, small);
        Action pastEnd = () => frame.ResolveQuerySet(queries, 3, 2, small);

        //Assert
        wrongUsage.Should().Throw<ArgumentException>().WithMessage("*QueryResolve*");
        overrun.Should().Throw<ArgumentOutOfRangeException>();
        pastEnd.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Wrapped_query_set_reads_type_and_count_from_the_native_set()
    {
        //Arrange
        gpu.RequireDevice();
        using GpuQuerySet original = gpu.Context.CreateOcclusionQuerySet(7);

        //Act
        using GpuQuerySet wrapped = gpu.Context.WrapQuerySet(original.Handle, "wrapped");

        //Assert
        wrapped.IsWrapped.Should().BeTrue();
        wrapped.Type.Should().Be(QueryType.Occlusion);
        wrapped.Count.Should().Be(7);
        wrapped.ResolvedSize.Should().Be(56UL);
    }

    private GraphicsContext TryPlainContext()
    {
        gpu.RequireDevice();
        Assert.SkipUnless(GraphicsContext.TryCreate(null, out GraphicsContext plain, out string reason), "No plain device: " + reason);
        return plain;
    }
}
