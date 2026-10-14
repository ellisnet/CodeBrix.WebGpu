using System;
using System.Collections.Generic;
using CodeBrix.WebGpu.Graphics.Interop;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>The query factories of <see cref="GraphicsContext" />.</summary>
public sealed unsafe partial class GraphicsContext
{
    /// <summary>The most queries one set may hold.</summary>
    public const int MaxQueriesPerSet = 4096;

    /// <summary>
    /// Creates an occlusion or timestamp query set. Timestamp sets need the device feature
    /// <see cref="FeatureName.TimestampQuery" />; pipeline-statistics sets have their own factory
    /// (<see cref="CreatePipelineStatisticsQuerySet(int, PipelineStatisticName[])" />).
    /// </summary>
    /// <param name="type">The query type.</param>
    /// <param name="count">The number of queries (1 to <see cref="MaxQueriesPerSet" />).</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The query set.</returns>
    /// <exception cref="InvalidOperationException">The device lacks the feature the type needs.</exception>
    /// <exception cref="GraphicsException">WebGPU rejected the set.</exception>
    public GpuQuerySet CreateQuerySet(QueryType type, int count, string label = null)
    {
        ThrowIfDisposed();
        if (type != QueryType.Occlusion && type != QueryType.Timestamp) throw new ArgumentOutOfRangeException(nameof(type), "Use CreatePipelineStatisticsQuerySet for pipeline statistics.");
        if (type == QueryType.Timestamp && !HasFeature(FeatureName.TimestampQuery)) throw new InvalidOperationException("Timestamp queries need the device feature FeatureName.TimestampQuery (GraphicsContextOptions.RequiredFeatures).");
        return CreateQuerySetCore(type, count, null, label);
    }

    /// <summary>Creates a timestamp query set (see <see cref="CreateQuerySet" />).</summary>
    /// <param name="count">The number of queries.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The query set.</returns>
    public GpuQuerySet CreateTimestampQuerySet(int count, string label = null) => CreateQuerySet(QueryType.Timestamp, count, label);

    /// <summary>Creates an occlusion query set (see <see cref="CreateQuerySet" />); pass it as <see cref="RenderPassOptions.OcclusionQuerySet" />.</summary>
    /// <param name="count">The number of queries.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The query set.</returns>
    public GpuQuerySet CreateOcclusionQuerySet(int count, string label = null) => CreateQuerySet(QueryType.Occlusion, count, label);

    /// <summary>
    /// Creates a pipeline-statistics query set recording the given statistics (needs the native feature
    /// <see cref="NativeFeature.PipelineStatisticsQuery" />). Each query resolves to one 64-bit value per statistic, in
    /// ascending <see cref="PipelineStatisticName" /> order (<see cref="GpuQuerySet.Statistics" />).
    /// </summary>
    /// <param name="count">The number of queries.</param>
    /// <param name="statistics">The statistics (at least one; duplicates are ignored).</param>
    /// <returns>The query set.</returns>
    /// <exception cref="InvalidOperationException">The device lacks the feature.</exception>
    public GpuQuerySet CreatePipelineStatisticsQuerySet(int count, params PipelineStatisticName[] statistics) => CreatePipelineStatisticsQuerySet(count, null, statistics);

