using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;

namespace Hollowbound.Simulation;

public sealed class WorldSnapshot
{
    // v12: explicit anti-replay state (once-event types, generation milestones)
    //      so Chronicle guards survive ring-buffer eviction.
    // v13: First Cycle - Resonance, intervention queue, active effects, score, intervention log
    // v14: durable score counters and remaining temporary Bloom food.
    // v15: local Shout v0 state and counters.
    // v16: per-agent trust and outcome-based Shout learning.
    // v17: bounded per-source reputation memory.
    // v18: bounded multi-signal memory and faction signal reliability.
    // v19: Passage intervention command (older readers must not silently drop it).
    // v20: colony ecology, local birth accounting and persistent drought phases.
    // v21: contextual drought-risk telemetry and intervention outcome counters.
    // v22: observed first-impact events and attribution for player-opened passages.
    // v23: persist Insight clue provenance and route-selection outcomes.
    // v24: causal Beacon/Insight arrival and harvest attribution.
    public const int CurrentVersion = 24;
    public ColonyEcologyState? Ecology { get; set; }

    /// <summary>First save version carrying the full determinism block.</summary>
    public const int DeterminismBlockVersion = 11;
    public const int AntiReplayVersion = 12;
    public const int FirstCycleVersion = 13;
    public const int FirstCycleScoreCountersVersion = 14;
    public const int InterventionOutcomeAttributionVersion = 24;

    public int Version { get; set; } = CurrentVersion;
    public int Seed { get; set; }
    public uint RandomState { get; set; }
    public long Tick { get; set; }
    public float Accumulator { get; set; }
    public float BirthCooldown { get; set; }
    public int NextAgentId { get; set; }
    public int FoodStockpile { get; set; }
    public int Births { get; set; }
    public int Deaths { get; set; }
    public int StarvationDeaths { get; set; }
    public int FoodConsumed { get; set; }
    public int FoodGathered { get; set; }
    public int FoodShared { get; set; }
    public int KnowledgeShared { get; set; }
    public int ResourceSurges { get; set; }
    public int ScarcityEvents { get; set; }
    public int MigrationWaves { get; set; }
    public string CurrentEvent { get; set; } = "quiet";
    public int EventTicksRemaining { get; set; }
    public int WallBlocksBuilt { get; set; }
    public int WallBlocksRemoved { get; set; }
    public bool LodEnabled { get; set; } = true;
    public byte[] MapCells { get; set; } = Array.Empty<byte>();
    public List<AgentSnapshot> Agents { get; set; } = new();
    public List<FactionSnapshot> Factions { get; set; } = new();
    public List<ResourceSnapshot> Food { get; set; } = new();
    public List<StorageSnapshot> FoodStorage { get; set; } = new();
    public List<WorldEventSnapshot> Chronicle { get; set; } = new();
    public List<ChunkSnapshot> Chunks { get; set; } = new();
    public List<SettlementSnapshot> Settlements { get; set; } = new();
    public int NextSettlementId { get; set; } = 0;
    public HashSet<int> RecordedSettlementMilestones { get; set; } = new();
    public Dictionary<int, byte> LastFactionGoal { get; set; } = new();

    // --- v11 determinism block ---------------------------------------------
    // Optional scheduler/cache state so a reloaded world continues bit-identically
    // to the original. Defaults replicate the legacy post-load values, so older
    // save files deserialize unchanged and keep their historical behaviour.
    public int EventCooldown { get; set; } = 900;
    public int DormantOffset { get; set; }
    public int KnowledgeShareOffset { get; set; }
    public int BuildingCandidatesOffset { get; set; } = 17;
    public int WorldEventOffset { get; set; } = 7;
    public int FactionCultureOffset { get; set; }
    public int FoodCrisisOffset { get; set; } = 13;
    public int ClusterCheckOffset { get; set; }
    public int SettlementUpdateOffset { get; set; } = 3;
    public int AggregatedLodOffset { get; set; }
    public int ExplorationGridOffset { get; set; }
    public int WallApproachGridOffset { get; set; }
    public int MigrationGridOffset { get; set; }
    public bool FoodCrisisActive { get; set; }

