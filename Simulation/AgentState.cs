using Microsoft.Xna.Framework;

namespace Hollowbound.Simulation;

public enum AgentAction : byte
{
    Idle = 0,
    SearchingFood = 1,
    GoingToFood = 2,
    GatheringFood = 3,
    CarryingFood = 4,
    ReturningToWall = 5,
    ReturningHome = ReturningToWall,
    StoringFood = 6,
    Resting = 7,
    Building = 8,
    Dead = 9,
}

public sealed class AgentState
{
    public int Id { get; init; }
    public int FactionId { get; set; }
    public Point Cell { get; set; }
    public Point TargetCell { get; set; }
    public Point FoodTargetCell { get; set; }
    public bool HasFoodTarget { get; set; }
    public List<Point> Path { get; set; } = new();
    public int PathIndex { get; set; }
    public float Energy { get; set; } = 100f;
    public float Age { get; set; }
    public int CarriedFood { get; set; }
    public float MoveCooldown { get; set; }
    public float RestTimer { get; set; }
    public float BuildCooldown { get; set; }
    public Point HomeWallCell { get; set; }
    public bool HasHomeWall { get; set; }
    public Point KnownFoodCell { get; set; }
    public bool HasKnownFood { get; set; }
    public float FoodKnowledge { get; set; }
    public int SuccessfulFoodTrips { get; set; }
    public int FailedFoodTrips { get; set; }
    public AgentAction Action { get; set; } = AgentAction.Idle;
    public bool Alive { get; set; } = true;
}
