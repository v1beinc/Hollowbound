using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Hollowbound.Simulation;

public enum CellType : byte
{
    Empty = 0,
    Floor = 1,
    Wall = 2,
    Door = 3,
    Storage = 4,
}

public sealed class Map
{
    public readonly int Width;
    public readonly int Height;
    private readonly CellType[] _cells;
    private readonly List<Point> _storageCells = new();
    private readonly List<Point> _doorCells = new();
    private readonly List<Point> _floorCells = new();
    private readonly List<Point> _wallCells = new();

    public Map(int width, int height)
    {
        Width = width;
        Height = height;
        _cells = new CellType[width * height];
    }

    public CellType this[int x, int y]
    {
        get => InBounds(x, y) ? _cells[y * Width + x] : CellType.Wall;
        set
        {
            if (!InBounds(x, y))
                return;
            var index = y * Width + x;
            var oldType = _cells[index];
            _cells[index] = value;
            UpdateCellLists(x, y, oldType, value);
        }
    }

    public CellType this[Point p]
    {
        get => this[p.X, p.Y];
        set => this[p.X, p.Y] = value;
    }

    public bool InBounds(int x, int y) => x >= 0 && x < Width && y >= 0 && y < Height;
    public bool InBounds(Point p) => InBounds(p.X, p.Y);

    public bool IsWalkable(int x, int y) => InBounds(x, y) && _cells[y * Width + x] != CellType.Wall;
    public bool IsWalkable(Point p) => IsWalkable(p.X, p.Y);

    public bool IsDoor(int x, int y) => InBounds(x, y) && _cells[y * Width + x] == CellType.Door;
    public bool IsDoor(Point p) => IsDoor(p.X, p.Y);

    public bool IsStorage(int x, int y) => InBounds(x, y) && _cells[y * Width + x] == CellType.Storage;
    public bool IsStorage(Point p) => IsStorage(p.X, p.Y);

    public IReadOnlyList<Point> StorageCells => _storageCells;
    public IReadOnlyList<Point> DoorCells => _doorCells;
    public IReadOnlyList<Point> FloorCells => _floorCells;
    public IReadOnlyList<Point> WallCells => _wallCells;

    public byte[] ExportCells()
    {
        var cells = new byte[_cells.Length];
        for (var i = 0; i < _cells.Length; i++)
            cells[i] = (byte)_cells[i];
        return cells;
    }

    public void RestoreCells(IReadOnlyList<byte> cells)
    {
        if (cells.Count != _cells.Length)
            throw new ArgumentException("Map snapshot has an invalid cell count.", nameof(cells));

        _floorCells.Clear();
        _wallCells.Clear();
        _doorCells.Clear();
        _storageCells.Clear();

        for (var i = 0; i < _cells.Length; i++)
        {
            var type = (CellType)cells[i];
            if (!Enum.IsDefined(type))
                throw new ArgumentException("Map snapshot contains an invalid cell type.", nameof(cells));

            _cells[i] = type;
            var point = new Point(i % Width, i / Width);
            switch (type)
            {
                case CellType.Floor: _floorCells.Add(point); break;
                case CellType.Wall: _wallCells.Add(point); break;
                case CellType.Door: _doorCells.Add(point); break;
                case CellType.Storage: _storageCells.Add(point); break;
            }
        }
    }

    private void UpdateCellLists(int x, int y, CellType oldType, CellType newType)
    {
        var p = new Point(x, y);
        RemoveFromLists(p, oldType);
        AddToLists(p, newType);
    }

    private void RemoveFromLists(Point p, CellType type)
    {
        switch (type)
        {
            case CellType.Floor: _floorCells.Remove(p); break;
            case CellType.Wall: _wallCells.Remove(p); break;
            case CellType.Door: _doorCells.Remove(p); break;
            case CellType.Storage: _storageCells.Remove(p); break;
        }
    }

    private void AddToLists(Point p, CellType type)
    {
        switch (type)
        {
            case CellType.Floor: AddInGridOrder(_floorCells, p); break;
            case CellType.Wall: AddInGridOrder(_wallCells, p); break;
            case CellType.Door: AddInGridOrder(_doorCells, p); break;
            case CellType.Storage: AddInGridOrder(_storageCells, p); break;
        }
    }

