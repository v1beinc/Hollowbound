using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;

namespace Hollowbound.Simulation;

/// <summary>
/// World divided into 16x16 chunks for spatial partitioning and LOD support.
/// Map: 128x80 = 8x5 chunks = 40 chunks total.
/// </summary>
public sealed class WorldChunks
{
    public const int ChunkSize = 16;
    private readonly int _chunkWidth;
    private readonly int _chunkHeight;
    private readonly ChunkData[] _chunks;

    public int ChunkWidth => _chunkWidth;
    public int ChunkHeight => _chunkHeight;
    public int TotalChunks => _chunks.Length;

    public WorldChunks(int mapWidth, int mapHeight)
    {
        _chunkWidth = (mapWidth + ChunkSize - 1) / ChunkSize;
        _chunkHeight = (mapHeight + ChunkSize - 1) / ChunkSize;
        _chunks = new ChunkData[_chunkWidth * _chunkHeight];
        for (int i = 0; i < _chunks.Length; i++)
            _chunks[i] = new ChunkData();
    }

    private int Index(int cx, int cy) => cy * _chunkWidth + cx;

    public (int cx, int cy) CellToChunk(Point cell)
    {
        return (cell.X / ChunkSize, cell.Y / ChunkSize);
    }

    public Point ChunkToCellOrigin(int cx, int cy)
    {
        return new Point(cx * ChunkSize, cy * ChunkSize);
    }

    public Rectangle ChunkBounds(int cx, int cy)
    {
        return new Rectangle(cx * ChunkSize, cy * ChunkSize, ChunkSize, ChunkSize);
    }

    public ChunkData GetChunk(int cx, int cy)
    {
        if (cx < 0 || cx >= _chunkWidth || cy < 0 || cy >= _chunkHeight)
            return default;
        return _chunks[Index(cx, cy)];
    }

    public void SetChunk(int cx, int cy, ChunkData chunk)
    {
        if (cx < 0 || cx >= _chunkWidth || cy < 0 || cy >= _chunkHeight)
            return;
        _chunks[Index(cx, cy)] = chunk;
    }

    public ChunkData GetChunkAt(Point cell)
    {
        var (cx, cy) = CellToChunk(cell);
        return GetChunk(cx, cy);
    }

    public void Clear()
    {
        for (int i = 0; i < _chunks.Length; i++)
        {
            _chunks[i].AgentCount = 0;
            _chunks[i].FoodCount = 0;
            _chunks[i].WallCount = 0;
            _chunks[i].ActivityLevel = 0f;
            _chunks[i].LastUpdateTick = -1;
            _chunks[i].Faction0Count = 0;
            _chunks[i].Faction1Count = 0;
            _chunks[i].BirthRate = 0f;
            _chunks[i].DeathRate = 0f;
            _chunks[i].MigrationPressure = 0f;
            // Aggregated LOD fields
            _chunks[i].AggregatedPopulation = 0;
            _chunks[i].AggregatedAvgEnergy = 0f;
            _chunks[i].AggregatedFoodStockpile = 0;
            _chunks[i].AggregatedFactionGoal = 0;
            _chunks[i].AggregatedCohesion = 0f;
            _chunks[i].LastAggregatedUpdateTick = -1;
            _chunks[i].IsAggregated = false;
        }
    }

    public void RegisterAgent(Point cell, int factionId)
    {
        var (cx, cy) = CellToChunk(cell);
        var chunk = _chunks[Index(cx, cy)];
        chunk.AgentCount++;
        if (factionId == 0) chunk.Faction0Count++;
        else if (factionId == 1) chunk.Faction1Count++;
        _chunks[Index(cx, cy)] = chunk;
    }

    public void UnregisterAgent(Point cell, int factionId)
    {
        var (cx, cy) = CellToChunk(cell);
        var chunk = _chunks[Index(cx, cy)];
        chunk.AgentCount = Math.Max(0, chunk.AgentCount - 1);
        if (factionId == 0) chunk.Faction0Count = Math.Max(0, chunk.Faction0Count - 1);
        else if (factionId == 1) chunk.Faction1Count = Math.Max(0, chunk.Faction1Count - 1);
        _chunks[Index(cx, cy)] = chunk;
    }