    /// <summary>Creates a labelled pipeline-statistics query set (see <see cref="CreatePipelineStatisticsQuerySet(int, PipelineStatisticName[])" />).</summary>
    /// <param name="count">The number of queries.</param>
    /// <param name="label">A label, or null.</param>
    /// <param name="statistics">The statistics (at least one; duplicates are ignored).</param>
    /// <returns>The query set.</returns>
    /// <exception cref="InvalidOperationException">The device lacks the feature.</exception>
    public GpuQuerySet CreatePipelineStatisticsQuerySet(int count, string label, params PipelineStatisticName[] statistics)
    {
        ThrowIfDisposed();
        if (!HasFeature(NativeFeature.PipelineStatisticsQuery)) throw new InvalidOperationException("Pipeline-statistics queries need the native feature NativeFeature.PipelineStatisticsQuery (GraphicsContextOptions.RequiredNativeFeatures).");
        if (statistics == null || statistics.Length == 0) throw new ArgumentException("Name at least one statistic.", nameof(statistics));
        SortedSet<PipelineStatisticName> unique = new SortedSet<PipelineStatisticName>();
        foreach (PipelineStatisticName statistic in statistics)
        {
            if (statistic < PipelineStatisticName.VertexShaderInvocations || statistic > PipelineStatisticName.ComputeShaderInvocations) throw new ArgumentOutOfRangeException(nameof(statistics), "Unknown statistic " + statistic + ".");
            unique.Add(statistic);
        }

        return CreateQuerySetCore(GpuQuerySet.PipelineStatisticsType, count, new List<PipelineStatisticName>(unique), label);
    }

    /// <summary>
    /// Creates a buffer to resolve a query set into: <see cref="BufferUsage.QueryResolve" /> plus copy usages, sized
    /// for every query (<see cref="GpuQuerySet.ResolvedSize" />). Read it with <see cref="GpuBuffer.Read{T}(int)" /> of
    /// <see cref="ulong" /> after the frame that resolved it was submitted.
    /// </summary>
    /// <param name="querySet">The query set.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The buffer.</returns>
    public GpuBuffer CreateQueryResolveBuffer(GpuQuerySet querySet, string label = null)
    {
        ArgumentNullException.ThrowIfNull(querySet);
        return CreateBuffer(querySet.ResolvedSize, BufferUsage.QueryResolve, label);
    }

    /// <summary>
    /// Creates a GPU timer with <paramref name="capacity" /> slots, each measuring one span (a pass, or anything between
    /// two timestamp writes) in milliseconds. Needs <see cref="FeatureName.TimestampQuery" />.
    /// </summary>
    /// <param name="capacity">The number of slots.</param>
    /// <param name="label">A label, or null.</param>
    /// <returns>The timer.</returns>
    public GpuTimer CreateTimer(int capacity = 16, string label = null)
    {
        ThrowIfDisposed();
        return new GpuTimer(this, capacity, label);
    }

    private GpuQuerySet CreateQuerySetCore(QueryType type, int count, List<PipelineStatisticName> statistics, string label)
    {
        if (count < 1 || count > MaxQueriesPerSet) throw new ArgumentOutOfRangeException(nameof(count), "A query set holds 1 to " + MaxQueriesPerSet + " queries.");
        using Utf8Text text = new Utf8Text();
        QuerySetDescriptor descriptor = new QuerySetDescriptor { Label = text.View(label), Type = type, Count = (uint)count };
        QuerySetDescriptorExtras extras = default;
        if (statistics != null)
        {
            PipelineStatisticName* names = (PipelineStatisticName*)text.Allocate(sizeof(PipelineStatisticName) * statistics.Count);
            for (int index = 0; index < statistics.Count; index++) names[index] = statistics[index];
            extras.Chain.SType = QuerySetDescriptorExtras.StructType;
            extras.PipelineStatistics = names;
            extras.PipelineStatisticCount = (nuint)statistics.Count;
            descriptor.NextInChain = &extras.Chain;
        }

        BeginGuard();
        QuerySet handle = Native.WebGpu.DeviceCreateQuerySet(Device, &descriptor);
        string error = EndGuard();
        if (error != null || handle.IsNull)
        {
            if (!handle.IsNull) Native.WebGpu.QuerySetRelease(handle);
            throw new GraphicsException("The query set" + LabelText(label) + " could not be created: " + (error ?? "wgpuDeviceCreateQuerySet returned null."));
        }

        return new GpuQuerySet(this, handle, type, count, statistics, false, label);
    }
}