    private void AddInGridOrder(List<Point> cells, Point point)
    {
        var key = point.Y * Width + point.X;
        var low = 0;
        var high = cells.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            var middleKey = cells[middle].Y * Width + cells[middle].X;
            if (middleKey < key)
                low = middle + 1;
            else
                high = middle;
        }

        cells.Insert(low, point);
    }

    public void InitializeOpen()
    {
        _floorCells.Clear();
        _wallCells.Clear();
        _doorCells.Clear();
        _storageCells.Clear();
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                _cells[y * Width + x] = CellType.Floor;
                _floorCells.Add(new Point(x, y));
            }
        }
    }

    // Compatibility helper for the archived pre-emergent simulation.
    // The active game uses InitializeOpen and never creates a preset shelter.
    public void InitializeShelter(Rectangle shelterBounds)
    {
        InitializeOpen();
        for (var x = shelterBounds.Left; x < shelterBounds.Right; x++)
        {
            this[x, shelterBounds.Top] = CellType.Wall;
            this[x, shelterBounds.Bottom - 1] = CellType.Wall;
        }
        for (var y = shelterBounds.Top; y < shelterBounds.Bottom; y++)
        {
            this[shelterBounds.Left, y] = CellType.Wall;
            this[shelterBounds.Right - 1, y] = CellType.Wall;
        }
        this[shelterBounds.Center.X, shelterBounds.Bottom - 1] = CellType.Door;
        for (var y = shelterBounds.Top + 1; y < shelterBounds.Bottom - 1; y++)
        {
            for (var x = shelterBounds.Left + 1; x < shelterBounds.Right - 1; x++)
                this[x, y] = CellType.Storage;
        }
    }

    public bool CanBuildWallSegment(Point start, bool horizontal)
    {
        for (var i = 0; i < 3; i++)
        {
            var cell = horizontal
                ? new Point(start.X + i, start.Y)
                : new Point(start.X, start.Y + i);
            if (!InBounds(cell) || this[cell] != CellType.Floor)
                return false;
        }
        return true;
    }

    public bool CanBuildWallCell(Point cell) => InBounds(cell) && this[cell] == CellType.Floor;

    public bool BuildWallCell(Point cell)
    {
        if (!CanBuildWallCell(cell))
            return false;

        this[cell] = CellType.Wall;
        return true;
    }

    public bool RemoveWallCell(Point cell)
    {
        if (!InBounds(cell) || this[cell] != CellType.Wall)
            return false;

        this[cell] = CellType.Floor;
        return true;
    }

    public int CountAdjacentWalls(Point cell)
    {
        var count = 0;
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if ((dx != 0 || dy != 0) && InBounds(cell.X + dx, cell.Y + dy) && this[cell.X + dx, cell.Y + dy] == CellType.Wall)
                    count++;
            }
        }
        return count;
    }

    public int CountCardinalWalls(Point cell)
    {
        var count = 0;
        if (InBounds(cell.X + 1, cell.Y) && this[cell.X + 1, cell.Y] == CellType.Wall) count++;
        if (InBounds(cell.X - 1, cell.Y) && this[cell.X - 1, cell.Y] == CellType.Wall) count++;
        if (InBounds(cell.X, cell.Y + 1) && this[cell.X, cell.Y + 1] == CellType.Wall) count++;
        if (InBounds(cell.X, cell.Y - 1) && this[cell.X, cell.Y - 1] == CellType.Wall) count++;
        return count;
    }

    public bool BuildWallSegment(Point start, bool horizontal)
    {
        if (!CanBuildWallSegment(start, horizontal))
            return false;

        for (var i = 0; i < 3; i++)
        {
            var cell = horizontal
                ? new Point(start.X + i, start.Y)
                : new Point(start.X, start.Y + i);
            this[cell] = CellType.Wall;
        }
        return true;
    }

    public Point? FindNearestWall(Point from)
    {
        Point? best = null;
        var bestDistance = int.MaxValue;
        foreach (var wall in _wallCells)
        {
            var distance = Math.Abs(wall.X - from.X) + Math.Abs(wall.Y - from.Y);
            // Tie-break by cell so the result never depends on _wallCells order
            // (which differs between an organic world and a restored one).
            if (distance < bestDistance ||
                (distance == bestDistance && best.HasValue &&
                 wall.Y * Width + wall.X < best.Value.Y * Width + best.Value.X))
            {
                best = wall;
                bestDistance = distance;
            }
        }
        return best;
    }

    public Point? FindNearestWallApproach(Point from)
    {
        Point? best = null;
        var bestDistance = int.MaxValue;
        foreach (var wall in _wallCells)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                        continue;

                    var approach = new Point(wall.X + dx, wall.Y + dy);
                    if (!IsWalkable(approach))
                        continue;

                    var distance = Math.Abs(approach.X - from.X) + Math.Abs(approach.Y - from.Y);
                    // Deterministic tie-break independent of _wallCells order.
                    if (distance < bestDistance ||
                        (distance == bestDistance && best.HasValue &&
                         approach.Y * Width + approach.X < best.Value.Y * Width + best.Value.X))
                    {
                        best = approach;
                        bestDistance = distance;
                    }
                }
            }
        }
        return best;
    }

    /// <summary>
    /// Returns walkable cells adjacent to at least one wall. These are valid
    /// targets for a distance field; wall cells themselves are not walkable and
    /// therefore cannot be used as BFS sources.
    /// </summary>
    public List<Point> FindWalkableWallApproachCells()
    {
        var approaches = new HashSet<Point>();
        foreach (var wall in _wallCells)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                        continue;

                    var approach = new Point(wall.X + dx, wall.Y + dy);
                    if (IsWalkable(approach))
                        approaches.Add(approach);
                }
            }
        }

        return approaches.ToList();
    }

    public bool IsNearWall(Point cell)
    {
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx != 0 || dy != 0)
                {
                    var neighbor = new Point(cell.X + dx, cell.Y + dy);
                    if (InBounds(neighbor) && this[neighbor] == CellType.Wall)
                        return true;
                }
            }
        }
        return false;
    }

    public Point FindNearestStorage(Point from)
    {
        Point best = default;
        int bestDist = int.MaxValue;
        foreach (var storage in _storageCells)
        {
            int dist = Math.Abs(storage.X - from.X) + Math.Abs(storage.Y - from.Y);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = storage;
            }
        }
        return best;
    }

    public Point FindNearestDoor(Point from)
    {
        Point best = default;
        int bestDist = int.MaxValue;
        foreach (var door in _doorCells)
        {
            int dist = Math.Abs(door.X - from.X) + Math.Abs(door.Y - from.Y);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = door;
            }
        }
        return best;
    }

    public Point FindRandomFloorCell(Random rng, Rectangle? bounds = null)
    {
        var cells = bounds.HasValue
            ? _floorCells.FindAll(c => bounds.Value.Contains(c))
            : _floorCells;
        if (cells.Count == 0)
            return new Point(Width / 2, Height / 2);
        return cells[rng.Next(cells.Count)];
    }

    internal Point FindRandomFloorCell(SimulationRandom rng, Rectangle? bounds = null)
    {
        var cells = bounds.HasValue
            ? _floorCells.FindAll(c => bounds.Value.Contains(c))
            : _floorCells;
        if (cells.Count == 0)
            return new Point(Width / 2, Height / 2);
        return cells[rng.Next(cells.Count)];
    }

    public Point FindRandomStorageCell(Random rng)
    {
        if (_storageCells.Count == 0)
            return new Point(Width / 2, Height / 2);
        return _storageCells[rng.Next(_storageCells.Count)];
    }
}

