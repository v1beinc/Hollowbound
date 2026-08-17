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
            case CellType.Floor: _floorCells.Add(p); break;
            case CellType.Wall: _wallCells.Add(p); break;
            case CellType.Door: _doorCells.Add(p); break;
            case CellType.Storage: _storageCells.Add(p); break;
        }
    }

    public void InitializeOpen()
    {
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                this[x, y] = CellType.Floor;
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
            if (distance < bestDistance)
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
                    if (distance < bestDistance)
                    {
                        best = approach;
                        bestDistance = distance;
                    }
                }
            }
        }
        return best;
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

    public Point FindRandomStorageCell(Random rng)
    {
        if (_storageCells.Count == 0)
            return new Point(Width / 2, Height / 2);
        return _storageCells[rng.Next(_storageCells.Count)];
    }
}

public sealed class PathFinder
{
    private readonly Map _map;
    private readonly int _width;
    private readonly int _height;
    private readonly int[] _cameFrom;
    private readonly int[] _costSoFar;
    private readonly bool[] _visited;
    private readonly Queue<int> _queue = new();
    private readonly int[] _neighbors = new int[8];

    public PathFinder(Map map)
    {
        _map = map;
        _width = map.Width;
        _height = map.Height;
        int size = _width * _height;
        _cameFrom = new int[size];
        _costSoFar = new int[size];
        _visited = new bool[size];
    }

    public List<Point> FindPath(Point start, Point goal)
    {
        if (!_map.InBounds(start) || !_map.InBounds(goal))
            return new List<Point>();

        if (!_map.IsWalkable(goal))
            return new List<Point>();

        if (start == goal)
            return new List<Point> { start };

        Array.Fill(_visited, false);
        Array.Fill(_costSoFar, int.MaxValue);
        Array.Fill(_cameFrom, -1);

        _queue.Clear();
        int startIdx = start.Y * _width + start.X;
        int goalIdx = goal.Y * _width + goal.X;

        _queue.Enqueue(startIdx);
        _visited[startIdx] = true;
        _costSoFar[startIdx] = 0;

        while (_queue.Count > 0)
        {
            int current = _queue.Dequeue();
            if (current == goalIdx)
                break;

            int cx = current % _width;
            int cy = current / _width;

            int neighborCount = GetNeighbors(cx, cy, _neighbors);
            for (int i = 0; i < neighborCount; i++)
            {
                int next = _neighbors[i];
                if (_visited[next])
                    continue;

                int newCost = _costSoFar[current] + 1;
                if (newCost < _costSoFar[next])
                {
                    _costSoFar[next] = newCost;
                    _cameFrom[next] = current;
                    _visited[next] = true;
                    _queue.Enqueue(next);
                }
            }
        }

        if (!_visited[goalIdx])
            return new List<Point>();

        var path = new List<Point>();
        int currentIdx = goalIdx;
        while (currentIdx != startIdx)
        {
            path.Add(new Point(currentIdx % _width, currentIdx / _width));
            currentIdx = _cameFrom[currentIdx];
        }
        path.Add(start);
        path.Reverse();
        return path;
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
}
