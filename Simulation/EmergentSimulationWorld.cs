using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Xna.Framework;

namespace Hollowbound.Simulation;

public sealed class EmergentSimulationWorld
{
    public const int Width = 128;
    public const int Height = 80;
    public const float TickLength = 0.1f;
    public const int MaxStepsPerFrame = 20;
    public const float MaxBacklogSeconds = 2f;
    public const double MaxSimulationMillisecondsPerFrame = 6d;
    public const float WallBuildEnergyCost = 22f;
    public const int MaxWallCells = 220;

    private readonly Random _rng;
    private readonly List<ResourceNode> _food = new();
    private readonly List<AgentState> _agents = new();
    private readonly Dictionary<Point, int> _foodStorage = new();
    private readonly Map _map;
    private readonly PathFinder _pathFinder;
    private float _accumulator;
    private int _nextAgentId;
    private int _wallBuildAttempts;
    private float _birthCooldown;

    public IReadOnlyList<AgentState> Agents => _agents;
    public IReadOnlyList<ResourceNode> Food => _food;
    public IReadOnlyDictionary<Point, int> FoodStorage => _foodStorage;
    public Map Map => _map;
    public int FoodStockpile { get; private set; } = 18;
    public int Births { get; private set; }
    public int Deaths { get; private set; }
    public int WallBlocksBuilt { get; private set; }
    public long Tick { get; private set; }
    public int Seed { get; }
    public bool IsCatchingUp { get; private set; }

    public EmergentSimulationWorld(int seed, int initialPopulation = 2)
    {
        Seed = seed;
        _rng = new Random(seed);
        _map = new Map(Width, Height);
        _map.InitializeOpen();
        _pathFinder = new PathFinder(_map);

        GenerateFood();
        GenerateAgents(initialPopulation);
    }

    public void Advance(float realSeconds, float timeScale)
    {
        _accumulator = MathF.Min(
            _accumulator + MathF.Min(realSeconds, 0.25f) * timeScale,
            MaxBacklogSeconds);

        var stopwatch = Stopwatch.StartNew();
        var steps = 0;
        while (_accumulator >= TickLength &&
               steps < MaxStepsPerFrame &&
               stopwatch.Elapsed.TotalMilliseconds < MaxSimulationMillisecondsPerFrame)
        {
            Step(TickLength);
            _accumulator -= TickLength;
            steps++;
        }

        IsCatchingUp = _accumulator >= TickLength;
    }

    private void Step(float dt)
    {
        Tick++;

        foreach (var agent in _agents)
        {
            if (!agent.Alive)
                continue;

            agent.Age += dt;
            agent.Energy -= dt * 0.035f;
            agent.MoveCooldown = MathF.Max(0, agent.MoveCooldown - dt);
            agent.RestTimer = MathF.Max(0, agent.RestTimer - dt);
            agent.BuildCooldown = MathF.Max(0, agent.BuildCooldown - dt);

            if (agent.Energy <= 0)
            {
                ReleaseFoodReservation(agent);
                agent.Energy = 0;
                agent.Alive = false;
                agent.Action = AgentAction.Dead;
                Deaths++;
                continue;
            }

            UpdateAgentState(agent);
            MoveAgent(agent);
            ResolveAction(agent);

            if (agent.Action == AgentAction.Resting && _map.IsNearWall(agent.Cell))
                agent.Energy = MathF.Min(100, agent.Energy + dt * 1.5f);
        }

        _birthCooldown = MathF.Max(0, _birthCooldown - dt);
        TryBirth();
        if (Tick % 50 == 0)
            RegrowFood();
    }