    // Cached pathfinding distance grids (Width*Height ints, little-endian).
    // They are consulted by exploration/wall-approach/migration decisions and
    // are refreshed only every 25-100 ticks, so a restored world needs the
    // exact cached values to stay deterministic until its next refresh.
    public byte[]? ExplorationDistanceGridData { get; set; }
    public byte[]? WallApproachDistanceGridData { get; set; }
    public byte[]? MigrationDistanceGridData { get; set; }

    // LOD telemetry counters as of the save moment. UpdateAgentLOD classifies
    // from pre-tick state inside Step, so recomputing after load can differ by
    // a hair; carrying the exact values keeps loaded worlds comparable.
    public int ActiveAgentCount { get; set; }
    public int DormantAgentCount { get; set; }

    // Chronicle anti-spam cooldowns (WorldEventType -> last recorded tick).
    // Without them a restored world would re-fire SourceDepleted /
    // NewClusterDetected immediately and desynchronize its chronicle.
    public Dictionary<int, long> EventCooldownTicks { get; set; } = new();

    // Chunks already announced as remote clusters; protects against repeat
    // announcements for chunks whose occupancy flickered around a check.
    public List<int> KnownClusterChunks { get; set; } = new();

    // --- v12 anti-replay block ----------------------------------------------
    // Once-only event types (WorldEventType values) that already fired. Without
    // this a type whose single chronicle entry was evicted from the 48-slot ring
    // buffer would fire again after load (e.g. FirstElder, whose only guard is
    // the once-set). Legacy saves re-derive what permanent counters allow.
    public List<int> OnceEventTypes { get; set; } = new();

    // Generation milestones already announced (every 5th generation at birth).
    public List<int> RecordedGenerations { get; set; } = new();

    // --- v13 First Cycle block -----------------------------------------------
    // Player intervention state for deterministic replay
    public int Resonance { get; set; } = 3;
    public long ResonanceRegenTick { get; set; } = 5000;
    public int TotalResonanceSpent { get; set; }
    public int SuccessfulInterventions { get; set; }
    public int FailedInterventions { get; set; }
    public List<InterventionCommandSnapshot> PendingInterventions { get; set; } = new();
    public List<InterventionLogEntrySnapshot> InterventionLog { get; set; } = new();
    public List<BloomEffectSnapshot> ActiveBlooms { get; set; } = new();
    public List<BeaconEffectSnapshot> ActiveBeacons { get; set; } = new();
    public long ScoreLastTick { get; set; }
    public float ScorePopulationComponent { get; set; }
    public float ScoreFoodComponent { get; set; }
    public float ScoreCrisisComponent { get; set; }
    public float ScoreSettlementComponent { get; set; }
    public float ScoreEfficiencyComponent { get; set; }
    public float ScoreTotal { get; set; }

    // --- v14 First Cycle score/bloom refinements ---------------------------
    // Chronicle is a bounded display history, so score must not derive crisis
    // history from it. These counters survive ring-buffer eviction and reloads.
    public int FoodCrisisCount { get; set; }
    public int FoodCrisisRecoveredCount { get; set; }
    public int ShoutsMade { get; set; }
    public int ShoutsHeard { get; set; }
    public int FoodShouts { get; set; }
    public int DangerShouts { get; set; }
    public int RallyShouts { get; set; }
    public int ShoutLearningEvents { get; set; }
    public int SuccessfulShoutLessons { get; set; }
    public int FailedShoutLessons { get; set; }
    public long BloomFoodHarvested { get; set; }
    public long BeaconExplorationStarts { get; set; }
    public long InsightAgentsTaught { get; set; }
    public long InsightFoodRoutesStarted { get; set; }
    public long PassageTraversals { get; set; }
    public long PlayerPassageTraversals { get; set; }
    public long BeaconArrivals { get; set; }
    public long InsightFoodArrivals { get; set; }
    public long InsightFoodHarvested { get; set; }
    // Passage cells are tracked for impact only when opening them connected
    // previously disconnected walkable regions.
    public List<PointSnapshot> PlayerPassageCells { get; set; } = new();
    public List<PointSnapshot> AnnouncedPassageImpactCells { get; set; } = new();
}

public sealed class PointSnapshot
{
    public int X { get; set; }
    public int Y { get; set; }

