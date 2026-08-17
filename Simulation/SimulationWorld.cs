using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;

namespace Seedfall.Simulation;

public sealed class ResourceNode
{
    public Vector2 Position { get; init; }
    public int Amount { get; set; }
}

public sealed class SimulationWorld
{
    public const int Width = 128;
    public const int Height = 80;
    public const float TickLength = 0.1f;

    private readonly Random _rng;
    private readonly List<ResourceNode> _food = new();
    private readonly List<AgentState> _agents = new();
    private float _accumulator;
    private int _nextAgentId;

    public IReadOnlyList<AgentState> Agents => _agents;
    public IReadOnlyList<ResourceNode> Food => _food;
    public Rectangle Shelter { get; } = new(Width / 2 - 9, Height / 2 - 6, 18, 12);
    public int FoodStockpile { get; private set; } = 30;
    public int Births { get; private set; }
    public int Deaths { get; private set; }
    public long Tick { get; private set; }
    public int Seed { get; }

    public SimulationWorld(int seed, int initialPopulation = 40)
    {
        Seed = seed;
        _rng = new Random(seed);
        GenerateFood();
        GenerateAgents(initialPopulation);
    }

    public void Advance(float realSeconds, float timeScale)
    {
        _accumulator += MathF.Min(realSeconds, 0.25f) * timeScale;
        var steps = 0;
        while (_accumulator >= TickLength && steps++ < 500)
        {
            Step(TickLength);
            _accumulator -= TickLength;
        }
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

            if (agent.Energy <= 0)
            {
                agent.Energy = 0;
                agent.Alive = false;
                agent.Action = "dead";
                Deaths++;
                continue;
            }

            if (agent.CarriedFood > 0 && (agent.CarriedFood >= 3 || agent.Energy < 65))
            {
                agent.Target = ShelterCenter();
                agent.Action = "returning";
            }
            else if (agent.Energy < 55)
            {
                var target = FindNearestFood(agent.Position);
                if (target is not null)
                {
                    agent.Target = target.Position;
                    agent.Action = "foraging";
                }
            }
            else if (agent.CarriedFood == 0 && Distance(agent.Position, ShelterCenter()) < 4)
            {
                var target = FindNearestFood(agent.Position);
                if (target is not null)
                {
                    agent.Target = target.Position;
                    agent.Action = "foraging";
                }
            }
            else if (Distance(agent.Position, ShelterCenter()) < 2)
            {
                agent.Action = "resting";
                agent.Target = ShelterCenter();
            }

            Move(agent, dt);
            ResolveAction(agent);
        }

        TryBirth();
        if (Tick % 50 == 0)
            RegrowFood();
    }

    private void Move(AgentState agent, float dt)
    {
        var delta = agent.Target - agent.Position;
        if (delta.LengthSquared() < 0.04f)
            return;

        delta.Normalize();
        var speed = agent.Action == "returning" ? 3.4f : 2.5f;
        agent.Position += delta * speed * dt;
        agent.Position = new Vector2(
            MathHelper.Clamp(agent.Position.X, 1, Width - 2),
            MathHelper.Clamp(agent.Position.Y, 1, Height - 2));
    }

    private void ResolveAction(AgentState agent)
    {
        if (agent.Action == "foraging")
        {
            var node = FindNearestFood(agent.Position, 1.1f);
            if (node is not null && node.Amount > 0)
            {
                var gathered = Math.Min(1, node.Amount);
                node.Amount -= gathered;
                agent.CarriedFood += gathered;
            }
        }

        if (agent.CarriedFood > 0 && InsideShelter(agent.Position))
        {
            FoodStockpile += agent.CarriedFood;
            agent.CarriedFood = 0;
            agent.Energy = MathF.Min(100, agent.Energy + MathF.Min(12, FoodStockpile * 0.15f));
            agent.Action = "resting";
        }
    }

    private void TryBirth()
    {
        var alive = _agents.Count(a => a.Alive);
        if (alive >= 250 || FoodStockpile < alive / 2 || _rng.NextDouble() > 0.012)
            return;

        var parents = _agents.Where(a => a.Alive && InsideShelter(a.Position) && a.Energy > 70)
            .Take(2).ToArray();
        if (parents.Length < 2)
            return;

        FoodStockpile = Math.Max(0, FoodStockpile - 2);
        var center = ShelterCenter();
        _agents.Add(new AgentState
        {
            Id = _nextAgentId++,
            FactionId = parents[0].FactionId,
            Position = center + new Vector2((float)(_rng.NextDouble() - 0.5), (float)(_rng.NextDouble() - 0.5)),
            Target = center,
            Action = "newborn",
        });
        Births++;
    }

    private void GenerateAgents(int count)
    {
        var center = ShelterCenter();
        for (var i = 0; i < count; i++)
        {
            var position = center + new Vector2(
                (float)(_rng.NextDouble() * 8 - 4),
                (float)(_rng.NextDouble() * 5 - 2.5));
            _agents.Add(new AgentState
            {
                Id = _nextAgentId++,
                FactionId = i < count / 2 ? 0 : 1,
                Position = position,
                Target = center,
            });
        }
    }

    private void GenerateFood()
    {
        for (var i = 0; i < 180; i++)
        {
            var position = new Vector2(_rng.Next(4, Width - 4), _rng.Next(4, Height - 4));
            if (InsideShelter(position))
                continue;
            _food.Add(new ResourceNode { Position = position, Amount = _rng.Next(2, 7) });
        }
    }

    private void RegrowFood()
    {
        if (_food.Count(n => n.Amount > 0) >= 220)
            return;
        var position = new Vector2(_rng.Next(4, Width - 4), _rng.Next(4, Height - 4));
        if (!InsideShelter(position))
            _food.Add(new ResourceNode { Position = position, Amount = 4 });
    }

    private ResourceNode? FindNearestFood(Vector2 position, float maxDistance = float.MaxValue)
    {
        ResourceNode? best = null;
        var bestDistance = maxDistance;
        foreach (var node in _food)
        {
            if (node.Amount <= 0)
                continue;
            var distance = Distance(position, node.Position);
            if (distance < bestDistance)
            {
                best = node;
                bestDistance = distance;
            }
        }
        return best;
    }

    public Vector2 ShelterCenter() => new(Shelter.Center.X, Shelter.Center.Y);
    public bool InsideShelter(Vector2 position) => Shelter.Contains(position.ToPoint());
    private static float Distance(Vector2 a, Vector2 b) => Vector2.Distance(a, b);
}
