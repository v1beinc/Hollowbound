using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Xna.Framework;

namespace Hollowbound.Simulation;

public sealed class ResourceNode
{
    public const int MaxGatherers = 2;
    public Point Cell { get; init; }
    public int Amount { get; set; }
    public HashSet<int> ReservedBy { get; } = new();

    public bool CanReserve(int agentId) => ReservedBy.Contains(agentId) || ReservedBy.Count < MaxGatherers;
}

public sealed class SimulationWorld
{
    public const int Width = 128;
    public const int Height = 80;
    public const float TickLength = 0.1f;
    public const int MaxStepsPerFrame = 20;
    public const float MaxBacklogSeconds = 2f;
    public const double MaxSimulationMillisecondsPerFrame = 6d;

    private readonly Random _rng;
    private readonly List<ResourceNode> _food = new();
    private readonly List<AgentState> _agents = new();
    private readonly Map _map;
    private readonly PathFinder _pathFinder;
    private float _accumulator;
    private int _nextAgentId;

    public IReadOnlyList<AgentState> Agents => _agents;
    public IReadOnlyList<ResourceNode> Food => _food;
    public Rectangle Shelter { get; }
    public Map Map => _map;
    public int FoodStockpile { get; private set; } = 30;
    public int Births { get; private set; }
    public int Deaths { get; private set; }
    public long Tick { get; private set; }
    public int Seed { get; }
    public bool IsCatchingUp { get; private set; }

