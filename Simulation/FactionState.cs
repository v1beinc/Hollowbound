using System;
using Microsoft.Xna.Framework;

namespace Hollowbound.Simulation;

public enum FactionGoal : byte
{
    Forage = 0,
    Build = 1,
    Explore = 2,
    Migrate = 3,
}

/// <summary>
/// A settlement represents a cluster of agents, walls, and food storage
/// that functions as a cohesive community. Settlements form organically
/// around concentrations of walls and agents.
/// </summary>
public sealed class SettlementState
{
    public int Id { get; set; }
    public int FactionId { get; set; }
    public Point CenterCell { get; set; }
    public int Population { get; set; }
    public int WallCount { get; set; }
    public int FoodStored { get; set; }
    public float AverageEnergy { get; set; }
    public long FoundedTick { get; set; }
    public long LastActiveTick { get; set; }
    public int Generation { get; set; } = 1;
    public int ElderCount { get; set; }
    public float Cohesion { get; set; } = 0.5f;
    public int Births { get; set; }
    public int Deaths { get; set; }
    public float TerritoryPressure { get; set; }
    public bool IsAbandoned { get; set; } = false;
}

/// <summary>
/// Compact collective memory for a faction. It is derived from the behaviour
/// of its living members and then fed back into their next decisions.
/// </summary>
public sealed class FactionState
{
    public int Id { get; init; }
    public FactionGoal Goal { get; set; } = FactionGoal.Forage;
    public int Population { get; set; }
    public float AverageEnergy { get; set; }
    public float FoodFocus { get; set; } = 0.5f;
    public float BuildFocus { get; set; } = 0.5f;
    public float ExploreFocus { get; set; } = 0.5f;
    public float Cohesion { get; set; } = 0.5f;
    public float TerritoryPressure { get; set; }
    public int FoodStored { get; set; }
    public int FoodConsumed { get; set; }
    public int KnowledgeShared { get; set; }
    public float FoodSignalReliability { get; set; } = 0.5f;
    public float DangerSignalReliability { get; set; } = 0.5f;
    public float RallySignalReliability { get; set; } = 0.5f;
    public int ShoutLearningEvents { get; set; }
    public int Births { get; set; }
    public int Deaths { get; set; }
}