    private void UpdateAgentState(AgentState agent)
    {
        switch (agent.Action)
        {
            case AgentAction.Idle:
                if (agent.CarriedFood > 0)
                {
                    agent.Action = AgentAction.ReturningToWall;
                    SetPathToNearestWall(agent);
                }
                else if (!TryStartBuilding(agent))
                {
                    agent.Action = AgentAction.SearchingFood;
                }
                break;

            case AgentAction.Resting:
                if (agent.RestTimer > 0)
                    break;

                if (agent.CarriedFood > 0)
                {
                    agent.Action = AgentAction.ReturningToWall;
                    SetPathToNearestWall(agent);
                }
                else if (!TryStartBuilding(agent))
                {
                    agent.Action = AgentAction.SearchingFood;
                }
                break;

            case AgentAction.SearchingFood:
                var foodTarget = FindNearestFood(agent);
                if (foodTarget.HasValue && SetFoodTarget(agent, foodTarget.Value))
                {
                    agent.Action = AgentAction.GoingToFood;
                }
                else if (!TryStartBuilding(agent))
                {
                    agent.Action = AgentAction.Idle;
                }
                break;

            case AgentAction.GoingToFood:
                if (agent.Cell == agent.TargetCell)
                {
                    var targetFood = FindFoodAt(agent.TargetCell);
                    if (targetFood is not null && targetFood.Amount > 0)
                        agent.Action = AgentAction.GatheringFood;
                    else
                    {
                        MarkFoodFailure(agent);
                        ReleaseFoodReservation(agent);
                        agent.Action = AgentAction.SearchingFood;
                    }
                }
                else if (agent.Path.Count == 0 || agent.PathIndex >= agent.Path.Count)
                {
                    MarkFoodFailure(agent);
                    ReleaseFoodReservation(agent);
                    agent.Action = AgentAction.SearchingFood;
                }
                break;

            case AgentAction.GatheringFood:
                var foodAtCell = FindFoodAt(agent.Cell);
                if (agent.CarriedFood >= 3 || agent.Energy < 65)
                {
                    ReleaseFoodReservation(agent);
                    agent.Action = AgentAction.ReturningToWall;
                    SetPathToNearestWall(agent);
                }
                else if (foodAtCell is null || foodAtCell.Amount <= 0)
                {
                    MarkFoodFailure(agent);
                    ReleaseFoodReservation(agent);
                    if (agent.CarriedFood > 0)
                    {
                        agent.Action = AgentAction.ReturningToWall;
                        SetPathToNearestWall(agent);
                    }
                    else
                    {
                        agent.Action = AgentAction.SearchingFood;
                    }
                }
                break;

            case AgentAction.CarryingFood:
            case AgentAction.ReturningToWall:
                if (_map.IsNearWall(agent.Cell) || _map.WallCells.Count == 0)
                {
                    agent.Action = AgentAction.StoringFood;
                }
                else if (agent.Path.Count == 0 || agent.PathIndex >= agent.Path.Count)
                {
                    if (!SetPathToNearestWall(agent))
                        agent.Action = AgentAction.StoringFood;
                }
                break;

            case AgentAction.StoringFood:
                // Food is deposited beside a wall in ResolveAction.
                break;

            case AgentAction.Building:
                // The segment was placed when the action started.
                break;
        }
    }

    private void MoveAgent(AgentState agent)
    {
        if (agent.MoveCooldown > 0 || agent.Path.Count == 0 || agent.PathIndex >= agent.Path.Count)
            return;

        var nextCell = agent.Path[agent.PathIndex];
        if (agent.Cell == nextCell)
        {
            agent.PathIndex++;
            if (agent.PathIndex >= agent.Path.Count)
                return;
            nextCell = agent.Path[agent.PathIndex];
        }

        if (!_map.IsWalkable(nextCell))
        {
            agent.Path.Clear();
            agent.PathIndex = 0;
            return;
        }

        agent.Cell = nextCell;
        agent.PathIndex++;
        agent.MoveCooldown = agent.Action == AgentAction.ReturningToWall ? 0.1f : 0.2f;
    }

    private void ResolveAction(AgentState agent)
    {
        if (agent.Action == AgentAction.GatheringFood)
        {
            var node = FindFoodAt(agent.Cell);
            if (node is not null && node.Amount > 0)
            {
                var firstUnit = agent.CarriedFood == 0;
                node.Amount--;
                agent.CarriedFood++;
                agent.Energy = MathF.Min(100, agent.Energy + 5);
                if (firstUnit)
                {
                    agent.KnownFoodCell = agent.Cell;
                    agent.HasKnownFood = true;
                    agent.FoodKnowledge = MathF.Min(1f, agent.FoodKnowledge + 0.2f);
                    agent.SuccessfulFoodTrips++;
                }
                if (node.Amount <= 0)
                    ReleaseFoodReservation(agent);
            }
        }

        if (agent.Action == AgentAction.StoringFood && agent.CarriedFood > 0)
        {
            var stored = agent.CarriedFood;
            FoodStockpile += stored;
            _foodStorage[agent.Cell] = _foodStorage.GetValueOrDefault(agent.Cell) + stored;
            var homeWall = _map.FindNearestWall(agent.Cell);
            if (homeWall.HasValue)
            {
                agent.HomeWallCell = homeWall.Value;
                agent.HasHomeWall = true;
            }
            agent.CarriedFood = 0;
            agent.Energy = MathF.Min(100, agent.Energy + 4);
            agent.Action = AgentAction.Resting;
            agent.RestTimer = 2f;
        }

        if (agent.Action == AgentAction.Building)
        {
            agent.Action = AgentAction.Resting;
            agent.RestTimer = 3f;
        }
    }