    public Point ToPoint() => new(X, Y);

    public static PointSnapshot From(Point point) => new() { X = point.X, Y = point.Y };
}

public sealed class ResourceSnapshot
{
    public PointSnapshot Cell { get; set; } = new();
    public int Amount { get; set; }
    public List<int> ReservedBy { get; set; } = new();
}

public sealed class FactionSnapshot
{
    public int Id { get; set; }
    public byte Goal { get; set; }
    public int Population { get; set; }
    public float AverageEnergy { get; set; }
    public float FoodFocus { get; set; }
    public float BuildFocus { get; set; }
    public float ExploreFocus { get; set; }
    public float Cohesion { get; set; }
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

    public static FactionSnapshot From(FactionState faction) => new()
    {
        Id = faction.Id,
        Goal = (byte)faction.Goal,
        Population = faction.Population,
        AverageEnergy = faction.AverageEnergy,
        FoodFocus = faction.FoodFocus,
        BuildFocus = faction.BuildFocus,
        ExploreFocus = faction.ExploreFocus,
        Cohesion = faction.Cohesion,
        TerritoryPressure = faction.TerritoryPressure,
        FoodStored = faction.FoodStored,
        FoodConsumed = faction.FoodConsumed,
        KnowledgeShared = faction.KnowledgeShared,
        FoodSignalReliability = faction.FoodSignalReliability,
        DangerSignalReliability = faction.DangerSignalReliability,
        RallySignalReliability = faction.RallySignalReliability,
        ShoutLearningEvents = faction.ShoutLearningEvents,
        Births = faction.Births,
        Deaths = faction.Deaths,
    };

    public FactionState ToFaction() => new()
    {
        Id = Id,
        Goal = (FactionGoal)Goal,
        Population = Math.Max(0, Population),
        AverageEnergy = Math.Max(0, AverageEnergy),
        FoodFocus = Math.Clamp(FoodFocus, 0, 1),
        BuildFocus = Math.Clamp(BuildFocus, 0, 1),
        ExploreFocus = Math.Clamp(ExploreFocus, 0, 1),
        Cohesion = Math.Clamp(Cohesion, 0, 1),
        TerritoryPressure = Math.Clamp(TerritoryPressure, 0, 1),
        FoodStored = Math.Max(0, FoodStored),
        FoodConsumed = Math.Max(0, FoodConsumed),
        KnowledgeShared = Math.Max(0, KnowledgeShared),
        FoodSignalReliability = Math.Clamp(FoodSignalReliability, 0, 1),
        DangerSignalReliability = Math.Clamp(DangerSignalReliability, 0, 1),
        RallySignalReliability = Math.Clamp(RallySignalReliability, 0, 1),
        ShoutLearningEvents = Math.Max(0, ShoutLearningEvents),
        Births = Math.Max(0, Births),
        Deaths = Math.Max(0, Deaths),
    };
}

public sealed class StorageSnapshot
{
    public PointSnapshot Cell { get; set; } = new();
    public int Amount { get; set; }
}

public sealed class ChunkSnapshot
{
    public int Cx { get; set; }
    public int Cy { get; set; }
    public int AgentCount { get; set; }
    public int FoodCount { get; set; }
    public int WallCount { get; set; }
    public float ActivityLevel { get; set; }
    public long LastUpdateTick { get; set; }
    public int Faction0Count { get; set; }
    public int Faction1Count { get; set; }
    public float BirthRate { get; set; }
    public float DeathRate { get; set; }
    public float MigrationPressure { get; set; }
    public int SettlementId { get; set; }
    public float AvgEnergy { get; set; }
    public int TotalFoodConsumed { get; set; }
    public int TotalFoodStored { get; set; }
    public int AggregatedPopulation { get; set; }
    public float AggregatedAvgEnergy { get; set; }
    public int AggregatedFoodStockpile { get; set; }
    public byte AggregatedFactionGoal { get; set; }
    public float AggregatedCohesion { get; set; }
    public long LastAggregatedUpdateTick { get; set; }
    public bool IsAggregated { get; set; }
}

