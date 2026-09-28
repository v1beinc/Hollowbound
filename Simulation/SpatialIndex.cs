using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Hollowbound.Simulation;

/// <summary>
/// Small uniform-grid index for nearby-agent queries. The world is already
/// discrete, so a bucket per map cell is cheaper and more predictable than a
/// general-purpose tree. Updated incrementally when agents move.
/// </summary>
internal sealed class AgentSpatialIndex
{
    private readonly Dictionary<Point, List<AgentState>> _buckets = new();
    private int _lastKnownAliveCount = -1;
    private long _lastRebuildTick = -1;

    public long QueryCount { get; private set; }
    public long RebuildCount { get; private set; }

    public void Rebuild(IEnumerable<AgentState> agents)
    {
        RebuildCount++;
        _buckets.Clear();
        int aliveCount = 0;
        foreach (var agent in agents)
        {
            if (!agent.Alive)
                continue;

            if (!_buckets.TryGetValue(agent.Cell, out var bucket))
            {
                bucket = new List<AgentState>(2);
                _buckets.Add(agent.Cell, bucket);
            }

            bucket.Add(agent);
            aliveCount++;
        }
        _lastKnownAliveCount = aliveCount;
    }

    /// <summary>
    /// Updates the index incrementally by tracking agent movements via PreviousCell.
    /// Returns true if rebuild was needed, false if incremental update sufficed.
    /// </summary>
    public bool UpdateIncremental(IEnumerable<AgentState> agents, long currentTick)
    {
        // If alive count changed, we need a full rebuild
        int aliveCount = 0;
        foreach (var agent in agents)
        {
            if (agent.Alive) aliveCount++;
        }

        if (aliveCount != _lastKnownAliveCount)
        {
            Rebuild(agents);
            // Update PreviousCell for all alive agents after rebuild
            foreach (var agent in agents)
            {
                if (agent.Alive)
                    agent.PreviousCell = agent.Cell;
            }
            _lastRebuildTick = currentTick;
            return true;
        }

        bool anyMoved = false;

        // First pass: check for movements and update index incrementally
        foreach (var agent in agents)
        {
            if (!agent.Alive) continue;

            if (agent.PreviousCell != agent.Cell)
            {
                anyMoved = true;

                // Remove from old bucket
                if (_buckets.TryGetValue(agent.PreviousCell, out var oldBucket))
                {
                    oldBucket.Remove(agent);
                    if (oldBucket.Count == 0)
                        _buckets.Remove(agent.PreviousCell);
                }

                // Add to new bucket
                if (!_buckets.TryGetValue(agent.Cell, out var newBucket))
                {
                    newBucket = new List<AgentState>(2);
                    _buckets.Add(agent.Cell, newBucket);
                }
                InsertSortedById(newBucket, agent);
            }
        }

        if (!anyMoved && _lastRebuildTick == currentTick)
        {
            return false; // No changes since last rebuild this tick
        }

        // Update PreviousCell for all alive agents
        foreach (var agent in agents)
        {
            if (agent.Alive)
                agent.PreviousCell = agent.Cell;
        }

        _lastRebuildTick = currentTick;
        return true;
    }

    public IEnumerable<AgentState> Nearby(Point center, int radius)
    {
        QueryCount++;
        for (var y = center.Y - radius; y <= center.Y + radius; y++)
        {
            for (var x = center.X - radius; x <= center.X + radius; x++)
            {
                if (!_buckets.TryGetValue(new Point(x, y), out var bucket))
                    continue;

                foreach (var agent in bucket)
                    yield return agent;
            }
        }
    }

    // A rebuilt index receives agents in their stable world-list order, while
    // incremental movement appends them as they arrive. Keep each cell bucket
    // ordered by ID so a save/load rebuild cannot change the order in which
    // callers make RNG-consuming social decisions.
    private static void InsertSortedById(List<AgentState> bucket, AgentState agent)
    {
        var index = bucket.FindIndex(existing => existing.Id > agent.Id);
        if (index < 0)
            bucket.Add(agent);
        else
            bucket.Insert(index, agent);
    }
}