    public void RegisterFood(Point cell)
    {
        var (cx, cy) = CellToChunk(cell);
        var chunk = _chunks[Index(cx, cy)];
        chunk.FoodCount++;
        _chunks[Index(cx, cy)] = chunk;
    }

    public void UnregisterFood(Point cell)
    {
        var (cx, cy) = CellToChunk(cell);
        var chunk = _chunks[Index(cx, cy)];
        chunk.FoodCount = Math.Max(0, chunk.FoodCount - 1);
        _chunks[Index(cx, cy)] = chunk;
    }

    public void RegisterWall(Point cell)
    {
        var (cx, cy) = CellToChunk(cell);
        var chunk = _chunks[Index(cx, cy)];
        chunk.WallCount++;
        _chunks[Index(cx, cy)] = chunk;
    }

    public void UnregisterWall(Point cell)
    {
        var (cx, cy) = CellToChunk(cell);
        var chunk = _chunks[Index(cx, cy)];
        chunk.WallCount = Math.Max(0, chunk.WallCount - 1);
        _chunks[Index(cx, cy)] = chunk;
    }

    public void UpdateActivity(int cx, int cy, float activity, long tick)
    {
        if (cx < 0 || cx >= _chunkWidth || cy < 0 || cy >= _chunkHeight) return;
        var chunk = _chunks[Index(cx, cy)];
        chunk.ActivityLevel = activity;
        chunk.LastUpdateTick = tick;
        _chunks[Index(cx, cy)] = chunk;
    }

    /// <summary>
    /// Gets chunks sorted by activity level (highest first).
    /// Useful for LOD: process active chunks fully, aggregate inactive ones.
    /// </summary>
    public IEnumerable<ChunkData> GetChunksByActivity()
    {
        var sorted = new List<ChunkData>(_chunks);
        sorted.Sort((a, b) => b.ActivityLevel.CompareTo(a.ActivityLevel));
        return sorted;
    }

    /// <summary>
    /// Gets all chunks that have any agents or resources.
    /// </summary>
    public IEnumerable<ChunkData> GetActiveChunks()
    {
        foreach (var chunk in _chunks)
        {
            if (chunk.AgentCount > 0 || chunk.FoodCount > 0 || chunk.WallCount > 0)
                yield return chunk;
        }
    }

    public int ActiveChunkCount => _chunks.Count(c => c.AgentCount > 0 || c.FoodCount > 0 || c.WallCount > 0);

    /// <summary>
    /// Returns centers of chunks with lowest activity for exploration targeting.
    /// </summary>
    public IEnumerable<Point> GetLowActivityChunkCenters()
    {
        var sortedIndices = Enumerable.Range(0, _chunks.Length)
            .OrderBy(index => _chunks[index].ActivityLevel)
            .ThenBy(index => index)
            .Where(index => _chunks[index].AgentCount == 0 && _chunks[index].FoodCount == 0)
            .Take(8);

        foreach (var index in sortedIndices)
        {
            int cx = index % _chunkWidth;
            int cy = index / _chunkWidth;
            yield return ChunkToCellOrigin(cx, cy);
        }
    }

    /// <summary>
    /// Returns centers of chunks with highest activity for migration targeting.
    /// </summary>
    public IEnumerable<Point> GetHighActivityChunkCenters()
    {
        var sortedIndices = Enumerable.Range(0, _chunks.Length)
            .OrderByDescending(index => _chunks[index].ActivityLevel)
            .ThenBy(index => index)
            .Where(index => _chunks[index].AgentCount > 0)
            .Take(8);

        foreach (var index in sortedIndices)
        {
            int cx = index % _chunkWidth;
            int cy = index / _chunkWidth;
            yield return ChunkToCellOrigin(cx, cy);
        }
    }