public sealed class SettlementSnapshot
{
    public int Id { get; set; }
    public int FactionId { get; set; }
    public PointSnapshot CenterCell { get; set; } = new();
    public int Population { get; set; }
    public int WallCount { get; set; }
    public int FoodStored { get; set; }
    public float AverageEnergy { get; set; }
    public long FoundedTick { get; set; }
    public long LastActiveTick { get; set; }
    public int Generation { get; set; }
    public int ElderCount { get; set; }
    public float Cohesion { get; set; }
    public int Births { get; set; }
    public int Deaths { get; set; }
    public float TerritoryPressure { get; set; }
    public bool IsAbandoned { get; set; }
}

public sealed class AgentSnapshot
{
    public int Id { get; set; }
    public int FactionId { get; set; }
    public PointSnapshot Cell { get; set; } = new();
    public PointSnapshot Facing { get; set; } = new();
    public PointSnapshot TargetCell { get; set; } = new();
    public PointSnapshot FoodTargetCell { get; set; } = new();
    public bool HasFoodTarget { get; set; }
    public List<PointSnapshot> Path { get; set; } = new();
    public int PathIndex { get; set; }
    public float Energy { get; set; }
    public float Age { get; set; }
    public int CarriedFood { get; set; }
    public float MoveCooldown { get; set; }
    public float RestTimer { get; set; }
    public float BuildCooldown { get; set; }
    public float FeedingCooldown { get; set; }
    public float ExplorationCooldown { get; set; }
    public byte Role { get; set; }
    public float RoleExperience { get; set; }
    public PointSnapshot HomeWallCell { get; set; } = new();
    public bool HasHomeWall { get; set; }
    public PointSnapshot KnownFoodCell { get; set; } = new();
    public bool HasKnownFood { get; set; }
    public PointSnapshot InsightFoodCell { get; set; } = new();
    public bool HasInsightFoodClue { get; set; }
    public float FoodKnowledge { get; set; }
    public int SuccessfulFoodTrips { get; set; }
    public int FailedFoodTrips { get; set; }
    public int FoodEaten { get; set; }
    public int ExplorationTrips { get; set; }
    public int SharedMemories { get; set; }
    public bool HasInsightRouteAttribution { get; set; }
    public bool InsightRouteArrived { get; set; }
    public PointSnapshot InsightRouteCell { get; set; } = new();
    public bool HasBeaconTarget { get; set; }
    public PointSnapshot BeaconTargetCell { get; set; } = new();
    public PointSnapshot KnownDangerCell { get; set; } = new();
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
    public float FoodUtilityBias { get; set; }
    public float BuildUtilityBias { get; set; }
    public float ExploreUtilityBias { get; set; }
    public float RestUtilityBias { get; set; }
    public byte LastDecisionAction { get; set; }
    public float LastDecisionScore { get; set; }
    public int DecisionsMade { get; set; }
    public int LearningUpdates { get; set; }
    public int PositiveOutcomes { get; set; }
    public int NegativeOutcomes { get; set; }
    public float ShoutCooldown { get; set; }
    public int ShoutsMade { get; set; }
    public int ShoutsHeard { get; set; }
    public byte LastShoutType { get; set; }
    public long LastShoutTick { get; set; } = -1;
    public byte LastHeardShoutType { get; set; }
    public long LastHeardShoutTick { get; set; } = -1;
    public PointSnapshot LastHeardShoutCell { get; set; } = new();
    public float LastHeardShoutStrength { get; set; }
    public int LastHeardShoutSenderId { get; set; } = -1;
    public bool LastHeardShoutEvaluated { get; set; }
    public float ShoutFoodTrust { get; set; } = 0.5f;
    public float ShoutDangerTrust { get; set; } = 0.5f;
    public float ShoutRallyTrust { get; set; } = 0.5f;
    public int ShoutLearningEvents { get; set; }
    public int SuccessfulShoutLessons { get; set; }
    public int FailedShoutLessons { get; set; }
    public byte Action { get; set; }
    public bool Alive { get; set; }

    // Aging & generations
    public int Generation { get; set; } = 1;
    public int ParentId1 { get; set; } = -1;
    public int ParentId2 { get; set; } = -1;
    public float MaxAge { get; set; } = 36000f;
    public bool IsElder { get; set; } = false;
    public float ElderWisdomBonus { get; set; } = 0f;

