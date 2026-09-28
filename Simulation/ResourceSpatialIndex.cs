using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Hollowbound.Simulation;

/// <summary>
/// Uniform-grid spatial index for food resource nodes.
/// Allows efficient nearby food queries instead of scanning all nodes.
/// </summary>
internal sealed class ResourceSpatialIndex
{
    private readonly Dictionary<Point, List<ResourceNode>> _buckets = new();
    private int _lastKnownCount = -1;

    public long QueryCount { get; private set; }
    public long RebuildCount { get; private set; }

    public void Rebuild(IEnumerable<ResourceNode> resources)
    {
        RebuildCount++;
        _buckets.Clear();
        int count = 0;
        foreach (var node in resources)
        {
            if (node.Amount <= 0)
                continue;

            if (!_buckets.TryGetValue(node.Cell, out var bucket))
            {
                bucket = new List<ResourceNode>(2);
                _buckets.Add(node.Cell, bucket);
            }

            bucket.Add(node);
            count++;
        }
        _lastKnownCount = count;
    }

    /// <summary>
    /// Finds food nodes within a given radius of the center point.
    /// Returns nodes sorted by distance (closest first).
    /// </summary>
    public List<(ResourceNode Node, int Distance)> FindNearby(Point center, int radius, int agentId)
    {
        QueryCount++;
        var results = new List<(ResourceNode Node, int Distance)>();

        for (var y = center.Y - radius; y <= center.Y + radius; y++)
        {
            for (var x = center.X - radius; x <= center.X + radius; x++)
            {
                var cell = new Point(x, y);
                if (!_buckets.TryGetValue(cell, out var bucket))
                    continue;

                foreach (var node in bucket)
                {
                    if (node.Amount <= 0 || !node.CanReserve(agentId))
                        continue;

                    var distance = Math.Abs(node.Cell.X - center.X) + Math.Abs(node.Cell.Y - center.Y);
                    results.Add((node, distance));
                }
            }
        }

        // Sort by distance, then by cell. List.Sort is unstable, so without the
        // cell tie-break equal-distance nodes would be ordered by bucket layout
        // history, which differs between an organic world and a restored one.
        results.Sort((a, b) =>
        {
            var cmp = a.Distance.CompareTo(b.Distance);
            if (cmp != 0)
                return cmp;
            cmp = a.Node.Cell.Y.CompareTo(b.Node.Cell.Y);
            return cmp != 0 ? cmp : a.Node.Cell.X.CompareTo(b.Node.Cell.X);
        });
        return results;
    }

    /// <summary>
    /// Updates a single resource node in the index without full rebuild.
    /// </summary>
    public void Update(ResourceNode node)
    {
        // For Bloom effect, the node stays in the same cell, just amount changes.
        // The FindNearby reads Amount directly from the node, so no index rebuild needed.
        // This method exists for API completeness.
    }

    /// <summary>
    /// Gets all food nodes within radius, expanding radius until minimum results found or max radius reached.
    /// </summary>
    public List<(ResourceNode Node, int Distance)> FindNearbyExpanding(Point center, int agentId, int minResults = 1, int startRadius = 3, int maxRadius = 30)
    {
        for (int radius = startRadius; radius <= maxRadius; radius += 3)
        {
            var results = FindNearby(center, radius, agentId);
            if (results.Count >= minResults)
                return results;
        }
        return FindNearby(center, maxRadius, agentId);
    }
}