    public List<ChunkSnapshot> GetAllChunksSnapshot()
    {
        var snapshots = new List<ChunkSnapshot>(_chunks.Length);
        for (int cy = 0; cy < _chunkHeight; cy++)
        {
            for (int cx = 0; cx < _chunkWidth; cx++)
            {
                var chunk = _chunks[Index(cx, cy)];
                snapshots.Add(new ChunkSnapshot
                {
                    Cx = cx,
                    Cy = cy,
                    AgentCount = chunk.AgentCount,
                    FoodCount = chunk.FoodCount,
                    WallCount = chunk.WallCount,
                    ActivityLevel = chunk.ActivityLevel,
                    LastUpdateTick = chunk.LastUpdateTick,
                    Faction0Count = chunk.Faction0Count,
                    Faction1Count = chunk.Faction1Count,
                    BirthRate = chunk.BirthRate,
                    DeathRate = chunk.DeathRate,
                    MigrationPressure = chunk.MigrationPressure,
                    SettlementId = chunk.SettlementId,
                    AvgEnergy = chunk.AvgEnergy,
                    TotalFoodConsumed = chunk.TotalFoodConsumed,
                    TotalFoodStored = chunk.TotalFoodStored,
                    AggregatedPopulation = chunk.AggregatedPopulation,
                    AggregatedAvgEnergy = chunk.AggregatedAvgEnergy,
                    AggregatedFoodStockpile = chunk.AggregatedFoodStockpile,
                    AggregatedFactionGoal = chunk.AggregatedFactionGoal,
                    AggregatedCohesion = chunk.AggregatedCohesion,
                    LastAggregatedUpdateTick = chunk.LastAggregatedUpdateTick,
                    IsAggregated = chunk.IsAggregated,
                });
            }
        }
        return snapshots;
    }

    public void RestoreFromSnapshot(List<ChunkSnapshot> snapshots)
    {
        if (snapshots == null) return;
        foreach (var snap in snapshots)
        {
            if (snap.Cx >= 0 && snap.Cx < _chunkWidth && snap.Cy >= 0 && snap.Cy < _chunkHeight)
            {
                var chunk = _chunks[Index(snap.Cx, snap.Cy)];
                chunk.AgentCount = snap.AgentCount;
                chunk.FoodCount = snap.FoodCount;
                chunk.WallCount = snap.WallCount;
                chunk.ActivityLevel = snap.ActivityLevel;
                chunk.LastUpdateTick = snap.LastUpdateTick;
                chunk.Faction0Count = snap.Faction0Count;
                chunk.Faction1Count = snap.Faction1Count;
                chunk.BirthRate = snap.BirthRate;
                chunk.DeathRate = snap.DeathRate;
                chunk.MigrationPressure = snap.MigrationPressure;
                chunk.SettlementId = snap.SettlementId;
                chunk.AvgEnergy = snap.AvgEnergy;
                chunk.TotalFoodConsumed = snap.TotalFoodConsumed;
                chunk.TotalFoodStored = snap.TotalFoodStored;
                chunk.AggregatedPopulation = snap.AggregatedPopulation;
                chunk.AggregatedAvgEnergy = snap.AggregatedAvgEnergy;
                chunk.AggregatedFoodStockpile = snap.AggregatedFoodStockpile;
                chunk.AggregatedFactionGoal = snap.AggregatedFactionGoal;
                chunk.AggregatedCohesion = snap.AggregatedCohesion;
                chunk.LastAggregatedUpdateTick = snap.LastAggregatedUpdateTick;
                chunk.IsAggregated = snap.IsAggregated;
                _chunks[Index(snap.Cx, snap.Cy)] = chunk;
            }
        }
    }
}

public struct ChunkData
{
    public int AgentCount;
    public int FoodCount;
    public int WallCount;
    public float ActivityLevel;
    public long LastUpdateTick;
    public int Faction0Count;
    public int Faction1Count;
    public float BirthRate;
    public float DeathRate;
    public float MigrationPressure;

    // Future: settlement affiliation, aggregated stats for LOD
    public int SettlementId;
    public float AvgEnergy;
    public int TotalFoodConsumed;
    public int TotalFoodStored;

    // Aggregated LOD fields
    public int AggregatedPopulation;
    public float AggregatedAvgEnergy;
    public int AggregatedFoodStockpile;
    public byte AggregatedFactionGoal;
    public float AggregatedCohesion;
    public long LastAggregatedUpdateTick;
    public bool IsAggregated;
}