    // Settlement
    public int SettlementId { get; set; } = -1;

    public List<ShoutReputationSnapshot>? ShoutReputations { get; set; }
    public List<ShoutMemorySnapshot>? ShoutMemories { get; set; }

    public static AgentSnapshot From(AgentState agent) => new()
    {
        Id = agent.Id,
        FactionId = agent.FactionId,
        Cell = PointSnapshot.From(agent.Cell),
        Facing = PointSnapshot.From(agent.Facing),
        TargetCell = PointSnapshot.From(agent.TargetCell),
        FoodTargetCell = PointSnapshot.From(agent.FoodTargetCell),
        HasFoodTarget = agent.HasFoodTarget,
        Path = agent.Path.Select(PointSnapshot.From).ToList(),
        PathIndex = agent.PathIndex,
        Energy = agent.Energy,
        Age = agent.Age,
        CarriedFood = agent.CarriedFood,
        MoveCooldown = agent.MoveCooldown,
        RestTimer = agent.RestTimer,
        BuildCooldown = agent.BuildCooldown,
        FeedingCooldown = agent.FeedingCooldown,
        ExplorationCooldown = agent.ExplorationCooldown,
        Role = (byte)agent.Role,
        RoleExperience = agent.RoleExperience,
        HomeWallCell = PointSnapshot.From(agent.HomeWallCell),
        HasHomeWall = agent.HasHomeWall,
        KnownFoodCell = PointSnapshot.From(agent.KnownFoodCell),
        HasKnownFood = agent.HasKnownFood,
        InsightFoodCell = PointSnapshot.From(agent.InsightFoodCell),
        HasInsightFoodClue = agent.HasInsightFoodClue,
        FoodKnowledge = agent.FoodKnowledge,
        SuccessfulFoodTrips = agent.SuccessfulFoodTrips,
        FailedFoodTrips = agent.FailedFoodTrips,
        FoodEaten = agent.FoodEaten,
        ExplorationTrips = agent.ExplorationTrips,
        SharedMemories = agent.SharedMemories,
        HasInsightRouteAttribution = agent.HasInsightRouteAttribution,
        InsightRouteArrived = agent.InsightRouteArrived,
        InsightRouteCell = PointSnapshot.From(agent.InsightRouteCell),
        HasBeaconTarget = agent.HasBeaconTarget,
        BeaconTargetCell = PointSnapshot.From(agent.BeaconTargetCell),
        KnownDangerCell = PointSnapshot.From(agent.KnownDangerCell),
        HasDangerMemory = agent.HasDangerMemory,
        DangerKnowledge = agent.DangerKnowledge,
        RouteKnowledge = agent.RouteKnowledge,
        BuildDrive = agent.BuildDrive,
        ExplorationDrive = agent.ExplorationDrive,
        RiskTolerance = agent.RiskTolerance,
        LearningRate = agent.LearningRate,
        Intelligence = agent.Intelligence,
        PlanningSkill = agent.PlanningSkill,
        SocialAwareness = agent.SocialAwareness,
        PreferredBuildDirection = agent.PreferredBuildDirection,
        FoodUtilityBias = agent.FoodUtilityBias,
        BuildUtilityBias = agent.BuildUtilityBias,
        ExploreUtilityBias = agent.ExploreUtilityBias,
        RestUtilityBias = agent.RestUtilityBias,
        LastDecisionAction = (byte)agent.LastDecisionAction,
        LastDecisionScore = agent.LastDecisionScore,
        DecisionsMade = agent.DecisionsMade,
        LearningUpdates = agent.LearningUpdates,
        PositiveOutcomes = agent.PositiveOutcomes,
        NegativeOutcomes = agent.NegativeOutcomes,
        ShoutCooldown = agent.ShoutCooldown,
        ShoutsMade = agent.ShoutsMade,
        ShoutsHeard = agent.ShoutsHeard,
        LastShoutType = (byte)agent.LastShoutType,
        LastShoutTick = agent.LastShoutTick,
        LastHeardShoutType = (byte)agent.LastHeardShoutType,
        LastHeardShoutTick = agent.LastHeardShoutTick,
        LastHeardShoutCell = PointSnapshot.From(agent.LastHeardShoutCell),
        LastHeardShoutStrength = agent.LastHeardShoutStrength,
        LastHeardShoutSenderId = agent.LastHeardShoutSenderId,
        LastHeardShoutEvaluated = agent.LastHeardShoutEvaluated,
        ShoutFoodTrust = agent.ShoutFoodTrust,
        ShoutDangerTrust = agent.ShoutDangerTrust,
        ShoutRallyTrust = agent.ShoutRallyTrust,
        ShoutLearningEvents = agent.ShoutLearningEvents,
        SuccessfulShoutLessons = agent.SuccessfulShoutLessons,
        FailedShoutLessons = agent.FailedShoutLessons,
        ShoutReputations = agent.ShoutReputations?.OrderBy(entry => entry.SenderId).Select(ShoutReputationSnapshot.From).ToList(),
        ShoutMemories = agent.ShoutMemories?.OrderBy(entry => entry.Type).ThenBy(entry => entry.Cell.Y)
            .ThenBy(entry => entry.Cell.X).ThenBy(entry => entry.SenderId).Select(ShoutMemorySnapshot.From).ToList(),
        Action = (byte)agent.Action,
        Alive = agent.Alive,
        // Aging & generations
        Generation = agent.Generation,
        ParentId1 = agent.ParentId1,
        ParentId2 = agent.ParentId2,
        MaxAge = agent.MaxAge,
        IsElder = agent.IsElder,
        ElderWisdomBonus = agent.ElderWisdomBonus,
        // Settlement
        SettlementId = agent.SettlementId,
    };

