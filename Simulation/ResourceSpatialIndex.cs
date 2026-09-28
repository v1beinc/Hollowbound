using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Hollowbound.Simulation;

/// <summary>
/// Chunked spatial index for food nodes. Nearby searches visit only occupied
/// resource chunks, then filter exact cells; depleted nodes stay indexed so a
/// later regrowth can make the same node available without a rebuild.
/// </summary>
internal sealed class ResourceSpatialIndex
{
    private const int BucketSize = 8;
    private readonly Dictionary<Point, List<ResourceNode>> _buckets = new();

    public long QueryCount { get; private set; }
    public long RebuildCount { get; private set; }

    public void Rebuild(IEnumerable<ResourceNode> resources)
    {
        RebuildCount++;
        _buckets.Clear();
        foreach (var node in resources)
            Add(node);
    }

    /// <summary>Registers a newly-created node. Amount changes need no index update.</summary>
    public void Add(ResourceNode node)
    {
        var bucketKey = new Point(node.Cell.X / BucketSize, node.Cell.Y / BucketSize);
        if (!_buckets.TryGetValue(bucketKey, out var bucket))
        {
            bucket = new List<ResourceNode>(4);
            _buckets.Add(bucketKey, bucket);
        }

        bucket.Add(node);
    }

    /// <summary>
    /// Finds nodes within a square radius, ordered by Manhattan distance and
    /// then cell coordinates for stable deterministic selection.
    /// </summary>
    public List<(ResourceNode Node, int Distance)> FindNearby(Point center, int radius, int agentId)
    {
        QueryCount++;
        var results = new List<(ResourceNode Node, int Distance)>();
        CollectRing(center, Math.Max(0, radius), -1, agentId, results);
        SortResults(results);
        return results;
    }

    /// <summary>
    /// Expands the search in the same 3-cell steps as the original query, but
    /// visits each resource only in the newly-added outer ring and sorts once.
    /// </summary>
    public List<(ResourceNode Node, int Distance)> FindNearbyExpanding(
        Point center,
        int agentId,
        int minResults = 1,
        int startRadius = 3,
        int maxRadius = 30)
    {
        QueryCount++;
        var results = new List<(ResourceNode Node, int Distance)>();
        var maximumRadius = Math.Max(0, maxRadius);
        var radius = System.Math.Min(Math.Max(0, startRadius), maximumRadius);
        var previousRadius = -1;

        while (true)
        {
            CollectRing(center, radius, previousRadius, agentId, results);
            if (results.Count >= minResults || radius >= maximumRadius)
                break;

            previousRadius = radius;
            radius = System.Math.Min(maximumRadius, radius + 3);
        }

        SortResults(results);
        return results;
    }

    private void CollectRing(
        Point center,
        int radius,
        int previousRadius,
        int agentId,
        List<(ResourceNode Node, int Distance)> results)
    {
        var minX = System.Math.Max(0, center.X - radius);
        var maxX = System.Math.Min(EmergentSimulationWorld.Width - 1, center.X + radius);
        var minY = System.Math.Max(0, center.Y - radius);
        var maxY = System.Math.Min(EmergentSimulationWorld.Height - 1, center.Y + radius);
        if (minX > maxX || minY > maxY)
            return;

        var minChunkX = minX / BucketSize;
        var maxChunkX = maxX / BucketSize;
        var minChunkY = minY / BucketSize;
        var maxChunkY = maxY / BucketSize;

        for (var chunkY = minChunkY; chunkY <= maxChunkY; chunkY++)
        {
            for (var chunkX = minChunkX; chunkX <= maxChunkX; chunkX++)
            {
                if (!_buckets.TryGetValue(new Point(chunkX, chunkY), out var bucket))
                    continue;

                foreach (var node in bucket)
                {
                    var dx = System.Math.Abs(node.Cell.X - center.X);
                    var dy = System.Math.Abs(node.Cell.Y - center.Y);
                    var chebyshevDistance = System.Math.Max(dx, dy);
                    if (chebyshevDistance > radius || chebyshevDistance <= previousRadius ||
                        node.Amount <= 0 || !node.CanReserve(agentId))
                        continue;

                    results.Add((node, dx + dy));
                }
            }
        }
    }

    private static void SortResults(List<(ResourceNode Node, int Distance)> results)
    {
        results.Sort(static (left, right) =>
        {
            var comparison = left.Distance.CompareTo(right.Distance);
            if (comparison != 0)
                return comparison;
            comparison = left.Node.Cell.Y.CompareTo(right.Node.Cell.Y);
            return comparison != 0
                ? comparison
                : left.Node.Cell.X.CompareTo(right.Node.Cell.X);
        });
    }
}
