using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Hollowbound.Simulation;

/// <summary>
/// Chunked spatial index for wall cells.
/// Map is 128x80, chunk size 16x16 = 8x5 = 40 chunks.
/// Allows efficient local wall queries instead of scanning all walls.
/// </summary>
internal sealed class WallSpatialIndex
{
    private const int ChunkSize = 16;
    private readonly int _chunkWidth;
    private readonly int _chunkHeight;
    private readonly List<Point>[] _chunks;

    public long QueryCount { get; private set; }
    public long RebuildCount { get; private set; }

    public WallSpatialIndex(int mapWidth, int mapHeight)
    {
        _chunkWidth = (mapWidth + ChunkSize - 1) / ChunkSize;
        _chunkHeight = (mapHeight + ChunkSize - 1) / ChunkSize;
        _chunks = new List<Point>[_chunkWidth * _chunkHeight];
        for (int i = 0; i < _chunks.Length; i++)
            _chunks[i] = new List<Point>();
    }

    private int ChunkIndex(int chunkX, int chunkY) => chunkY * _chunkWidth + chunkX;

    private (int cx, int cy) CellToChunk(Point cell) => (cell.X / ChunkSize, cell.Y / ChunkSize);

    public void Rebuild(IEnumerable<Point> walls)
    {
        RebuildCount++;
        foreach (var chunk in _chunks)
            chunk.Clear();

        foreach (var wall in walls)
        {
            var (cx, cy) = CellToChunk(wall);
            if (cx >= 0 && cx < _chunkWidth && cy >= 0 && cy < _chunkHeight)
            {
                _chunks[ChunkIndex(cx, cy)].Add(wall);
            }
        }

        // Map restoration enumerates wall cells in map order, while a running
        // world adds them in construction order. Normalise each bucket so wall
        // candidate generation cannot consume RNG in a different order after
        // a save/load round-trip.
        foreach (var chunk in _chunks)
        {
            chunk.Sort((left, right) =>
            {
                var row = left.Y.CompareTo(right.Y);
                return row != 0 ? row : left.X.CompareTo(right.X);
            });
        }
    }

    /// <summary>
    /// Gets walls in the chunk containing the cell and its 8 neighbors.
    /// </summary>
    public IEnumerable<Point> GetWallsNear(Point cell, int chunkRadius = 1)
    {
        QueryCount++;
        var (cx, cy) = CellToChunk(cell);

        for (int dy = -chunkRadius; dy <= chunkRadius; dy++)
        {
            for (int dx = -chunkRadius; dx <= chunkRadius; dx++)
            {
                int ncx = cx + dx;
                int ncy = cy + dy;

                if (ncx >= 0 && ncx < _chunkWidth && ncy >= 0 && ncy < _chunkHeight)
                {
                    foreach (var wall in _chunks[ChunkIndex(ncx, ncy)])
                        yield return wall;
                }
            }
        }
    }

    /// <summary>
    /// Gets walls in a rectangular region of chunks.
    /// </summary>
    public IEnumerable<Point> GetWallsInChunkRange(int minCx, int maxCx, int minCy, int maxCy)
    {
        QueryCount++;
        minCx = Math.Max(0, minCx);
        maxCx = Math.Min(_chunkWidth - 1, maxCx);
        minCy = Math.Max(0, minCy);
        maxCy = Math.Min(_chunkHeight - 1, maxCy);

        for (int cy = minCy; cy <= maxCy; cy++)
        {
            for (int cx = minCx; cx <= maxCx; cx++)
            {
                foreach (var wall in _chunks[ChunkIndex(cx, cy)])
                    yield return wall;
            }
        }
    }

    /// <summary>
    /// Gets all walls (for compatibility with existing code that needs full scan).
    /// </summary>
    public IEnumerable<Point> GetAllWalls()
    {
        foreach (var chunk in _chunks)
        {
            foreach (var wall in chunk)
                yield return wall;
        }
    }

    public int ChunkCount => _chunks.Length;
    public int ChunkWidth => _chunkWidth;
    public int ChunkHeight => _chunkHeight;
}