    public AgentState ToAgent() => new()
    {
        Id = Id,
        FactionId = FactionId,
        Cell = Cell.ToPoint(),
        Facing = Facing.ToPoint(),
        TargetCell = TargetCell.ToPoint(),
        FoodTargetCell = FoodTargetCell.ToPoint(),
        HasFoodTarget = HasFoodTarget,
        Path = Path.Select(point => point.ToPoint()).ToList(),
        PathIndex = PathIndex,
        Energy = Energy,
        Age = Age,
        CarriedFood = CarriedFood,
        MoveCooldown = MoveCooldown,
        RestTimer = RestTimer,
        BuildCooldown = BuildCooldown,
        FeedingCooldown = FeedingCooldown,
        ExplorationCooldown = ExplorationCooldown,
        Role = (AgentRole)Role,
        RoleExperience = RoleExperience,
        HomeWallCell = HomeWallCell.ToPoint(),
        HasHomeWall = HasHomeWall,
        KnownFoodCell = KnownFoodCell.ToPoint(),
        HasKnownFood = HasKnownFood,
        InsightFoodCell = InsightFoodCell.ToPoint(),
        HasInsightFoodClue = HasInsightFoodClue,
        FoodKnowledge = FoodKnowledge,
        SuccessfulFoodTrips = SuccessfulFoodTrips,
        FailedFoodTrips = FailedFoodTrips,
        FoodEaten = FoodEaten,
        ExplorationTrips = ExplorationTrips,
        SharedMemories = SharedMemories,
        HasInsightRouteAttribution = HasInsightRouteAttribution,
        InsightRouteArrived = InsightRouteArrived,
        InsightRouteCell = InsightRouteCell.ToPoint(),
        HasBeaconTarget = HasBeaconTarget,
        BeaconTargetCell = BeaconTargetCell.ToPoint(),
        KnownDangerCell = KnownDangerCell.ToPoint(),
        HasDangerMemory = HasDangerMemory,
        DangerKnowledge = DangerKnowledge,
        RouteKnowledge = RouteKnowledge,
        BuildDrive = BuildDrive,
        ExplorationDrive = ExplorationDrive,
        RiskTolerance = RiskTolerance,
        LearningRate = LearningRate,
        Intelligence = Intelligence,
        PlanningSkill = PlanningSkill,
        SocialAwareness = SocialAwareness,
        PreferredBuildDirection = PreferredBuildDirection,
        FoodUtilityBias = FoodUtilityBias,
        BuildUtilityBias = BuildUtilityBias,
        ExploreUtilityBias = ExploreUtilityBias,
        RestUtilityBias = RestUtilityBias,
        LastDecisionAction = (AgentAction)LastDecisionAction,
        LastDecisionScore = LastDecisionScore,
        DecisionsMade = DecisionsMade,
        LearningUpdates = LearningUpdates,
        PositiveOutcomes = PositiveOutcomes,
        NegativeOutcomes = NegativeOutcomes,
        ShoutCooldown = ShoutCooldown,
        ShoutsMade = ShoutsMade,
        ShoutsHeard = ShoutsHeard,
        LastShoutType = (ShoutType)LastShoutType,
        LastShoutTick = LastShoutTick,
        LastHeardShoutType = (ShoutType)LastHeardShoutType,
        LastHeardShoutTick = LastHeardShoutTick,
        LastHeardShoutCell = LastHeardShoutCell.ToPoint(),
        LastHeardShoutStrength = LastHeardShoutStrength,
        LastHeardShoutSenderId = LastHeardShoutSenderId,
        LastHeardShoutEvaluated = LastHeardShoutEvaluated,
        ShoutFoodTrust = ShoutFoodTrust,
        ShoutDangerTrust = ShoutDangerTrust,
        ShoutRallyTrust = ShoutRallyTrust,
        ShoutLearningEvents = ShoutLearningEvents,
        SuccessfulShoutLessons = SuccessfulShoutLessons,
        FailedShoutLessons = FailedShoutLessons,
        ShoutReputations = ShoutReputations?.OrderByDescending(entry => entry.LastSeenTick)
            .ThenBy(entry => entry.SenderId).Take(8).Select(entry => entry.ToReputation()).ToList(),
        ShoutMemories = ShoutMemories?.OrderByDescending(entry => entry.HeardTick)
            .ThenBy(entry => entry.Type).ThenBy(entry => entry.Cell.Y).ThenBy(entry => entry.Cell.X)
            .Take(6).Select(entry => entry.ToMemory()).ToList(),
        Action = (AgentAction)Action,
        Alive = Alive,
        // Aging & generations
        Generation = Generation,
        ParentId1 = ParentId1,
        ParentId2 = ParentId2,
        MaxAge = MaxAge,
        IsElder = IsElder,
        ElderWisdomBonus = ElderWisdomBonus,
        // Settlement
        SettlementId = SettlementId,
    };
}