    private bool TryStartBuilding(AgentState agent)
    {
        if (agent.BuildCooldown > 0 || agent.Energy < WallBuildEnergyCost ||
            _map.WallCells.Count + 1 > MaxWallCells || agent.CarriedFood > 0)
            return false;

        var candidates = new List<Point>();
        var nearestWall = _map.FindNearestWall(agent.Cell);
        if (nearestWall.HasValue &&
            Math.Abs(nearestWall.Value.X - agent.Cell.X) + Math.Abs(nearestWall.Value.Y - agent.Cell.Y) <= 9)
        {
            var wall = nearestWall.Value;
            for (var radius = 1; radius <= 2; radius++)
            {
                for (var dy = -radius; dy <= radius; dy++)
                {
                    for (var dx = -radius; dx <= radius; dx++)
                    {
                        if (Math.Abs(dx) + Math.Abs(dy) != radius)
                            continue;
                        candidates.Add(new Point(wall.X + dx, wall.Y + dy));
                    }
                }
            }
        }
        else
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx != 0 || dy != 0)
                        candidates.Add(new Point(agent.Cell.X + dx, agent.Cell.Y + dy));
                }
            }
        }

        var offset = _wallBuildAttempts++ % candidates.Count;
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[(i + offset) % candidates.Count];
            if (!CanBuildAt(candidate))
                continue;

            _map.BuildWallCell(candidate);
            agent.Energy -= WallBuildEnergyCost;
            agent.BuildCooldown = 4f;
            agent.Action = AgentAction.Building;
            agent.Path.Clear();
            agent.PathIndex = 0;
            agent.HomeWallCell = candidate;
            agent.HasHomeWall = true;
            WallBlocksBuilt++;
            return true;
        }

        return false;
    }

    private bool CanBuildAt(Point cell)
    {
        if (!_map.CanBuildWallCell(cell))
            return false;

        return !_agents.Any(agent => agent.Alive && agent.Cell == cell) && FindFoodAt(cell) is null;
    }

    private bool SetPathToNearestWall(AgentState agent)
    {
        var approach = _map.FindNearestWallApproach(agent.Cell);
        if (!approach.HasValue)
            return false;

        SetPath(agent, approach.Value);
        return true;
    }

    private void SetPath(AgentState agent, Point target)
    {
        agent.Path = _pathFinder.FindPath(agent.Cell, target);
        agent.PathIndex = 0;
        agent.TargetCell = target;
    }

    private void TryBirth()
    {
        var alive = _agents.Count(a => a.Alive);
        if (_birthCooldown > 0 || alive >= 250 || FoodStockpile < Math.Max(8, alive * 2) || _rng.NextDouble() > 0.01)
            return;

        var candidates = _agents
            .Where(a => a.Alive && _map.IsNearWall(a.Cell) && a.Energy > 65 && a.Age >= 5f)
            .ToArray();
        AgentState? firstParent = null;
        AgentState? secondParent = null;
        for (var i = 0; i < candidates.Length && firstParent is null; i++)
        {
            for (var j = i + 1; j < candidates.Length; j++)
            {
                var distance = Math.Abs(candidates[i].Cell.X - candidates[j].Cell.X) +
                               Math.Abs(candidates[i].Cell.Y - candidates[j].Cell.Y);
                if (distance <= 8)
                {
                    firstParent = candidates[i];
                    secondParent = candidates[j];
                    break;
                }
            }
        }

        if (firstParent is null || secondParent is null)
            return;

        var spawnCell = FindFreeHomeCell(firstParent.Cell);
        if (!spawnCell.HasValue)
            return;

        FoodStockpile = Math.Max(0, FoodStockpile - 6);
        ConsumeStoredFood(6);
        firstParent.Energy -= 8;
        secondParent.Energy -= 8;
        _agents.Add(new AgentState
        {
            Id = _nextAgentId++,
            FactionId = firstParent.FactionId,
            Cell = spawnCell.Value,
            TargetCell = spawnCell.Value,
            Action = AgentAction.Resting,
            RestTimer = 3f,
            Energy = 80f,
        });
        Births++;
        _birthCooldown = 4f;
    }

    private Point? FindFreeHomeCell(Point center)
    {
        for (var radius = 1; radius <= 4; radius++)
        {
            for (var dy = -radius; dy <= radius; dy++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Abs(dx) + Math.Abs(dy) != radius)
                        continue;

                    var cell = new Point(center.X + dx, center.Y + dy);
                    if (!_map.IsWalkable(cell) || !_map.IsNearWall(cell))
                        continue;
                    if (_agents.Any(agent => agent.Alive && agent.Cell == cell) || FindFoodAt(cell) is not null)
                        continue;
                    return cell;
                }
            }
        }

        return null;
    }

    private void ConsumeStoredFood(int amount)
    {
        var remaining = amount;
        foreach (var cell in _foodStorage.Keys.ToArray())
        {
            if (remaining <= 0)
                break;

            var taken = Math.Min(remaining, _foodStorage[cell]);
            _foodStorage[cell] -= taken;
            remaining -= taken;
            if (_foodStorage[cell] <= 0)
                _foodStorage.Remove(cell);
        }
    }

    private void GenerateAgents(int count)
    {
        var spawnBounds = new Rectangle(Width / 2 - 4, Height / 2 - 4, 8, 8);
        for (var i = 0; i < count; i++)
        {
            var spawnCell = _map.FindRandomFloorCell(_rng, spawnBounds);
            _agents.Add(new AgentState
            {
                Id = _nextAgentId++,
                FactionId = i % 2,
                Cell = spawnCell,
                TargetCell = spawnCell,
                Action = AgentAction.SearchingFood,
            });
        }
    }

    private void GenerateFood()
    {
        var occupied = new HashSet<Point>();
        for (var i = 0; i < 180; i++)
        {
            var cell = FindRandomFloorCell(occupied);
            if (!cell.HasValue)
                break;

            occupied.Add(cell.Value);
            _food.Add(new ResourceNode { Cell = cell.Value, Amount = _rng.Next(2, 7) });
        }
    }

    private void RegrowFood()
    {
        if (_food.Count(n => n.Amount > 0) >= 220)
            return;

        var occupied = new HashSet<Point>(_food.Where(n => n.Amount > 0).Select(n => n.Cell));
        var cell = FindRandomFloorCell(occupied);
        if (cell.HasValue)
            _food.Add(new ResourceNode { Cell = cell.Value, Amount = 4 });
    }

    private Point? FindRandomFloorCell(HashSet<Point> occupied)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var cell = new Point(_rng.Next(4, Width - 4), _rng.Next(4, Height - 4));
            if (_map.IsWalkable(cell) && !occupied.Contains(cell))
                return cell;
        }

        return null;
    }

    private bool SetFoodTarget(AgentState agent, Point target)
    {
        var node = FindFoodAt(target);
        if (node is null || node.Amount <= 0 || !node.CanReserve(agent.Id))
            return false;

        ReleaseFoodReservation(agent);
        node.ReservedBy.Add(agent.Id);
        agent.FoodTargetCell = target;
        agent.HasFoodTarget = true;
        SetPath(agent, target);
        if (agent.Path.Count > 0)
            return true;

        ReleaseFoodReservation(agent);
        return false;
    }

    private void ReleaseFoodReservation(AgentState agent)
    {
        if (!agent.HasFoodTarget)
            return;

        foreach (var node in _food)
            node.ReservedBy.Remove(agent.Id);
        agent.HasFoodTarget = false;
    }

    private Point? FindNearestFood(AgentState agent)
    {
        Point? best = null;
        var bestScore = int.MaxValue;
        foreach (var node in _food)
        {
            if (node.Amount <= 0 || !node.CanReserve(agent.Id))
                continue;

            var distance = Math.Abs(node.Cell.X - agent.Cell.X) + Math.Abs(node.Cell.Y - agent.Cell.Y);
            var reservationPenalty = node.ReservedBy.Contains(agent.Id) ? 0 : node.ReservedBy.Count * 12;
            var learnedBonus = agent.HasKnownFood && agent.KnownFoodCell == node.Cell
                ? (int)(25 * agent.FoodKnowledge)
                : 0;
            var score = distance + reservationPenalty - learnedBonus;
            if (score < bestScore)
            {
                best = node.Cell;
                bestScore = score;
            }
        }

        return best;
    }

    private void MarkFoodFailure(AgentState agent)
    {
        agent.FailedFoodTrips++;
        agent.FoodKnowledge = MathF.Max(0, agent.FoodKnowledge - 0.15f);
        if (agent.HasKnownFood && agent.KnownFoodCell == agent.FoodTargetCell && agent.FoodKnowledge <= 0.05f)
            agent.HasKnownFood = false;
    }

    private ResourceNode? FindFoodAt(Point cell)
    {
        foreach (var node in _food)
        {
            if (node.Amount > 0 && node.Cell == cell)
                return node;
        }

        return null;
    }
}
