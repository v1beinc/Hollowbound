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
    Digging = 10,
    Exploring = 11,
    Migrating = 12,
}

public enum AgentRole : byte
{
    Generalist = 0,
    Forager = 1,
    Builder = 2,
    Scout = 3,
    Keeper = 4,
    Pathfinder = 5,
}

public sealed class AgentState
{
    public int Id { get; init; }
    public int FactionId { get; set; }
    public Point Cell { get; set; }
    public Point Facing { get; set; } = new(1, 0);
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
    public float FeedingCooldown { get; set; }
    public float ExplorationCooldown { get; set; }
    public AgentRole Role { get; set; } = AgentRole.Generalist;
    public float RoleExperience { get; set; }
    public Point HomeWallCell { get; set; }
    public bool HasHomeWall { get; set; }
    public Point KnownFoodCell { get; set; }
    public bool HasKnownFood { get; set; }
    public float FoodKnowledge { get; set; }
    public int SuccessfulFoodTrips { get; set; }
    public int FailedFoodTrips { get; set; }
    public int FoodEaten { get; set; }
    public int ExplorationTrips { get; set; }
    public int SharedMemories { get; set; }
    public Point KnownDangerCell { get; set; }
    public bool HasDangerMemory { get; set; }
    public float DangerKnowledge { get; set; }
    public float RouteKnowledge { get; set; }
    public float BuildDrive { get; set; }
    public float ExplorationDrive { get; set; }
    public float RiskTolerance { get; set; }
    public float LearningRate { get; set; }
    public float Intelligence { get; set; }
    public float PlanningSkill { get; set; }
    public float SocialAwareness { get; set; }
    public int PreferredBuildDirection { get; set; }

    // Compact individual policy memory. These are not neural-network weights:
    // each agent simply learns which action categories paid off in its own
    // environment. The small state is cheap enough to keep for large worlds
    // and makes behaviour diverge instead of replaying one global script.
    public float FoodUtilityBias { get; set; }
    public float BuildUtilityBias { get; set; }
    public float ExploreUtilityBias { get; set; }
    public float RestUtilityBias { get; set; }
    public AgentAction LastDecisionAction { get; set; } = AgentAction.Idle;
    public float LastDecisionScore { get; set; }
    public int DecisionsMade { get; set; }
    public int LearningUpdates { get; set; }
    public int PositiveOutcomes { get; set; }
    public int NegativeOutcomes { get; set; }
    public float ShoutCooldown { get; set; }
    public int ShoutsMade { get; set; }
    public int ShoutsHeard { get; set; }
    public ShoutType LastShoutType { get; set; }
    public long LastShoutTick { get; set; } = -1;
    public ShoutType LastHeardShoutType { get; set; }
    public long LastHeardShoutTick { get; set; } = -1;
    public Point LastHeardShoutCell { get; set; }
    public float LastHeardShoutStrength { get; set; }
    public int LastHeardShoutSenderId { get; set; } = -1;
    public bool LastHeardShoutEvaluated { get; set; }
    public float ShoutFoodTrust { get; set; } = 0.5f;
    public float ShoutDangerTrust { get; set; } = 0.5f;
    public float ShoutRallyTrust { get; set; } = 0.5f;
    public int ShoutLearningEvents { get; set; }
    public int SuccessfulShoutLessons { get; set; }
    public int FailedShoutLessons { get; set; }
    public List<ShoutReputation>? ShoutReputations { get; set; }
    public List<ShoutMemory>? ShoutMemories { get; set; }
    public AgentAction Action { get; set; } = AgentAction.Idle;
    public bool Alive { get; set; } = true;

    // For incremental spatial index - tracks previous cell to detect movement
    public Point PreviousCell { get; set; }

    // Aging & generations
    public int Generation { get; set; } = 1;
    public int ParentId1 { get; set; } = -1;
    public int ParentId2 { get; set; } = -1;
    public float MaxAge { get; set; } = 3600f; // simulation seconds: 36,000 ticks; existing saves retain their lifespan
    public bool IsElder { get; set; } = false;
    public float ElderWisdomBonus { get; set; } = 0f; // passed to children

    // Settlement
    public int SettlementId { get; set; } = -1;
}