public sealed class ShoutReputationSnapshot
{
    public int SenderId { get; set; }
    public float FoodTrust { get; set; } = 0.5f;
    public float DangerTrust { get; set; } = 0.5f;
    public float RallyTrust { get; set; } = 0.5f;
    public int HeardCount { get; set; }
    public int LearningEvents { get; set; }
    public long LastSeenTick { get; set; } = -1;

    public static ShoutReputationSnapshot From(ShoutReputation reputation) => new()
    {
        SenderId = reputation.SenderId,
        FoodTrust = reputation.FoodTrust,
        DangerTrust = reputation.DangerTrust,
        RallyTrust = reputation.RallyTrust,
        HeardCount = reputation.HeardCount,
        LearningEvents = reputation.LearningEvents,
        LastSeenTick = reputation.LastSeenTick,
    };

    public ShoutReputation ToReputation() => new()
    {
        SenderId = SenderId,
        FoodTrust = FoodTrust,
        DangerTrust = DangerTrust,
        RallyTrust = RallyTrust,
        HeardCount = HeardCount,
        LearningEvents = LearningEvents,
        LastSeenTick = LastSeenTick,
    };
}

public sealed class ShoutMemorySnapshot
{
    public int SenderId { get; set; } = -1;
    public int FactionId { get; set; } = -1;
    public PointSnapshot Cell { get; set; } = new();
    public byte Type { get; set; }
    public float Confidence { get; set; } = 0.5f;
    public float Strength { get; set; }
    public int SuccessfulOutcomes { get; set; }
    public int FailedOutcomes { get; set; }
    public long HeardTick { get; set; } = -1;
    public long LastOutcomeTick { get; set; } = -1;

