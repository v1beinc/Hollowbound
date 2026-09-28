using System;
using System.Diagnostics;

namespace Hollowbound.Simulation;

public readonly record struct StepMetricSummary(long Samples, double P50Milliseconds, double P95Milliseconds, double MaxMilliseconds);

public readonly record struct StepPerformanceSummary(
    StepMetricSummary Total,
    StepMetricSummary AgentLoop,
    StepMetricSummary DistanceGridPreparation,
    StepMetricSummary OtherWork);

/// <summary>
/// Bounded, allocation-free while recording, sampled wall-clock timings for
/// simulation ticks. Diagnostics are deliberately not part of deterministic
/// world state and therefore are not serialized into save files.
/// </summary>
public sealed class StepPerformanceProfiler
{
    // Co-prime with the simulation's recurring 2/5/10/25/50/200-tick work,
    // so periodic profiling cannot become phase-locked to only some systems.
    public const int SampleIntervalTicks = 17;
    public const int SampleWindow = 256;

    private static readonly double[] BucketUpperBoundsMs =
        { 0.10, 0.25, 0.5, 1, 2, 4, 8, 16, 32, 64, 128 };

    private readonly long[][] _histograms =
    {
        new long[BucketUpperBoundsMs.Length + 1],
        new long[BucketUpperBoundsMs.Length + 1],
        new long[BucketUpperBoundsMs.Length + 1],
        new long[BucketUpperBoundsMs.Length + 1],
    };
    private readonly byte[][] _sampleBuckets =
    {
        new byte[SampleWindow], new byte[SampleWindow], new byte[SampleWindow], new byte[SampleWindow],
    };
    private readonly long[][] _sampleElapsedTicks =
    {
        new long[SampleWindow], new long[SampleWindow], new long[SampleWindow], new long[SampleWindow],
    };
    private int _nextSampleSlot;
    private int _samplesInWindow;

    public static bool ShouldSample(long tick) => tick > 0 && tick % SampleIntervalTicks == 0;

    public void Record(long totalTicks, long agentLoopTicks, long navigationTicks)
    {
        totalTicks = Math.Max(0, totalTicks);
        agentLoopTicks = Math.Clamp(agentLoopTicks, 0, totalTicks);
        navigationTicks = Math.Clamp(navigationTicks, 0, totalTicks - agentLoopTicks);
        var otherTicks = totalTicks - agentLoopTicks - navigationTicks;

        var replacingOldSample = _samplesInWindow == SampleWindow;
        RecordMetric(0, totalTicks, replacingOldSample);
        RecordMetric(1, agentLoopTicks, replacingOldSample);
        RecordMetric(2, navigationTicks, replacingOldSample);
        RecordMetric(3, otherTicks, replacingOldSample);
        _samplesInWindow = Math.Min(SampleWindow, _samplesInWindow + 1);
        _nextSampleSlot = (_nextSampleSlot + 1) % SampleWindow;
    }

    public StepPerformanceSummary GetSummary() => new(
        GetMetricSummary(0),
        GetMetricSummary(1),
        GetMetricSummary(2),
        GetMetricSummary(3));

    private void RecordMetric(int metricIndex, long elapsedTicks, bool replacingOldSample)
    {
        var oldBucket = _sampleBuckets[metricIndex][_nextSampleSlot];
        if (replacingOldSample)
            _histograms[metricIndex][oldBucket]--;

        var milliseconds = elapsedTicks * 1000d / Stopwatch.Frequency;
        var bucket = 0;
        while (bucket < BucketUpperBoundsMs.Length && milliseconds > BucketUpperBoundsMs[bucket])
            bucket++;

        _sampleBuckets[metricIndex][_nextSampleSlot] = (byte)bucket;
        _sampleElapsedTicks[metricIndex][_nextSampleSlot] = elapsedTicks;
        _histograms[metricIndex][bucket]++;
    }

    private StepMetricSummary GetMetricSummary(int metricIndex)
    {
        var count = _samplesInWindow;
        if (count == 0)
            return default;

        var buckets = _histograms[metricIndex];
        var maxTicks = 0L;
        for (var i = 0; i < count; i++)
            maxTicks = Math.Max(maxTicks, _sampleElapsedTicks[metricIndex][i]);
        var p50 = GetPercentileUpperBound(buckets, count, 0.50, maxTicks);
        var p95 = GetPercentileUpperBound(buckets, count, 0.95, maxTicks);
        var max = maxTicks * 1000d / Stopwatch.Frequency;
        return new StepMetricSummary(count, p50, p95, max);
    }

    private static double GetPercentileUpperBound(long[] buckets, long sampleCount, double percentile, long maxTicks)
    {
        var targetRank = Math.Max(1, (long)Math.Ceiling(sampleCount * percentile));
        long seen = 0;
        for (var bucket = 0; bucket < buckets.Length; bucket++)
        {
            seen += buckets[bucket];
            if (seen < targetRank)
                continue;

            return bucket < BucketUpperBoundsMs.Length
                ? BucketUpperBoundsMs[bucket]
                : maxTicks * 1000d / Stopwatch.Frequency;
        }

        return maxTicks * 1000d / Stopwatch.Frequency;
    }
}