    public SimulationWorld(int seed, int initialPopulation = 2)
    {
        Seed = seed;
        _rng = new Random(seed);
        _map = new Map(Width, Height);
        _pathFinder = new PathFinder(_map);

        var shelterBounds = new Rectangle(Width / 2 - 9, Height / 2 - 6, 18, 12);
        Shelter = shelterBounds;
        _map.InitializeShelter(shelterBounds);

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
            MoveAgent(agent, dt);
            ResolveAction(agent);

            if (agent.Action == AgentAction.Resting && _map.IsStorage(agent.Cell))
                agent.Energy = MathF.Min(100, agent.Energy + dt * 1.5f);
        }

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
                    agent.Action = AgentAction.ReturningHome;
                    SetPathToNearestDoor(agent);
                }
                else
                {
                    agent.Action = AgentAction.SearchingFood;
                }
                break;

            case AgentAction.Resting:
                if (agent.RestTimer > 0)
                    break;

                if (agent.CarriedFood > 0)
                {
                    agent.Action = AgentAction.ReturningHome;
                    SetPathToNearestDoor(agent);
                }
                else if (agent.Energy < 55)
                {
                    agent.Action = AgentAction.SearchingFood;
                }
                else
                {
                    agent.Action = AgentAction.SearchingFood;
                }
                break;

            case AgentAction.SearchingFood:
                var foodTarget = FindNearestFood(agent.Cell, agent.Id);
                if (foodTarget.HasValue && SetFoodTarget(agent, foodTarget.Value))
                {
                    agent.Action = AgentAction.GoingToFood;
                }
                else
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
                        ReleaseFoodReservation(agent);
                        agent.Action = AgentAction.SearchingFood;
                    }
                }
                else if (agent.Path.Count == 0 || agent.PathIndex >= agent.Path.Count)
                {
                    ReleaseFoodReservation(agent);
                    agent.Action = AgentAction.SearchingFood;
                }
                break;

            case AgentAction.GatheringFood:
                var foodAtCell = FindFoodAt(agent.Cell);
                if (agent.CarriedFood >= 3 || agent.Energy < 65)
                {
                    ReleaseFoodReservation(agent);
                    agent.Action = AgentAction.ReturningHome;
                    SetPathToNearestDoor(agent);
                }
                else if (foodAtCell is not null && foodAtCell.Amount > 0)
                {
                    break;
                }
                else
                {
                    ReleaseFoodReservation(agent);
                    if (agent.CarriedFood > 0)
                    {
                        agent.Action = AgentAction.ReturningHome;
                        SetPathToNearestDoor(agent);
                    }
                    else
                    {
                        agent.Action = AgentAction.SearchingFood;
                    }
                }
                break;

            case AgentAction.CarryingFood:
            case AgentAction.ReturningHome:
                if (_map.IsDoor(agent.Cell))
                {
                    agent.Action = AgentAction.StoringFood;
                }
                else if (agent.Path.Count == 0 || agent.PathIndex >= agent.Path.Count)
                {
                    SetPathToNearestDoor(agent);
                }
                break;

            case AgentAction.StoringFood:
                if (!_map.IsDoor(agent.Cell))
                {
                    agent.Action = AgentAction.Resting;
                    agent.TargetCell = _map.FindRandomStorageCell(_rng);
                    SetPath(agent, agent.TargetCell);
                }
                break;
        }
    }

    private void MoveAgent(AgentState agent, float dt)
    {
        if (agent.MoveCooldown > 0)
            return;

        if (agent.Path.Count == 0 || agent.PathIndex >= agent.Path.Count)
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
        agent.MoveCooldown = agent.Action == AgentAction.ReturningHome ? 0.1f : 0.2f;
    }

    private void SetPath(AgentState agent, Point target)
    {
        agent.Path = _pathFinder.FindPath(agent.Cell, target);
        agent.PathIndex = 0;
        agent.TargetCell = target;
    }

    private void SetPathToNearestDoor(AgentState agent)
    {
        var door = _map.FindNearestDoor(agent.Cell);
        if (_map.IsDoor(door))
        {
            SetPath(agent, door);
        }
    }

    private void ResolveAction(AgentState agent)
    {
        if (agent.Action == AgentAction.GatheringFood)
        {
            var node = FindFoodAt(agent.Cell);
            if (node is not null && node.Amount > 0)
            {
                var gathered = Math.Min(1, node.Amount);
                node.Amount -= gathered;
                agent.CarriedFood += gathered;
                agent.Energy = MathF.Min(100, agent.Energy + 5);
                if (node.Amount <= 0)
                    ReleaseFoodReservation(agent);
            }
        }

        if (agent.Action == AgentAction.StoringFood && _map.IsDoor(agent.Cell) && agent.CarriedFood > 0)
        {
            FoodStockpile += agent.CarriedFood;
            agent.CarriedFood = 0;
            agent.Energy = MathF.Min(100, agent.Energy + MathF.Min(12, FoodStockpile * 0.15f));
            agent.Action = AgentAction.Resting;
            agent.RestTimer = 2f;
            var storageCell = _map.FindNearestStorage(agent.Cell);
            if (_map.IsStorage(storageCell))
            {
                agent.TargetCell = storageCell;
                SetPath(agent, storageCell);
            }
        }
    }

    private void TryBirth()
    {
        var alive = _agents.Count(a => a.Alive);
        if (alive >= 250 || FoodStockpile < alive / 2 || _rng.NextDouble() > 0.012)
            return;

        var parents = _agents.Where(a => a.Alive && _map.IsStorage(a.Cell) && a.Energy > 70)
            .Take(2).ToArray();
        if (parents.Length < 2)
            return;

        FoodStockpile = Math.Max(0, FoodStockpile - 2);
        var spawnCell = _map.FindRandomStorageCell(_rng);
        _agents.Add(new AgentState
        {
            Id = _nextAgentId++,
            FactionId = parents[0].FactionId,
            Cell = spawnCell,
            TargetCell = spawnCell,
            Action = AgentAction.Resting,
            RestTimer = 2f,
        });
        Births++;
    }

    private void GenerateAgents(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var spawnCell = _map.FindRandomStorageCell(_rng);
            _agents.Add(new AgentState
            {
                Id = _nextAgentId++,
                FactionId = i < count / 2 ? 0 : 1,
                Cell = spawnCell,
                TargetCell = spawnCell,
                Action = AgentAction.SearchingFood,
            });
        }
    }

    private void GenerateFood()
    {
        for (var i = 0; i < 180; i++)
        {
            Point cell;
            int attempts = 0;
            do
            {
                cell = new Point(_rng.Next(4, Width - 4), _rng.Next(4, Height - 4));
                attempts++;
            } while (_map.IsWalkable(cell) == false && attempts < 100);

            if (_map.IsWalkable(cell) && !_map.IsStorage(cell) && !_map.IsDoor(cell))
            {
                _food.Add(new ResourceNode { Cell = cell, Amount = _rng.Next(2, 7) });
            }
        }
    }

    private void RegrowFood()
    {
        if (_food.Count(n => n.Amount > 0) >= 220)
            return;

        Point cell;
        int attempts = 0;
        do
        {
            cell = new Point(_rng.Next(4, Width - 4), _rng.Next(4, Height - 4));
            attempts++;
        } while ((_map.IsWalkable(cell) == false || _map.IsStorage(cell) || _map.IsDoor(cell)) && attempts < 100);

        if (_map.IsWalkable(cell) && !_map.IsStorage(cell) && !_map.IsDoor(cell))
        {
            _food.Add(new ResourceNode { Cell = cell, Amount = 4 });
        }
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
        return agent.Path.Count > 0;
    }

    private void ReleaseFoodReservation(AgentState agent)
    {
        if (!agent.HasFoodTarget)
            return;

        foreach (var node in _food)
            node.ReservedBy.Remove(agent.Id);
        agent.HasFoodTarget = false;
    }

    private Point? FindNearestFood(Point from, int agentId)
    {
        Point? best = null;
        var bestScore = int.MaxValue;
        foreach (var node in _food)
        {
            if (node.Amount <= 0 || !node.CanReserve(agentId))
                continue;

            var distance = Math.Abs(node.Cell.X - from.X) + Math.Abs(node.Cell.Y - from.Y);
            var reservationPenalty = node.ReservedBy.Contains(agentId) ? 0 : node.ReservedBy.Count * 12;
            var score = distance + reservationPenalty;
            if (score < bestScore)
            {
                best = node.Cell;
                bestScore = score;
            }
        }
        return best;
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