    public static ShoutMemorySnapshot From(ShoutMemory memory) => new()
    {
        SenderId = memory.SenderId,
        FactionId = memory.FactionId,
        Cell = PointSnapshot.From(memory.Cell),
        Type = (byte)memory.Type,
        Confidence = memory.Confidence,
        Strength = memory.Strength,
        SuccessfulOutcomes = memory.SuccessfulOutcomes,
        FailedOutcomes = memory.FailedOutcomes,
        HeardTick = memory.HeardTick,
        LastOutcomeTick = memory.LastOutcomeTick,
    };

    public ShoutMemory ToMemory() => new()
    {
        SenderId = SenderId,
        FactionId = FactionId,
        Cell = Cell.ToPoint(),
        Type = (ShoutType)Type,
        Confidence = Math.Clamp(Confidence, 0f, 1f),
        Strength = Math.Clamp(Strength, 0f, 1f),
        SuccessfulOutcomes = Math.Max(0, SuccessfulOutcomes),
        FailedOutcomes = Math.Max(0, FailedOutcomes),
        HeardTick = HeardTick,
        LastOutcomeTick = LastOutcomeTick,
    };
}

public sealed class InterventionCommandSnapshot
{
    public byte Type { get; set; }
    public PointSnapshot Cell { get; set; } = new();
    public int Cost { get; set; }
    public long RequestedTick { get; set; }
}

public sealed class InterventionLogEntrySnapshot
{
    public long Tick { get; set; }
    public byte Type { get; set; }
    public PointSnapshot Cell { get; set; } = new();
    public int Cost { get; set; }
    public bool Success { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class BloomEffectSnapshot
{
    public PointSnapshot Cell { get; set; } = new();
    public int RemainingTicks { get; set; }
    public int BoostAmount { get; set; }
    public int OriginalAmount { get; set; }
    public int RemainingBoost { get; set; }
    public bool CreatedSource { get; set; }
    public int HarvestedUnits { get; set; }
    public bool ImpactAnnounced { get; set; }
}

public sealed class BeaconEffectSnapshot
{
    public PointSnapshot Cell { get; set; } = new();
    public int RemainingTicks { get; set; }
    public float Strength { get; set; }
    public int ExplorationTrips { get; set; }
    public bool ImpactAnnounced { get; set; }
}

public static class WorldSaveService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string DefaultPath
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root))
                root = AppContext.BaseDirectory;
            return Path.Combine(root, "Hollowbound", "saves", "autosave.json");
        }
    }

    public static void Save(EmergentSimulationWorld world, string path = "")
    {
        path = string.IsNullOrWhiteSpace(path) ? DefaultPath : path;
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Save path has no directory.");

        Directory.CreateDirectory(directory);
        var temporaryPath = path + ".tmp";
        var json = JsonSerializer.Serialize(world.CreateSnapshot(), JsonOptions);
        File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
        File.Move(temporaryPath, path, true);
    }

    public static EmergentSimulationWorld Load(string path = "")
    {
        path = string.IsNullOrWhiteSpace(path) ? DefaultPath : path;
        var json = File.ReadAllText(path, Encoding.UTF8);
        var snapshot = JsonSerializer.Deserialize<WorldSnapshot>(json, JsonOptions)
            ?? throw new InvalidDataException("Save file is empty.");
        return EmergentSimulationWorld.FromSnapshot(snapshot);
    }
}

internal sealed class SimulationRandom
{
    private uint _state;

    public SimulationRandom(int seed)
    {
        _state = (uint)seed;
        if (_state == 0)
            _state = 0xA341316Cu;
    }

    public uint State => _state;

    public void RestoreState(uint state)
    {
        _state = state == 0 ? 0xA341316Cu : state;
    }

    public int Next(int maxValue)
    {
        if (maxValue <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxValue));
        return (int)(NextUInt() % (uint)maxValue);
    }

    public int Next(int minValue, int maxValue)
    {
        if (minValue >= maxValue)
            throw new ArgumentOutOfRangeException(nameof(maxValue));
        return minValue + Next(maxValue - minValue);
    }

    public double NextDouble()
    {
        return (NextUInt() >> 8) * (1.0 / 16777216.0);
    }

    private uint NextUInt()
    {
        _state = unchecked(_state * 1664525u + 1013904223u);
        return _state;
    }
}