public sealed class PathFinder
{
    private readonly record struct PathPriority(int TotalCost, int Heuristic, int CellIndex) : IComparable<PathPriority>
    {
        public int CompareTo(PathPriority other)
        {
            var result = TotalCost.CompareTo(other.TotalCost);
            if (result != 0) return result;
            result = Heuristic.CompareTo(other.Heuristic);
            return result != 0 ? result : CellIndex.CompareTo(other.CellIndex);
        }
    }

    private readonly Map _map;
    private readonly int _width;
    private readonly int _height;
    private readonly int[] _cameFrom;
    private readonly int[] _visitStamp;
    private readonly int[] _closedStamp;
    private readonly int[] _distanceFromStart;
    private readonly Queue<int> _queue = new();
    private readonly PriorityQueue<int, PathPriority> _open = new(512);
    private readonly int[] _neighbors = new int[8];
    private int _currentVisitStamp;

    // Path buffer reuse
    private readonly List<Point> _pathBuffer = new();

    // Simple LRU cache for recent paths (max 64 entries)
    private readonly Dictionary<(int startIdx, int goalIdx), List<Point>> _pathCache = new();
    private readonly Queue<(int startIdx, int goalIdx)> _cacheOrder = new();
    private const int MaxCacheSize = 64;

    public long PathRequests { get; private set; }
    public long PathCacheHits { get; private set; }
    public long DistanceGridRequests { get; private set; }

