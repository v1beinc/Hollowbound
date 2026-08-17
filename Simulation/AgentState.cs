using Microsoft.Xna.Framework;

namespace Hollowbound.Simulation;

public enum AgentAction : byte
{
    Idle = 0,
    SearchingFood = 1,
    GoingToFood = 2,
    GatheringFood = 3,
    CarryingFood = 4,
    ReturningHome = 5,
    StoringFood = 6,
    Resting = 7,
    Dead = 8,
}

public sealed class AgentState
{
    public int Id { get; init; }
    public int FactionId { get; set; }
    public Point Cell { get; set; }
    public Point TargetCell { get; set; }
    public List<Point> Path { get; set; } = new();
    public int PathIndex { get; set; }
    public float Energy { get; set; } = 100f;
    public float Age { get; set; }
    public int CarriedFood { get; set; }
    public float MoveCooldown { get; set; }
    public float RestTimer { get; set; }
    public AgentAction Action { get; set; } = AgentAction.Idle;
    public bool Alive { get; set; } = true;
}
