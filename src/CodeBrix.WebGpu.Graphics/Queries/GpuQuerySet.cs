using System;
using System.Collections.Generic;
using CodeBrix.WebGpu.Native;

namespace CodeBrix.WebGpu.Graphics;

/// <summary>
/// A set of GPU queries: occlusion (samples that passed the depth test), timestamp (GPU clock ticks) or pipeline
/// statistics (shader invocation counts). Write it from passes and frames, then
/// <see cref="GraphicsFrame.ResolveQuerySet" /> it into a buffer made by
/// <see cref="GraphicsContext.CreateQueryResolveBuffer" /> and read that. <see cref="GpuTimer" /> wraps the timestamp
/// case into milliseconds.
/// </summary>
public sealed class GpuQuerySet : GraphicsResource
{
    /// <summary>The query type value of pipeline-statistics sets (the engine's native query type).</summary>
    public const QueryType PipelineStatisticsType = (QueryType)NativeQueryType.PipelineStatistics;

    internal GpuQuerySet(GraphicsContext context, QuerySet handle, QueryType type, int count, IReadOnlyList<PipelineStatisticName> statistics, bool isWrapped, string label)
        : base(context, label)
    {
        Handle = handle;
        Type = type;
        Count = count;
        Statistics = statistics ?? Array.Empty<PipelineStatisticName>();
        IsWrapped = isWrapped;
    }

    /// <summary>The native query set. Owned by this object (for a wrapped one: this object's reference).</summary>
    public QuerySet Handle { get; private set; }

    /// <summary>The query type (<see cref="QueryType.Occlusion" />, <see cref="QueryType.Timestamp" /> or <see cref="PipelineStatisticsType" />).</summary>
    public QueryType Type { get; }

    /// <summary>True for a pipeline-statistics set.</summary>
    public bool IsPipelineStatistics => Type == PipelineStatisticsType;

    /// <summary>The number of queries.</summary>
    public int Count { get; }

    /// <summary>The statistics each pipeline-statistics query records, in result order (empty for other types and for wrapped sets).</summary>
    public IReadOnlyList<PipelineStatisticName> Statistics { get; }

    /// <summary>
    /// How many 64-bit values one query resolves to: one, or one per statistic for a pipeline-statistics set. The
    /// engine writes a set's statistics in ascending <see cref="PipelineStatisticName" /> order.
    /// </summary>
    public int ValuesPerQuery => IsPipelineStatistics ? Math.Max(1, Statistics.Count) : 1;

    /// <summary>The bytes all queries resolve to (<see cref="Count" /> x <see cref="ValuesPerQuery" /> x 8).</summary>
    public ulong ResolvedSize => (ulong)Count * (ulong)ValuesPerQuery * 8;

    /// <summary>True when the set belongs to another library and this object holds only its own reference.</summary>
    public bool IsWrapped { get; }

    private protected override void ReleaseNative()
    {
        if (!Handle.IsNull) Native.WebGpu.QuerySetRelease(Handle);
        Handle = QuerySet.Null;
    }
}