    public PathFinder(Map map)
    {
        _map = map;
        _width = map.Width;
        _height = map.Height;
        int size = _width * _height;
        _cameFrom = new int[size];
        _visitStamp = new int[size];
        _closedStamp = new int[size];
        _distanceFromStart = new int[size];
    }

    public List<Point> FindPath(Point start, Point goal)
    {
        PathRequests++;

        if (!_map.InBounds(start) || !_map.InBounds(goal))
            return EmptyPath;

        if (!_map.IsWalkable(goal))
            return EmptyPath;

        if (start == goal)
            return SinglePointPath(start);

        int startIdx = start.Y * _width + start.X;
        int goalIdx = goal.Y * _width + goal.X;

        // Check cache
        var cacheKey = (startIdx, goalIdx);
        if (_pathCache.TryGetValue(cacheKey, out var cachedPath))
        {
            PathCacheHits++;
            // Move to end (LRU)
            _cacheOrder.Enqueue(cacheKey);
            // Callers advance and clear their paths. Never expose the cached
            // mutable list directly or one agent can invalidate another's path.
            return new List<Point>(cachedPath);
        }

        // A* with an admissible Chebyshev heuristic explores the route corridor
        // instead of filling the entire reachable map for every cache miss.
        // Stamps avoid clearing map-sized search arrays between requests.
        if (_currentVisitStamp == int.MaxValue)
        {
            Array.Clear(_visitStamp);
            Array.Clear(_closedStamp);
            _currentVisitStamp = 0;
        }
        var visitStamp = ++_currentVisitStamp;

        _open.Clear();
        _visitStamp[startIdx] = visitStamp;
        _distanceFromStart[startIdx] = 0;
        var startHeuristic = GetHeuristic(startIdx, goal.X, goal.Y);
        _open.Enqueue(startIdx, new PathPriority(startHeuristic, startHeuristic, startIdx));

        while (_open.TryDequeue(out var current, out var priority))
        {
            if (_closedStamp[current] == visitStamp)
                continue;

            var currentCost = _distanceFromStart[current];
            var currentHeuristic = GetHeuristic(current, goal.X, goal.Y);
            if (priority.TotalCost != currentCost + currentHeuristic ||
                priority.Heuristic != currentHeuristic || priority.CellIndex != current)
                continue;

            _closedStamp[current] = visitStamp;
            if (current == goalIdx)
                break;

            int cx = current % _width;
            int cy = current / _width;

            int neighborCount = GetNeighbors(cx, cy, _neighbors);
            for (int i = 0; i < neighborCount; i++)
            {
                int next = _neighbors[i];
                if (_closedStamp[next] == visitStamp)
                    continue;

                var tentativeCost = currentCost + 1;
                if (_visitStamp[next] == visitStamp && tentativeCost >= _distanceFromStart[next])
                    continue;

                var heuristic = GetHeuristic(next, goal.X, goal.Y);
                _cameFrom[next] = current;
                _distanceFromStart[next] = tentativeCost;
                _visitStamp[next] = visitStamp;
                _open.Enqueue(next, new PathPriority(tentativeCost + heuristic, heuristic, next));
            }
        }

        if (_visitStamp[goalIdx] != visitStamp)
            return EmptyPath;

        // Build path in buffer
        _pathBuffer.Clear();
        int currentIdx = goalIdx;
        while (currentIdx != startIdx)
        {
            _pathBuffer.Add(new Point(currentIdx % _width, currentIdx / _width));
            currentIdx = _cameFrom[currentIdx];
        }
        _pathBuffer.Add(start);
        _pathBuffer.Reverse();

        // The caller owns and mutates its path (for example, it may clear it
        // when food is consumed). The cache must therefore keep a separate
        // immutable-by-convention copy even on a cache miss; sharing `result`
        // here made future route choices depend on an earlier agent's path
        // mutation and broke save/load continuation.
        var result = new List<Point>(_pathBuffer);
        _pathCache[cacheKey] = new List<Point>(result);
        _cacheOrder.Enqueue(cacheKey);
        if (_pathCache.Count > MaxCacheSize)
        {
            var oldest = _cacheOrder.Dequeue();
            _pathCache.Remove(oldest);
        }

        return result;
    }

    private int GetHeuristic(int cellIndex, int goalX, int goalY)
    {
        var dx = Math.Abs(cellIndex % _width - goalX);
        var dy = Math.Abs(cellIndex / _width - goalY);
        return Math.Max(dx, dy);
    }

    private static List<Point> EmptyPath => new();

    private static List<Point> SinglePointPath(Point p)
    {
        return new List<Point> { p };
    }

    /// <summary>
    /// Invalidate path cache when map topology changes (walls built/removed).
    /// </summary>
    public void InvalidatePathCache()
    {
        _pathCache.Clear();
        _cacheOrder.Clear();
    }

    private int GetNeighbors(int x, int y, int[] neighbors)
    {
        int count = 0;
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0)
                    continue;

                int nx = x + dx;
                int ny = y + dy;

                if (!_map.InBounds(nx, ny) || !_map.IsWalkable(nx, ny))
                    continue;

                if (dx != 0 && dy != 0)
                {
                    if (!_map.IsWalkable(x + dx, y) || !_map.IsWalkable(x, y + dy))
                        continue;
                }

                neighbors[count++] = ny * _width + nx;
            }
        }
        return count;
    }

    /// <summary>
    /// Multi-source BFS: computes distance from EVERY walkable cell to the NEAREST target.
    /// Single BFS pass from all targets simultaneously - O(W×H) instead of O(N×W×H) for N agents.
    /// Returns distance grid where distance[i] = steps to nearest target, or -1 if unreachable.
    /// </summary>
    public int[] ComputeDistanceToNearestTarget(IEnumerable<Point> targets)
    {
        DistanceGridRequests++;
        var distances = new int[_width * _height];
        Array.Fill(distances, -1);

        _queue.Clear();
        int enqueued = 0;

        foreach (var target in targets)
        {
            if (!_map.InBounds(target) || !_map.IsWalkable(target))
                continue;

            int idx = target.Y * _width + target.X;
            if (distances[idx] == -1)
            {
                distances[idx] = 0;
                _queue.Enqueue(idx);
                enqueued++;
            }
        }

        if (enqueued == 0)
            return distances;

        while (_queue.Count > 0)
        {
            int current = _queue.Dequeue();
            int cx = current % _width;
            int cy = current / _width;
            int currentDist = distances[current];

            int neighborCount = GetNeighbors(cx, cy, _neighbors);
            for (int i = 0; i < neighborCount; i++)
            {
                int next = _neighbors[i];
                if (distances[next] == -1)
                {
                    distances[next] = currentDist + 1;
                    _queue.Enqueue(next);
                }
            }
        }

        return distances;
    }

    /// <summary>
    /// Reconstruct path from start to nearest target using precomputed distance grid.
    /// Returns empty if unreachable.
    /// </summary>
    public List<Point> GetPathFromDistanceGrid(Point start, int[] distanceGrid)
    {
        if (!_map.InBounds(start))
            return EmptyPath;

        int startIdx = start.Y * _width + start.X;
        int dist = distanceGrid[startIdx];
        if (dist <= 0)
            return dist == 0 ? SinglePointPath(start) : EmptyPath;

        // Greedy descent on distance grid
        _pathBuffer.Clear();
        int currentIdx = startIdx;
        _pathBuffer.Add(start);

        while (dist > 0)
        {
            int cx = currentIdx % _width;
            int cy = currentIdx / _width;

            int neighborCount = GetNeighbors(cx, cy, _neighbors);
            int bestNext = -1;
            int bestDist = int.MaxValue;

            for (int i = 0; i < neighborCount; i++)
            {
                int next = _neighbors[i];
                int nextDist = distanceGrid[next];
                if (nextDist >= 0 && nextDist < bestDist)
                {
                    bestDist = nextDist;
                    bestNext = next;
                }
            }

            if (bestNext == -1 || bestDist >= dist)
                break;

            currentIdx = bestNext;
            dist = bestDist;
            _pathBuffer.Add(new Point(currentIdx % _width, currentIdx / _width));
        }

        if (dist != 0)
            return EmptyPath;

        var result = new List<Point>(_pathBuffer);
        return result;
    }
}
