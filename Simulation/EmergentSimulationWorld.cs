using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Xna.Framework;

namespace Hollowbound.Simulation;

public sealed partial class EmergentSimulationWorld
{
    public const int Width = 128;
    public const int Height = 80;
    public const float TickLength = 0.1f;
    public const int MaxStepsPerFrame = 20;
    public const float MaxBacklogSeconds = 2f;
    public const double MaxSimulationMillisecondsPerFrame = 5d;
    public const float WallBuildEnergyCost = 22f;
    public const float FoodEnergyValue = 22f;
    public const float FeedingInterval = 3.5f;
    // A settlement is made from single blocks, but it must be able to grow into
    // long avenues instead of stopping at the old prototype-sized ceiling.
    // Gameplay limit: maximum wall cells. Scales with population naturally.
// For very large populations, this will be replaced by chunk-based wall limits.
    public const int MaxWallCells = 1200;

    // Safety limit: absolute maximum population to prevent runaway simulation.
    // This is NOT a gameplay limit - it's an emergency guard. Real population
    // is regulated by food, energy, space, and mortality.
    public const int PopulationSafetyLimit = 100000;

    private readonly SimulationRandom _rng;
    private readonly List<ResourceNode> _food = new();
    private readonly Dictionary<Point, ResourceNode> _foodByCell = new();
    private readonly List<AgentState> _agents = new();
    private readonly List<FactionState> _factions = new();
    private readonly Dictionary<Point, int> _foodStorage = new();
    private readonly AgentSpatialIndex _agentIndex = new();
    private readonly ResourceSpatialIndex _resourceIndex = new();
    private readonly WallSpatialIndex _wallIndex;
    private readonly WorldChunks _chunks;
    private readonly Map _map;
    private readonly PathFinder _pathFinder;
    private float _accumulator;
    private int _nextAgentId;
    private float _birthCooldown;
    private int _eventCooldown = 900;

    // Time-slicing counters for heavy systems
    // Initialized with different phases to avoid sync spikes
    private int _knowledgeShareOffset = 0;
    private int _buildingCandidatesOffset = 17;  // Phase shift
    private int _worldEventOffset = 7;          // Phase shift

    // Batched multi-source pathfinding distance grids
    // These are computed periodically for exploration, wall approach, and migration
    private int[]? _explorationDistanceGrid;
    private int[]? _wallApproachDistanceGrid;
    private int[]? _migrationDistanceGrid;
    private int _explorationGridOffset = 0;
    private int _wallApproachGridOffset = 0;
    private int _migrationGridOffset = 0;
    private int _factionCultureOffset = 0;

    // LOD (Level of Detail) population management
    // Active agents: full simulation every tick
    // Dormant agents: aggregated, updated less frequently
    // Aggregated chunks: statistical simulation only
    private enum AgentLOD : byte
    {
        Active = 0,    // Full per-tick simulation
        Dormant = 1,   // Aggregated stats, updated every N ticks
        Aggregated = 2 // Chunk-level statistics only
    }
    private readonly Dictionary<int, AgentLOD> _agentLodById = new();
    private const int ActiveAgentThreshold = 100;
    private const int ActiveAgentBudgetLimit = 100;
    private const int DormantUpdateInterval = 10;
    private int _dormantOffset = 0;
    private float _aggregatedFoodConsumptionPerAgent = 0.6f;

    // Chronicle: bounded history of significant world events.
    // Events are recorded only on real state changes; one-shot flags and
    // tick cooldowns prevent spam. The list is capped so memory stays flat.
    private const int ChronicleMaxEvents = 48;
    private readonly List<WorldEvent> _chronicle = new(ChronicleMaxEvents);
    private readonly List<WorldEvent> _pendingChronicleEvents = new(8);
    private readonly List<ShoutSignal> _recentShouts = new(32);
    private const int ShoutIntervalTicks = 10;
    private const int ShoutVisualLifetimeTicks = 18;
    private const int MaxShoutsPerTick = 8;
    private const int MaxShoutReputationsPerAgent = 8;
    private const int MaxShoutMemoriesPerAgent = 6;
    private const long ShoutMemoryLifetimeTicks = 3600;

    // True while a world is being reconstructed from a snapshot. Reconstruction
    // (culture rebuild, settlement reassignment) is bookkeeping, not world
    // activity - nothing it records may enter the Chronicle or pending queue.
    private bool _suppressChronicleRecording;
    private readonly HashSet<WorldEventType> _onceEvents = new();
    private readonly Dictionary<WorldEventType, long> _eventCooldownTicks = new();
    private readonly Dictionary<int, FactionGoal> _lastFactionGoal = new();
    private int _lastWallMilestoneIndex;
    private bool _foodCrisisActive;
    private readonly HashSet<int> _knownClusterChunks = new();
    private int _clusterCheckOffset = 0;
    private int _foodCrisisOffset = 13;  // Phase shift, decoupled from regrow/culture
    private int _settlementUpdateOffset = 3;  // Phase shift for settlement updates
    private int _aggregatedLodOffset = 0;  // Phase shift for aggregated LOD updates

    private static readonly int[] WallMilestones = { 32, 64, 128, 256, 512, 1024 };

    // ========== First Cycle: Player Intervention Types ==========
    /// <summary>Type of player intervention.</summary>
    public enum InterventionType : byte
    {
        None = 0,
        Bloom = 1,         // Temporary food source boost
        Beacon = 2,        // Attraction signal
        InsightPulse = 3,  // Knowledge sharing boost
        Passage = 4,      // Open one wall cell, protecting a permanent route
    }

    /// <summary>Command queued by player, applied at tick boundary.</summary>
    public sealed class InterventionCommand
    {
        public InterventionType Type { get; set; }
        public Point Cell { get; set; }
        public int Cost { get; set; }
        public long RequestedTick { get; set; }
    }

    /// <summary>Result of an applied intervention (logged for replay/analytics).</summary>
    public sealed class InterventionLogEntry
    {
        public long Tick { get; set; }
        public InterventionType Type { get; set; }
        public Point Cell { get; set; }
        public int Cost { get; set; }
        public bool Success { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>Active Bloom effect - temporary food boost at a cell.</summary>
    public sealed class BloomEffect
    {
        public Point Cell { get; set; }
        public int RemainingTicks { get; set; }
        public int BoostAmount { get; set; }
        public int OriginalAmount { get; set; }
        public int RemainingBoost { get; set; }
        public bool CreatedSource { get; set; }
        public int HarvestedUnits { get; set; }
        public bool ImpactAnnounced { get; set; }
    }

    /// <summary>Active Beacon effect - attraction signal at a cell.</summary>
    public sealed class BeaconEffect
    {
        public Point Cell { get; set; }
        public int RemainingTicks { get; set; }
        public float Strength { get; set; }
        public int ExplorationTrips { get; set; }
        public bool ImpactAnnounced { get; set; }
    }

    // Settlement system
    private int _nextSettlementId = 0;
    private readonly Dictionary<int, SettlementState> _settlements = new();
    private readonly HashSet<int> _recordedSettlementMilestones = new();  // Track population milestones per settlement

    // ========== First Cycle: Player Interventions ==========
    // Resonance: limited player resource for interventions
    private int _resonance = 3;
    private long _resonanceRegenTick = 5000; // regain 1 Resonance every 5000 ticks

    // Intervention command queue - applied at tick boundary for determinism
    private readonly Queue<InterventionCommand> _pendingInterventions = new();
    private readonly List<InterventionLogEntry> _interventionLog = new();

    // Active intervention effects
    private readonly Dictionary<Point, BloomEffect> _activeBlooms = new();
    private readonly Dictionary<Point, BeaconEffect> _activeBeacons = new();

    // Score tracking
    private long _scoreLastTick = 0;
    private float _scorePopulationComponent = 0f;
    private float _scoreFoodComponent = 0f;
    private float _scoreCrisisComponent = 0f;
    private float _scoreSettlementComponent = 0f;
    private float _scoreEfficiencyComponent = 0f;
    private float _scoreTotal = 0f;
    private int _totalResonanceSpent = 0;
    private int _successfulInterventions = 0;
    private int _failedInterventions = 0;
    private long _bloomFoodHarvested;
    private long _beaconExplorationStarts;
    private long _beaconArrivals;
    private long _insightAgentsTaught;
    private long _insightFoodRoutesStarted;
    private long _insightFoodArrivals;
    private long _insightFoodHarvested;
    private long _passageTraversals;
    private long _playerPassageTraversals;
    private readonly HashSet<Point> _playerPassageCells = new();
    private readonly HashSet<Point> _announcedPassageImpactCells = new();
    private int _foodCrisisCount;
    private int _foodCrisisRecoveredCount;
    private int _shoutsMade;
    private int _shoutsHeard;
    private readonly int[] _shoutTypeCounts = new int[3];
    private int _shoutLearningEvents;
    private int _successfulShoutLessons;
    private int _failedShoutLessons;
    private readonly StepPerformanceProfiler _stepPerformanceProfiler = new();

    public IReadOnlyList<AgentState> Agents => _agents;
    public IReadOnlyList<FactionState> Factions => _factions;
    public IReadOnlyList<ResourceNode> Food => _food;
    public IReadOnlyDictionary<Point, int> FoodStorage => _foodStorage;
    public Map Map => _map;
    public int FoodStockpile { get; private set; } = 18;
    public int Births { get; private set; }
    public int Deaths { get; private set; }
    public int StarvationDeaths { get; private set; }
    public int FoodConsumed { get; private set; }
    public int FoodGathered { get; private set; }
    public int FoodShared { get; private set; }
    public int KnowledgeShared { get; private set; }
    public int ResourceSurges { get; private set; }
    public int ScarcityEvents { get; private set; }
    public int MigrationWaves { get; private set; }
    public string CurrentEvent { get; private set; } = "quiet";
    public int EventTicksRemaining { get; private set; }
    public int WallBlocksBuilt { get; private set; }
    public int WallBlocksRemoved { get; private set; }
    public long Tick { get; private set; }
    public int Seed { get; }
    public bool IsCatchingUp { get; private set; }
    public bool LodEnabled { get; private set; }
    public int ActiveAgentCount { get; private set; }
    public int DormantAgentCount { get; private set; }
    public int AggregatedAgentCount { get; private set; }
    public int ActiveAgentBudget => ActiveAgentBudgetLimit;
    public long SpatialQueryCount => _agentIndex.QueryCount;
    public long FoodQueryCount => _resourceIndex.QueryCount;
    public long WallQueryCount => _wallIndex.QueryCount;
    public long PathfindingRequests => _pathFinder.PathRequests;
    public long PathfindingCacheHits => _pathFinder.PathCacheHits;
    public long DistanceGridRequests => _pathFinder.DistanceGridRequests;
    public int LastStepsProcessed { get; private set; }
    public double LastSimulationMilliseconds { get; private set; }
    public StepPerformanceSummary StepPerformance => _stepPerformanceProfiler.GetSummary();
    public double LastEffectiveSimulationSpeed { get; private set; }
    public long CatchingUpFrames { get; private set; }
    public double CatchingUpSeconds { get; private set; }

    /// <summary>
    /// Wall-clock budget for simulation inside one Advance call. The interactive
    /// default stays far below a 60 Hz frame so pause, input and rendering stay
    /// responsive at every speed; headless/benchmark hosts raise it explicitly
    /// for throughput. It bounds how many ticks run per call - never their
    /// content or order.
    /// </summary>
    public const float DefaultInteractiveFrameBudgetMs = 6f;
    private double _frameBudgetMs = DefaultInteractiveFrameBudgetMs;

    public double FrameSimulationBudgetMilliseconds
    {
        get => _frameBudgetMs;
        set => _frameBudgetMs = double.IsFinite(value) && value > 0
            ? value
            : DefaultInteractiveFrameBudgetMs;
    }

    /// <summary>Simulation seconds still owed by the world to the requested speed. Bounded by GetMaxBacklogSeconds.</summary>
    public float BacklogSeconds => _accumulator;

    /// <summary>Smoothed actual speed (sim-seconds per real second) for stable HUD display.</summary>
    public float SmoothedActualSpeed { get; private set; }
    public int CatchingUpEpisodes { get; private set; }
    public double CurrentCatchingUpForSeconds { get; private set; }
    public double LongestCatchingUpEpisodeSeconds { get; private set; }

    // Dead agent cleanup - deferred removal to avoid modifying list during iteration
    private readonly List<AgentState> _deadAgents = new();
    private int _deadAgentsRemovedThisTick;
    public int DeadAgentsRemovedThisTick => _deadAgentsRemovedThisTick;

    // Births that happened during the most recent completed tick. Newborns are
    // classified by the next UpdateAgentLOD, so exactly this many alive agents
    // may legitimately lack a LOD entry between ticks.
    private int _birthsThisTick;
    public int BirthsThisTick => _birthsThisTick;

    // Cached statistics - refreshed once per simulation tick
    private int _cachedAlivePopulation;
    private int _cachedLowEnergyAgents;
    private int _cachedActiveBuilders;
    private int _cachedActiveExplorers;
    private float _cachedAverageEnergy;
    private float _cachedAverageIntelligence;
    private float _cachedAverageLearning;
    private int _cachedForagers;
    private int _cachedBuilders;
    private int _cachedScouts;
    private int _cachedKeepers;
    private int _cachedPathfinders;
    private int _cachedDecisions;
    private int _cachedLearningUpdates;
    private int _cachedPositiveOutcomes;
    private int _cachedNegativeOutcomes;
    private float _cachedAverageFoodBias;
    private float _cachedAverageBuildBias;
    private float _cachedAverageExploreBias;
    private bool _statsDirty = true;

    public int StoredFoodUnits => _foodStorage.Values.Sum();
    public int AlivePopulation => _cachedAlivePopulation;
    public int LowEnergyAgents => _cachedLowEnergyAgents;
    public int ActiveBuilders => _cachedActiveBuilders;
    public int ActiveExplorers => _cachedActiveExplorers;
    public float AverageEnergy => _cachedAverageEnergy;
    public float AverageIntelligence => _cachedAverageIntelligence;
    public float AverageLearning => _cachedAverageLearning;
    public int Foragers => _cachedForagers;
    public int Builders => _cachedBuilders;
    public int Scouts => _cachedScouts;
    public int Keepers => _cachedKeepers;
    public int Pathfinders => _cachedPathfinders;
    public int DecisionsMade => _cachedDecisions;
    public int LearningUpdates => _cachedLearningUpdates;
    public int PositiveOutcomes => _cachedPositiveOutcomes;
    public int NegativeOutcomes => _cachedNegativeOutcomes;
    public float AverageFoodPolicyBias => _cachedAverageFoodBias;
    public float AverageBuildPolicyBias => _cachedAverageBuildBias;
    public float AverageExplorePolicyBias => _cachedAverageExploreBias;

    public WorldChunks Chunks => _chunks;
    public int ActiveChunkCount => _chunks.ActiveChunkCount;

    /// <summary>
    /// Alive agents with no LOD classification yet. Between ticks this is at
    /// most the number of agents born during the last tick (they are classified
    /// by the next UpdateAgentLOD); anything larger is a bookkeeping defect.
    /// </summary>
    public int UnclassifiedAliveAgents
    {
        get
        {
            var count = 0;
            foreach (var agent in _agents)
            {
                if (agent.Alive && !_agentLodById.ContainsKey(agent.Id))
                    count++;
            }
            return count;
        }
    }

    public IReadOnlyList<WorldEvent> Chronicle => _chronicle;
    public int ChronicleCount => _chronicle.Count;
    public bool FoodCrisisActive => _foodCrisisActive;

    public int SettlementCount => _settlements.Count;

    // Read-only views for headless regression tests and analytics hosts.
    public IReadOnlyDictionary<int, SettlementState> Settlements => _settlements;
    public int NextSettlementId => _nextSettlementId;
    public IReadOnlyCollection<int> RecordedSettlementMilestones => _recordedSettlementMilestones;
    public uint RandomState => _rng.State;

    /// <summary>Once-only event types that already fired (anti-replay guard).</summary>
    public IReadOnlyCollection<WorldEventType> FiredOnceEvents => _onceEvents;

    /// <summary>Chronicle anti-spam cooldowns: event type -> last recorded tick.</summary>
    public IReadOnlyDictionary<WorldEventType, long> EventCooldowns => _eventCooldownTicks;

    /// <summary>Chunks already announced as remote clusters.</summary>
    public IReadOnlyCollection<int> KnownClusterChunks => _knownClusterChunks;

    // ========== First Cycle: Player Interventions - Public API ==========
    /// <summary>Current Resonance available for interventions.</summary>
    public int Resonance => _resonance;
    public int AvailableResonance => Math.Max(0, _resonance - _pendingInterventions.Sum(command => command.Cost));

    /// <summary>Total Resonance spent this experiment.</summary>
    public int TotalResonanceSpent => _totalResonanceSpent;

    /// <summary>Number of successful interventions.</summary>
    public int SuccessfulInterventions => _successfulInterventions;

    /// <summary>Number of failed interventions.</summary>
    public int FailedInterventions => _failedInterventions;
    public long BloomFoodHarvested => _bloomFoodHarvested;
    public long BeaconExplorationStarts => _beaconExplorationStarts;
    public long BeaconArrivals => _beaconArrivals;
    public long InsightAgentsTaught => _insightAgentsTaught;
    public long InsightFoodRoutesStarted => _insightFoodRoutesStarted;
    public long InsightFoodArrivals => _insightFoodArrivals;
    public long InsightFoodHarvested => _insightFoodHarvested;
    public long PassageTraversals => _passageTraversals;
    public long PlayerPassageTraversals => _playerPassageTraversals;
    public int ShoutsMade => _shoutsMade;
    public int ShoutsHeard => _shoutsHeard;
    public int FoodShouts => _shoutTypeCounts[(int)ShoutType.Food];
    public int DangerShouts => _shoutTypeCounts[(int)ShoutType.Danger];
    public int RallyShouts => _shoutTypeCounts[(int)ShoutType.Rally];
    public int ShoutLearningEvents => _shoutLearningEvents;
    public int SuccessfulShoutLessons => _successfulShoutLessons;
    public int FailedShoutLessons => _failedShoutLessons;
    public int ShoutReputationRecords
    {
        get
        {
            var count = 0;
            foreach (var agent in _agents)
                count += agent.ShoutReputations?.Count ?? 0;
            return count;
        }
    }
    public int ShoutMemoryRecords
    {
        get
        {
            var count = 0;
            foreach (var agent in _agents)
                count += agent.ShoutMemories?.Count ?? 0;
            return count;
        }
    }
    public int FactionShoutLearningEvents
    {
        get
        {
            var count = 0;
            foreach (var faction in _factions)
                count += faction.ShoutLearningEvents;
            return count;
        }
    }
    public IReadOnlyList<ShoutSignal> RecentShouts => _recentShouts;

    /// <summary>Provisional, non-competitive Resilient Settlement score; not leaderboard-ready.</summary>
    public float ScorePopulationComponent => _scorePopulationComponent;
    public float ScoreFoodComponent => _scoreFoodComponent;
    public float ScoreCrisisComponent => _scoreCrisisComponent;
    public float ScoreSettlementComponent => _scoreSettlementComponent;
    public float ScoreEfficiencyComponent => _scoreEfficiencyComponent;
    public float ScoreTotal => _scoreTotal;

    /// <summary>Intervention log (applied interventions with results).</summary>
    public IReadOnlyList<InterventionLogEntry> InterventionLog => _interventionLog;

    /// <summary>Active Bloom effects.</summary>
    public IReadOnlyDictionary<Point, BloomEffect> ActiveBlooms => _activeBlooms;

    /// <summary>Active Beacon effects.</summary>
    public IReadOnlyDictionary<Point, BeaconEffect> ActiveBeacons => _activeBeacons;

    /// <summary>
    /// Returns events recorded since the last drain and clears the pending list.
    /// Used by UI/logging hosts (Game1, headless, benchmark) so each event is
    /// written to the JSONL log exactly once.
    /// </summary>
    public IReadOnlyList<WorldEvent> DrainNewChronicleEvents()
    {
        if (_pendingChronicleEvents.Count == 0)
            return Array.Empty<WorldEvent>();

        var result = _pendingChronicleEvents.ToArray();
        _pendingChronicleEvents.Clear();
        return result;
    }

    private void RecordEvent(WorldEventType type, string description,
        WorldEventImportance importance, int factionId = -1, Point? cell = null)
    {
        if (_suppressChronicleRecording)
            return;

        var worldEvent = new WorldEvent(Tick, type, description, importance, factionId, cell);
        _chronicle.Add(worldEvent);
        _pendingChronicleEvents.Add(worldEvent);
        if (_chronicle.Count > ChronicleMaxEvents)
            _chronicle.RemoveAt(0);
    }

    /// <summary>
    /// True if a one-shot event type has never been recorded. After recording
    /// it is added to _onceEvents so it can never fire again.
    /// </summary>
    private bool CanRecordOnce(WorldEventType type)
    {
        return !_onceEvents.Contains(type);
    }

    private void MarkRecordedOnce(WorldEventType type)
    {
        _onceEvents.Add(type);
    }

    private bool CanRecordCooldown(WorldEventType type, long cooldownTicks)
    {
        return !_eventCooldownTicks.TryGetValue(type, out var lastTick) ||
               Tick - lastTick >= cooldownTicks;
    }

    private void MarkRecordedCooldown(WorldEventType type)
    {
        _eventCooldownTicks[type] = Tick;
    }

    /// <summary>
    /// Food crisis state machine. Fires once when available food falls below
    /// a population-based reserve threshold, and once when it recovers with
    /// margin. Edge-triggered, so it cannot spam.
    /// </summary>
    private void CheckFoodCrisis()
    {
        var alive = AlivePopulation;
        if (alive <= 0)
            return;

        // FoodStockpile mirrors deposits; adding it counted the same food twice.
        var totalAvailable = StoredFoodUnits;
        var crisisThreshold = Math.Max(8, alive * 2);
        if (!_foodCrisisActive && totalAvailable < crisisThreshold)
        {
            _foodCrisisActive = true;
            _foodCrisisCount++;
            RecordEvent(WorldEventType.FoodCrisis,
                $"КРИЗИС ЕДЫ: запасы {totalAvailable} при населении {alive}",
                WorldEventImportance.Critical);
        }
        else if (_foodCrisisActive && totalAvailable >= crisisThreshold + 10)
        {
            _foodCrisisActive = false;
            _foodCrisisRecoveredCount++;
            RecordEvent(WorldEventType.FoodRecovered,
                $"Запасы восстановлены: {totalAvailable} еды при населении {alive}",
                WorldEventImportance.Major);
        }
    }

    /// <summary>
    /// Detects new remote agent clusters. A chunk with agents that has no
    /// other occupied chunk within Chebyshev distance 2 is a remote cluster.
    /// Each cluster is announced once; tracking resets when the chunk empties.
    /// </summary>
    private void CheckRemoteClusters()
    {
        var occupied = new List<int>(_chunks.TotalChunks);
        for (var cy = 0; cy < _chunks.ChunkHeight; cy++)
        {
            for (var cx = 0; cx < _chunks.ChunkWidth; cx++)
            {
                var chunk = _chunks.GetChunk(cx, cy);
                if (chunk.AgentCount > 0)
                    occupied.Add(cy * _chunks.ChunkWidth + cx);
            }
        }

        var stillOccupied = new HashSet<int>(occupied);
        _knownClusterChunks.RemoveWhere(index => !stillOccupied.Contains(index));

        foreach (var index in occupied)
        {
            if (_knownClusterChunks.Contains(index))
                continue;

            var cx = index % _chunks.ChunkWidth;
            var cy = index / _chunks.ChunkWidth;
            var hasNearbyCluster = false;
            foreach (var other in occupied)
            {
                if (other == index)
                    continue;

                var ox = other % _chunks.ChunkWidth;
                var oy = other / _chunks.ChunkWidth;
                if (Math.Max(Math.Abs(cx - ox), Math.Abs(cy - oy)) <= 2)
                {
                    hasNearbyCluster = true;
                    break;
                }
            }

            _knownClusterChunks.Add(index);
            if (hasNearbyCluster || !CanRecordCooldown(WorldEventType.NewClusterDetected, 300))
                continue;

            var origin = _chunks.ChunkToCellOrigin(cx, cy);
            RecordEvent(WorldEventType.NewClusterDetected,
                $"Обнаружен удалённый кластер агентов в районе ({origin.X},{origin.Y})",
                WorldEventImportance.Major, cell: origin);
            MarkRecordedCooldown(WorldEventType.NewClusterDetected);
        }
    }

    /// <summary>
    /// Seeds cluster tracking from current chunk occupancy so a freshly loaded
    /// world does not announce pre-existing clusters as new.
    /// </summary>
    private void RebuildClusterTracking()
    {
        _knownClusterChunks.Clear();
        for (var cy = 0; cy < _chunks.ChunkHeight; cy++)
        {
            for (var cx = 0; cx < _chunks.ChunkWidth; cx++)
            {
                var chunk = _chunks.GetChunk(cx, cy);
                if (chunk.AgentCount > 0)
                    _knownClusterChunks.Add(cy * _chunks.ChunkWidth + cx);
            }
        }
    }

    /// <summary>
    /// Restores anti-spam state from a loaded chronicle so previously recorded
    /// one-shot events, wall milestones, and the crisis flag do not re-fire.
    /// </summary>
    private void RestoreChronicleState()
    {
        foreach (var worldEvent in _chronicle)
        {
            switch (worldEvent.Type)
            {
                // Only types whose recording path is genuinely once-guarded may
                // be derived here. MigrationStarted is cooldown-guarded and the
                // settlement events are per-settlement repeatable - marking them
                // as once would corrupt the anti-replay state on every load.
                case WorldEventType.FirstFoodGathered:
                case WorldEventType.FirstWallBuilt:
                case WorldEventType.FirstBirth:
                case WorldEventType.FirstDeath:
                case WorldEventType.FirstElder:
                    _onceEvents.Add(worldEvent.Type);
                    break;
                case WorldEventType.WallMilestone:
                    // Wall milestones are tracked by WallBlocksBuilt count
                    break;
                case WorldEventType.FoodCrisis:
                    _foodCrisisActive = true;
                    break;
                case WorldEventType.FoodRecovered:
                    _foodCrisisActive = false;
                    break;
            }
        }
        // Initialize wall milestone index based on current walls
        _lastWallMilestoneIndex = CountWallMilestonesPassed(WallBlocksBuilt);
    }

    private static int CountWallMilestonesPassed(int wallsBuilt)
    {
        var count = 0;
        foreach (var milestone in WallMilestones)
        {
            if (wallsBuilt >= milestone)
                count++;
        }

        return count;
    }

    private void RecordFirstDeath(AgentState agent)
    {
        if (Deaths != 1 || !CanRecordOnce(WorldEventType.FirstDeath))
            return;

        MarkRecordedOnce(WorldEventType.FirstDeath);
        RecordEvent(WorldEventType.FirstDeath,
            "Первая смерть агента",
            WorldEventImportance.Major, agent.FactionId, agent.Cell);
    }

    private static string FormatGoal(FactionGoal goal) => goal switch
    {
        FactionGoal.Forage => "добыча",
        FactionGoal.Build => "строительство",
        FactionGoal.Explore => "исследование",
        FactionGoal.Migrate => "миграция",
        _ => goal.ToString().ToLowerInvariant(),
    };

    public EmergentSimulationWorld(int seed, int initialPopulation = 2, bool enableLod = true)
        : this(seed, enableLod)
    {
        GenerateFood();
        GenerateAgents(initialPopulation);
        _agentIndex.Rebuild(_agents);
        _resourceIndex.Rebuild(_food);
        _wallIndex.Rebuild(_map.WallCells);
        UpdateFactionCulture();
        UpdateAgentLOD();
        RefreshStats();
    }

    private EmergentSimulationWorld(int seed, bool enableLod)
    {
        Seed = seed;
        LodEnabled = enableLod;
        _rng = new SimulationRandom(seed);
        _map = new Map(Width, Height);
        _map.InitializeOpen();
        _wallIndex = new WallSpatialIndex(Width, Height);
        _chunks = new WorldChunks(Width, Height);
        _pathFinder = new PathFinder(_map);
        EnsureFaction(0);
        EnsureFaction(1);

    }

    public WorldSnapshot CreateSnapshot()
    {
        return new WorldSnapshot
        {
            Ecology = Ecology.Copy(),
            Seed = Seed,
            RandomState = _rng.State,
            Tick = Tick,
            Accumulator = _accumulator,
            BirthCooldown = _birthCooldown,
            NextAgentId = _nextAgentId,
            FoodStockpile = FoodStockpile,
            Births = Births,
            Deaths = Deaths,
            StarvationDeaths = StarvationDeaths,
            FoodConsumed = FoodConsumed,
            FoodGathered = FoodGathered,
            FoodShared = FoodShared,
            KnowledgeShared = KnowledgeShared,
            ResourceSurges = ResourceSurges,
            ScarcityEvents = ScarcityEvents,
            MigrationWaves = MigrationWaves,
            CurrentEvent = CurrentEvent,
            EventTicksRemaining = EventTicksRemaining,
            WallBlocksBuilt = WallBlocksBuilt,
            WallBlocksRemoved = WallBlocksRemoved,
            LodEnabled = LodEnabled,
            MapCells = _map.ExportCells(),
            Agents = _agents.Select(AgentSnapshot.From).ToList(),
            Factions = _factions.Select(FactionSnapshot.From).ToList(),
            Food = _food.Select(node => new ResourceSnapshot
            {
                Cell = PointSnapshot.From(node.Cell),
                Amount = node.Amount,
                ReservedBy = node.ReservedBy.ToList(),
            }).ToList(),
            FoodStorage = _foodStorage.Select(pair => new StorageSnapshot
            {
                Cell = PointSnapshot.From(pair.Key),
                Amount = pair.Value,
            }).ToList(),
            Chronicle = _chronicle.Select(WorldEventSnapshot.From).ToList(),
            Chunks = _chunks.GetAllChunksSnapshot(),
            Settlements = _settlements.Values.Select(s => new SettlementSnapshot
            {
                Id = s.Id,
                FactionId = s.FactionId,
                CenterCell = PointSnapshot.From(s.CenterCell),
                Population = s.Population,
                WallCount = s.WallCount,
                FoodStored = s.FoodStored,
                AverageEnergy = s.AverageEnergy,
                FoundedTick = s.FoundedTick,
                LastActiveTick = s.LastActiveTick,
                Generation = s.Generation,
                ElderCount = s.ElderCount,
                Cohesion = s.Cohesion,
                Births = s.Births,
                Deaths = s.Deaths,
                TerritoryPressure = s.TerritoryPressure,
                IsAbandoned = s.IsAbandoned,
            }).ToList(),
            NextSettlementId = _nextSettlementId,
            RecordedSettlementMilestones = _recordedSettlementMilestones,
            LastFactionGoal = _lastFactionGoal.ToDictionary(kvp => kvp.Key, kvp => (byte)kvp.Value),

            // v11 determinism block: exact scheduler phases, event cooldown,
            // crisis flag and cached distance grids.
            EventCooldown = _eventCooldown,
            DormantOffset = _dormantOffset,
            KnowledgeShareOffset = _knowledgeShareOffset,
            BuildingCandidatesOffset = _buildingCandidatesOffset,
            WorldEventOffset = _worldEventOffset,
            FactionCultureOffset = _factionCultureOffset,
            FoodCrisisOffset = _foodCrisisOffset,
            ClusterCheckOffset = _clusterCheckOffset,
            SettlementUpdateOffset = _settlementUpdateOffset,
            AggregatedLodOffset = _aggregatedLodOffset,
            ExplorationGridOffset = _explorationGridOffset,
            WallApproachGridOffset = _wallApproachGridOffset,
            MigrationGridOffset = _migrationGridOffset,
            FoodCrisisActive = _foodCrisisActive,
            ExplorationDistanceGridData = SerializeIntGrid(_explorationDistanceGrid),
            WallApproachDistanceGridData = SerializeIntGrid(_wallApproachDistanceGrid),
            MigrationDistanceGridData = SerializeIntGrid(_migrationDistanceGrid),
            ActiveAgentCount = ActiveAgentCount,
            DormantAgentCount = DormantAgentCount,
            EventCooldownTicks = _eventCooldownTicks.ToDictionary(kv => (int)kv.Key, kv => kv.Value),
            KnownClusterChunks = _knownClusterChunks.ToList(),

            // v12 anti-replay state (sorted for stable files).
            OnceEventTypes = _onceEvents.Select(t => (int)t).OrderBy(t => t).ToList(),
            RecordedGenerations = _recordedGenerations.OrderBy(g => g).ToList(),

            // v13 First Cycle state
            Resonance = _resonance,
            ResonanceRegenTick = _resonanceRegenTick,
            TotalResonanceSpent = _totalResonanceSpent,
            SuccessfulInterventions = _successfulInterventions,
            FailedInterventions = _failedInterventions,
            PendingInterventions = _pendingInterventions.Select(c => new InterventionCommandSnapshot
            {
                Type = (byte)c.Type,
                Cell = PointSnapshot.From(c.Cell),
                Cost = c.Cost,
                RequestedTick = c.RequestedTick
            }).ToList(),
            InterventionLog = _interventionLog.Select(l => new InterventionLogEntrySnapshot
            {
                Tick = l.Tick,
                Type = (byte)l.Type,
                Cell = PointSnapshot.From(l.Cell),
                Cost = l.Cost,
                Success = l.Success,
                Reason = l.Reason
            }).ToList(),
            ActiveBlooms = _activeBlooms.Values
                .OrderBy(b => b.Cell.Y * Width + b.Cell.X)
                .Select(b => new BloomEffectSnapshot
            {
                Cell = PointSnapshot.From(b.Cell),
                RemainingTicks = b.RemainingTicks,
                BoostAmount = b.BoostAmount,
                OriginalAmount = b.OriginalAmount,
                RemainingBoost = b.RemainingBoost,
                CreatedSource = b.CreatedSource,
                HarvestedUnits = b.HarvestedUnits,
                ImpactAnnounced = b.ImpactAnnounced,
            }).ToList(),
            ActiveBeacons = _activeBeacons.Values
                .OrderBy(b => b.Cell.Y * Width + b.Cell.X)
                .Select(b => new BeaconEffectSnapshot
            {
                Cell = PointSnapshot.From(b.Cell),
                RemainingTicks = b.RemainingTicks,
                Strength = b.Strength,
                ExplorationTrips = b.ExplorationTrips,
                ImpactAnnounced = b.ImpactAnnounced,
            }).ToList(),
            ScoreLastTick = _scoreLastTick,
            ScorePopulationComponent = _scorePopulationComponent,
            ScoreFoodComponent = _scoreFoodComponent,
            ScoreCrisisComponent = _scoreCrisisComponent,
            ScoreSettlementComponent = _scoreSettlementComponent,
            ScoreEfficiencyComponent = _scoreEfficiencyComponent,
            ScoreTotal = _scoreTotal,
            FoodCrisisCount = _foodCrisisCount,
            FoodCrisisRecoveredCount = _foodCrisisRecoveredCount,
            ShoutsMade = _shoutsMade,
            ShoutsHeard = _shoutsHeard,
            FoodShouts = FoodShouts,
            DangerShouts = DangerShouts,
            RallyShouts = RallyShouts,
            ShoutLearningEvents = _shoutLearningEvents,
            SuccessfulShoutLessons = _successfulShoutLessons,
            FailedShoutLessons = _failedShoutLessons,
            BloomFoodHarvested = _bloomFoodHarvested,
            BeaconExplorationStarts = _beaconExplorationStarts,
            BeaconArrivals = _beaconArrivals,
            InsightAgentsTaught = _insightAgentsTaught,
            InsightFoodRoutesStarted = _insightFoodRoutesStarted,
            InsightFoodArrivals = _insightFoodArrivals,
            InsightFoodHarvested = _insightFoodHarvested,
            PassageTraversals = _passageTraversals,
            PlayerPassageTraversals = _playerPassageTraversals,
            PlayerPassageCells = _playerPassageCells.OrderBy(p => p.Y).ThenBy(p => p.X)
                .Select(PointSnapshot.From).ToList(),
            AnnouncedPassageImpactCells = _announcedPassageImpactCells.OrderBy(p => p.Y).ThenBy(p => p.X)
                .Select(PointSnapshot.From).ToList(),
        };
    }

    private static byte[]? SerializeIntGrid(int[]? grid)
    {
        if (grid == null || grid.Length != Width * Height)
            return null;
        var bytes = new byte[grid.Length * sizeof(int)];
        Buffer.BlockCopy(grid, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static int[]? DeserializeIntGrid(byte[]? data)
    {
        if (data == null || data.Length != Width * Height * sizeof(int))
            return null;
        var grid = new int[Width * Height];
        Buffer.BlockCopy(data, 0, grid, 0, data.Length);
        return grid;
    }

    public static EmergentSimulationWorld FromSnapshot(WorldSnapshot snapshot)
    {
        if (snapshot.Version < 2 || snapshot.Version > WorldSnapshot.CurrentVersion)
            throw new InvalidDataException($"Unsupported save version: {snapshot.Version}.");
        if (snapshot.MapCells.Length != Width * Height)
            throw new InvalidDataException("Save contains an invalid map size.");

        var world = new EmergentSimulationWorld(snapshot.Seed, snapshot.LodEnabled);
        world.Ecology = snapshot.Ecology?.ValidatedCopy() ?? new ColonyEcologyState { NextDroughtTick = snapshot.Tick + 3000 };
        world._suppressChronicleRecording = true;
        world._map.RestoreCells(snapshot.MapCells);
        if (snapshot.RandomState != 0)
            world._rng.RestoreState(snapshot.RandomState);
        world._agents.Clear();
        world._factions.Clear();
        world._food.Clear();
        world._foodByCell.Clear();
        world._foodStorage.Clear();

        foreach (var agent in snapshot.Agents)
        {
            var loadedAgent = agent.ToAgent();
            if (snapshot.Version < WorldSnapshot.InterventionOutcomeAttributionVersion)
            {
                // v10-v23 had no per-agent causal route markers. Do not infer
                // attribution from an old generic path or remembered clue.
                loadedAgent.HasInsightRouteAttribution = false;
                loadedAgent.InsightRouteArrived = false;
                loadedAgent.InsightRouteCell = Point.Zero;
                loadedAgent.HasBeaconTarget = false;
                loadedAgent.BeaconTargetCell = Point.Zero;
            }
            loadedAgent.PreviousCell = loadedAgent.Cell;
            world._agents.Add(loadedAgent);
        }

        foreach (var faction in snapshot.Factions)
        {
            if (faction.Id >= 0)
                world._factions.Add(faction.ToFaction());
        }
        world.EnsureFaction(0);
        world.EnsureFaction(1);

        foreach (var resource in snapshot.Food)
        {
            var node = new ResourceNode
            {
                Cell = resource.Cell.ToPoint(),
                Amount = resource.Amount,
            };
            foreach (var agentId in resource.ReservedBy.Distinct())
                node.ReservedBy.Add(agentId);
            world._food.Add(node);
            world._foodByCell[node.Cell] = node;
        }

        foreach (var storage in snapshot.FoodStorage)
            world._foodStorage[storage.Cell.ToPoint()] = storage.Amount;

        world._resourceIndex.Rebuild(world._food);

        // Restore chunks from snapshot (preserves aggregated LOD state)
        if (snapshot.Chunks != null && snapshot.Chunks.Count > 0)
        {
            world._chunks.RestoreFromSnapshot(snapshot.Chunks);
        }
        else
        {
            // Fallback: rebuild chunks from loaded agents and food (for older saves)
            world._chunks.Clear();
            foreach (var agent in world._agents)
            {
                if (agent.Alive)
                    world._chunks.RegisterAgent(agent.Cell, agent.FactionId);
            }
            foreach (var node in world._food)
            {
                if (node.Amount > 0)
                    world._chunks.RegisterFood(node.Cell);
            }
            foreach (var wall in world._map.WallCells)
            {
                world._chunks.RegisterWall(wall);
            }
        }

        world._agentIndex.Rebuild(world._agents);

        world._accumulator = Math.Clamp(snapshot.Accumulator, 0, MaxBacklogSeconds);
        world._birthCooldown = Math.Max(0, snapshot.BirthCooldown);
        world._nextAgentId = Math.Max(snapshot.NextAgentId, world._agents.Select(agent => agent.Id).DefaultIfEmpty(0).Max() + 1);
        world.FoodStockpile = Math.Max(0, snapshot.FoodStockpile);
        world.Births = Math.Max(0, snapshot.Births);
        world.Deaths = Math.Max(0, snapshot.Deaths);
        world.StarvationDeaths = Math.Max(0, snapshot.StarvationDeaths);
        world.FoodConsumed = Math.Max(0, snapshot.FoodConsumed);
        world.FoodGathered = Math.Max(0, snapshot.FoodGathered);
        world.FoodShared = Math.Max(0, snapshot.FoodShared);
        world.KnowledgeShared = Math.Max(0, snapshot.KnowledgeShared);
        world.ResourceSurges = Math.Max(0, snapshot.ResourceSurges);
        world.ScarcityEvents = Math.Max(0, snapshot.ScarcityEvents);
        world.MigrationWaves = Math.Max(0, snapshot.MigrationWaves);
        world._shoutsMade = Math.Max(0, snapshot.ShoutsMade);
        world._shoutsHeard = Math.Max(0, snapshot.ShoutsHeard);
        world._shoutTypeCounts[(int)ShoutType.Food] = Math.Max(0, snapshot.FoodShouts);
        world._shoutTypeCounts[(int)ShoutType.Danger] = Math.Max(0, snapshot.DangerShouts);
        world._shoutTypeCounts[(int)ShoutType.Rally] = Math.Max(0, snapshot.RallyShouts);
        world._shoutLearningEvents = Math.Max(0, snapshot.ShoutLearningEvents);
        world._successfulShoutLessons = Math.Max(0, snapshot.SuccessfulShoutLessons);
        world._failedShoutLessons = Math.Max(0, snapshot.FailedShoutLessons);
        world.CurrentEvent = string.IsNullOrWhiteSpace(snapshot.CurrentEvent) ? "quiet" : snapshot.CurrentEvent;
        world.EventTicksRemaining = Math.Max(0, snapshot.EventTicksRemaining);
        if (snapshot.Version >= WorldSnapshot.DeterminismBlockVersion)
        {
            // v11+ saves carry the exact scheduler phases and event cooldown so
            // a reloaded world consumes RNG on exactly the same ticks as the
            // original. Older saves keep the legacy reset behaviour.
            world._eventCooldown = Math.Max(0, snapshot.EventCooldown);
            world._dormantOffset = snapshot.DormantOffset;
            world._knowledgeShareOffset = snapshot.KnowledgeShareOffset;
            world._buildingCandidatesOffset = snapshot.BuildingCandidatesOffset;
            world._worldEventOffset = snapshot.WorldEventOffset;
            world._factionCultureOffset = snapshot.FactionCultureOffset;
            world._foodCrisisOffset = snapshot.FoodCrisisOffset;
            world._clusterCheckOffset = snapshot.ClusterCheckOffset;
            world._settlementUpdateOffset = snapshot.SettlementUpdateOffset;
            world._aggregatedLodOffset = snapshot.AggregatedLodOffset;
            world._explorationGridOffset = snapshot.ExplorationGridOffset;
            world._wallApproachGridOffset = snapshot.WallApproachGridOffset;
            world._migrationGridOffset = snapshot.MigrationGridOffset;
            world._explorationDistanceGrid = DeserializeIntGrid(snapshot.ExplorationDistanceGridData);
            world._wallApproachDistanceGrid = DeserializeIntGrid(snapshot.WallApproachDistanceGridData);
            world._migrationDistanceGrid = DeserializeIntGrid(snapshot.MigrationDistanceGridData);
            world._eventCooldownTicks.Clear();
            if (snapshot.EventCooldownTicks != null)
            {
                foreach (var kv in snapshot.EventCooldownTicks)
                    world._eventCooldownTicks[(WorldEventType)kv.Key] = kv.Value;
            }
        }
        else
        {
            world._eventCooldown = 900;
        }
        world.WallBlocksBuilt = Math.Max(0, snapshot.WallBlocksBuilt);
        world.WallBlocksRemoved = Math.Max(0, snapshot.WallBlocksRemoved);
        world.Tick = Math.Max(0, snapshot.Tick);
        world.IsCatchingUp = false;
        world._agentIndex.Rebuild(world._agents);
        world._resourceIndex.Rebuild(world._food);
        world._wallIndex.Rebuild(world._map.WallCells);

        // Restore Chronicle state FIRST so one-shots, wall milestones, food crisis,
        // and cluster tracking do not re-fire. Do this BEFORE UpdateFactionCulture()
        // which can generate new FactionGoalChanged events.
        if (snapshot.Chronicle is { Count: > 0 })
        {
            world._chronicle.AddRange(snapshot.Chronicle.Select(s => s.ToWorldEvent()));
            world.RestoreChronicleState();
            world._pendingChronicleEvents.Clear(); // Don't re-drain loaded events
        }
        if (snapshot.Version >= WorldSnapshot.DeterminismBlockVersion)
        {
            // The chronicle ring buffer can rotate the crisis-start event out,
            // so for current saves the exact flag wins over chronicle-derived state.
            world._foodCrisisActive = snapshot.FoodCrisisActive;
        }

        // Anti-replay state. v12+ carries it explicitly; older saves re-derive
        // as much as permanent counters allow (best effort, documented limit).
        if (snapshot.Version >= WorldSnapshot.AntiReplayVersion)
        {
            world._onceEvents.UnionWith(
                (snapshot.OnceEventTypes ?? new List<int>()).Select(t => (WorldEventType)t));
            world._recordedGenerations.UnionWith(snapshot.RecordedGenerations ?? new List<int>());
        }
        else
        {
            // A first-event whose guard is a cumulative counter hitting exactly 1
            // provably fired when that counter is past 1; seed those so eviction
            // from the ring buffer cannot resurrect them after load.
            if (world.FoodGathered > 0)
                world._onceEvents.Add(WorldEventType.FirstFoodGathered);
            if (world.WallBlocksBuilt > 0)
                world._onceEvents.Add(WorldEventType.FirstWallBuilt);
            if (world.Births > 0)
                world._onceEvents.Add(WorldEventType.FirstBirth);
            if (world.Deaths > 0)
                world._onceEvents.Add(WorldEventType.FirstDeath);
            if (world.MigrationWaves > 0)
                world._onceEvents.Add(WorldEventType.MigrationStarted);
            if (world._agents.Any(a => a.IsElder))
                world._onceEvents.Add(WorldEventType.FirstElder);

            // Generation milestones: any alive generation-N agent proves N was
            // reached and therefore announced at its birth. Generations reached
            // only by agents who already died are unrecoverable from legacy
            // saves - accepted, documented limitation.
            var maxGeneration = world._agents.Count > 0 ? world._agents.Max(a => a.Generation) : 1;
            for (var g = 5; g <= maxGeneration; g += 5)
                world._recordedGenerations.Add(g);
        }
        world.RebuildClusterTracking();
        if (snapshot.Version >= WorldSnapshot.DeterminismBlockVersion && snapshot.KnownClusterChunks != null)
        {
            // The rebuild approximates the set from current occupancy; current
            // saves carry the exact announced-cluster state of the original.
            world._knownClusterChunks.Clear();
            foreach (var chunkIndex in snapshot.KnownClusterChunks)
                world._knownClusterChunks.Add(chunkIndex);
        }

        // Restore settlements
        if (snapshot.Settlements != null && snapshot.Settlements.Count > 0)
        {
            world._settlements.Clear();
            foreach (var s in snapshot.Settlements)
            {
                var settlement = new SettlementState
                {
                    Id = s.Id,
                    FactionId = s.FactionId,
                    CenterCell = s.CenterCell.ToPoint(),
                    Population = s.Population,
                    WallCount = s.WallCount,
                    FoodStored = s.FoodStored,
                    AverageEnergy = s.AverageEnergy,
                    FoundedTick = s.FoundedTick,
                    LastActiveTick = s.LastActiveTick,
                    Generation = s.Generation,
                    ElderCount = s.ElderCount,
                    Cohesion = s.Cohesion,
                    Births = s.Births,
                    Deaths = s.Deaths,
                    TerritoryPressure = s.TerritoryPressure,
                    IsAbandoned = s.IsAbandoned,
                };
                world._settlements[s.Id] = settlement;
            }
            world._nextSettlementId = Math.Max(snapshot.NextSettlementId, world._settlements.Keys.DefaultIfEmpty(0).Max() + 1);
            world._recordedSettlementMilestones.Clear();
            if (snapshot.RecordedSettlementMilestones != null)
            {
                world._recordedSettlementMilestones.UnionWith(snapshot.RecordedSettlementMilestones);
            }
        }
        else
        {
            // Legacy saves carry no settlements; drop stale agent references so
            // state stays consistent until UpdateSettlements reassigns them.
            foreach (var agent in world._agents)
                agent.SettlementId = -1;
        }

        // Restore _lastFactionGoal to prevent re-firing FactionGoalChanged events
        if (snapshot.LastFactionGoal != null)
        {
            world._lastFactionGoal.Clear();
            foreach (var kvp in snapshot.LastFactionGoal)
            {
                world._lastFactionGoal[kvp.Key] = (FactionGoal)kvp.Value;
            }
        }

        // v13 First Cycle state
        if (snapshot.Version >= WorldSnapshot.FirstCycleVersion)
        {
            world._resonance = Math.Max(0, Math.Min(10, snapshot.Resonance));
            world._resonanceRegenTick = snapshot.ResonanceRegenTick > 0 ? snapshot.ResonanceRegenTick : 5000;
            world._totalResonanceSpent = Math.Max(0, snapshot.TotalResonanceSpent);
            world._successfulInterventions = Math.Max(0, snapshot.SuccessfulInterventions);
                world._failedInterventions = Math.Max(0, snapshot.FailedInterventions);
                if (snapshot.Version >= 21)
                {
                    world._bloomFoodHarvested = Math.Max(0, snapshot.BloomFoodHarvested);
                    world._beaconExplorationStarts = Math.Max(0, snapshot.BeaconExplorationStarts);
                    world._insightAgentsTaught = Math.Max(0, snapshot.InsightAgentsTaught);
                    if (snapshot.Version >= 23)
                        world._insightFoodRoutesStarted = Math.Max(0, snapshot.InsightFoodRoutesStarted);
                    world._passageTraversals = Math.Max(0, snapshot.PassageTraversals);
                    world._playerPassageTraversals = Math.Max(0, snapshot.PlayerPassageTraversals);
                    if (snapshot.Version >= WorldSnapshot.InterventionOutcomeAttributionVersion)
                    {
                        world._beaconArrivals = Math.Max(0, snapshot.BeaconArrivals);
                        world._insightFoodArrivals = Math.Max(0, snapshot.InsightFoodArrivals);
                        world._insightFoodHarvested = Math.Max(0, snapshot.InsightFoodHarvested);
                    }
                    world._playerPassageCells.Clear();
                    foreach (var cell in snapshot.PlayerPassageCells ?? new List<PointSnapshot>())
                    {
                        var point = cell.ToPoint();
                        if (world._map.InBounds(point) && world._map[point] == CellType.Door)
                            world._playerPassageCells.Add(point);
                    }
                    world._announcedPassageImpactCells.Clear();
                    foreach (var cell in snapshot.AnnouncedPassageImpactCells ?? new List<PointSnapshot>())
                    {
                        var point = cell.ToPoint();
                        if (world._playerPassageCells.Contains(point))
                            world._announcedPassageImpactCells.Add(point);
                    }
                }

            world._pendingInterventions.Clear();
            if (snapshot.PendingInterventions != null)
            {
                foreach (var cmd in snapshot.PendingInterventions)
                {
                    var type = (InterventionType)cmd.Type;
                    var cell = cmd.Cell?.ToPoint() ?? new Point(-1, -1);
                    var expectedCost = world.GetInterventionCost(type);
                    // Saves are user-editable JSON. Do not restore malformed
                    // commands that can mint Resonance, bypass bounds checks,
                    // or remain queued forever.
                    if (type is InterventionType.None || !Enum.IsDefined(type) || expectedCost <= 0 ||
                        cmd.Cost != expectedCost || !world._map.InBounds(cell) || world.Tick == long.MaxValue ||
                        cmd.RequestedTick != world.Tick + 1)
                        continue;

                    world._pendingInterventions.Enqueue(new InterventionCommand
                    {
                        Type = type,
                        Cell = cell,
                        Cost = expectedCost,
                        RequestedTick = cmd.RequestedTick
                    });
                }
            }

            world._interventionLog.Clear();
            if (snapshot.InterventionLog != null)
            {
                foreach (var log in snapshot.InterventionLog)
                {
                    world._interventionLog.Add(new InterventionLogEntry
                    {
                        Tick = log.Tick,
                        Type = (InterventionType)log.Type,
                        Cell = log.Cell.ToPoint(),
                        Cost = log.Cost,
                        Success = log.Success,
                        Reason = log.Reason ?? string.Empty
                    });
                }
            }

            world._activeBlooms.Clear();
            if (snapshot.ActiveBlooms != null)
            {
                foreach (var bloom in snapshot.ActiveBlooms)
                {
                    world._activeBlooms[bloom.Cell.ToPoint()] = new BloomEffect
                    {
                        Cell = bloom.Cell.ToPoint(),
                        RemainingTicks = bloom.RemainingTicks,
                        BoostAmount = bloom.BoostAmount,
                        OriginalAmount = bloom.OriginalAmount,
                        // v13 had no remaining-bonus accounting. Derive the
                        // conservative amount that still sits above the old
                        // baseline so legacy active Blooms do not refill food
                        // when they expire.
                        RemainingBoost = snapshot.Version >= WorldSnapshot.FirstCycleScoreCountersVersion
                            ? Math.Max(0, bloom.RemainingBoost)
                            : Math.Clamp(
                                world._foodByCell.TryGetValue(bloom.Cell.ToPoint(), out var node)
                                    ? node.Amount - bloom.OriginalAmount
                                    : 0,
                                0,
                                Math.Max(0, bloom.BoostAmount)),
                        CreatedSource = snapshot.Version >= WorldSnapshot.FirstCycleScoreCountersVersion && bloom.CreatedSource,
                        HarvestedUnits = snapshot.Version >= 21 ? Math.Max(0, bloom.HarvestedUnits) : 0,
                        ImpactAnnounced = snapshot.Version >= 22 && bloom.ImpactAnnounced,
                    };
                }
            }

            world._activeBeacons.Clear();
            if (snapshot.ActiveBeacons != null)
            {
                foreach (var beacon in snapshot.ActiveBeacons)
                {
                    world._activeBeacons[beacon.Cell.ToPoint()] = new BeaconEffect
                    {
                        Cell = beacon.Cell.ToPoint(),
                        RemainingTicks = beacon.RemainingTicks,
                        Strength = beacon.Strength,
                        ExplorationTrips = snapshot.Version >= 21 ? Math.Max(0, beacon.ExplorationTrips) : 0,
                        ImpactAnnounced = snapshot.Version >= 22 && beacon.ImpactAnnounced,
                    };
                }
            }

            world._scoreLastTick = snapshot.ScoreLastTick;
            world._scorePopulationComponent = snapshot.ScorePopulationComponent;
            world._scoreFoodComponent = snapshot.ScoreFoodComponent;
            world._scoreCrisisComponent = snapshot.ScoreCrisisComponent;
            world._scoreSettlementComponent = snapshot.ScoreSettlementComponent;
            world._scoreEfficiencyComponent = snapshot.ScoreEfficiencyComponent;
            world._scoreTotal = snapshot.ScoreTotal;

            if (snapshot.Version >= WorldSnapshot.FirstCycleScoreCountersVersion)
            {
                world._foodCrisisCount = Math.Max(0, snapshot.FoodCrisisCount);
                world._foodCrisisRecoveredCount = Math.Max(0, snapshot.FoodCrisisRecoveredCount);
            }
            else
            {
                // v13 persisted only the bounded Chronicle. This is the best
                // available reconstruction for an old save; v14 keeps exact
                // counters from this point forward.
                world._foodCrisisCount = world._chronicle.Count(e => e.Type == WorldEventType.FoodCrisis);
                world._foodCrisisRecoveredCount = world._chronicle.Count(e => e.Type == WorldEventType.FoodRecovered);
            }
        }

        if (snapshot.Version < WorldSnapshot.DeterminismBlockVersion)
        {
            // Legacy saves predate complete faction snapshots and carry no goal
            // baseline. Seed it from the restored goals so culture reconstruction
            // does not emit phantom FactionGoalChanged events; real later changes
            // are still recorded. Current saves already carry exact focus/cohesion
            // values; an extra smoothing pass would perturb them and break
            // deterministic continuation.
            foreach (var faction in world._factions)
                world._lastFactionGoal[faction.Id] = faction.Goal;
            world.UpdateFactionCulture();
        }
        world.UpdateAgentLOD();
        world.RefreshStats();

        if (snapshot.Version >= WorldSnapshot.DeterminismBlockVersion &&
            (snapshot.ActiveAgentCount > 0 || snapshot.DormantAgentCount > 0))
        {
            // UpdateAgentLOD just classified from post-tick state; current saves
            // carry the exact counters observed at save time instead. Files with
            // no counter data (truncated or minimal fixtures) keep the freshly
            // computed classification - zero overrides only when truly empty.
            world.ActiveAgentCount = Math.Max(0, snapshot.ActiveAgentCount);
            world.DormantAgentCount = Math.Max(0, snapshot.DormantAgentCount);
        }

        // AggregatedAgentCount is refreshed only inside UpdateAggregatedChunks
        // (every 200 ticks); recompute it from restored chunks so telemetry is
        // correct immediately after load and matches the original world.
        // Placed after UpdateAgentLOD(), which resets the counter to zero.
        world.AggregatedAgentCount = 0;
        for (var cy = 0; cy < world._chunks.ChunkHeight; cy++)
        {
            for (var cx = 0; cx < world._chunks.ChunkWidth; cx++)
            {
                var restored = world._chunks.GetChunk(cx, cy);
                if (restored.IsAggregated)
                    world.AggregatedAgentCount += restored.AggregatedPopulation;
            }
        }

        // Reconstruction must never surface as fresh chronicle activity.
        world._suppressChronicleRecording = false;
        world._pendingChronicleEvents.Clear();

        return world;
    }

    public void Advance(float realSeconds, float timeScale)
    {
        Advance(realSeconds, timeScale, int.MaxValue);
    }

    /// <summary>
    /// Advances the simulation while optionally limiting the number of ticks.
    /// The limit is used by deterministic headless checks so they report the
    /// requested tick count exactly instead of overshooting by one frame.
    /// </summary>
    public void Advance(float realSeconds, float timeScale, int maxTicks)
    {
        // Sanitize host input first: NaN, Infinity, zero, and negative speeds
        // must never poison the accumulator or the world. They are treated as
        // "paused": nothing accumulates, nothing runs, telemetry stays finite.
        var clampedRealSeconds = MathF.Max(0f, MathF.Min(realSeconds, 0.25f));
        if (!float.IsFinite(clampedRealSeconds))
            clampedRealSeconds = 0f;
        var effectiveTimeScale = float.IsFinite(timeScale) && timeScale > 0f ? timeScale : 0f;

        if (effectiveTimeScale <= 0f)
        {
            LastStepsProcessed = 0;
            LastSimulationMilliseconds = 0;
            IsCatchingUp = _accumulator >= TickLength; // leftover work stays visible
            UpdateCatchingUpTelemetry(clampedRealSeconds);
            return;
        }

        var maxBacklog = GetMaxBacklogSeconds(effectiveTimeScale);
        _accumulator = MathF.Min(
            _accumulator + clampedRealSeconds * effectiveTimeScale,
            maxBacklog);

        var stopwatch = Stopwatch.StartNew();
        var maxSteps = Math.Min(GetMaxStepsPerFrame(effectiveTimeScale), Math.Max(0, maxTicks));
        var steps = 0;
        while (_accumulator >= TickLength &&
               steps < maxSteps &&
               stopwatch.Elapsed.TotalMilliseconds < _frameBudgetMs)
        {
            Step(TickLength);
            _accumulator -= TickLength;
            steps++;
        }

        IsCatchingUp = _accumulator >= TickLength;
        LastStepsProcessed = steps;
        LastSimulationMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        if (clampedRealSeconds > 0)
        {
            // Raw actual speed for this call: sim-seconds produced per real second.
            LastEffectiveSimulationSpeed = steps * TickLength / clampedRealSeconds;

            // Smoothed actual speed for the HUD: exponential-style moving average
            // with a ~0.5 s time constant so single-frame spikes do not flicker.
            var instant = (float)LastEffectiveSimulationSpeed;
            var alpha = MathF.Min(1f, clampedRealSeconds / 0.5f);
            var blended = SmoothedActualSpeed + (instant - SmoothedActualSpeed) * alpha;
            SmoothedActualSpeed = SmoothedActualSpeed <= 0f ? instant : blended;
        }

        UpdateCatchingUpTelemetry(clampedRealSeconds);
    }

    private void UpdateCatchingUpTelemetry(float realSeconds)
    {
        if (IsCatchingUp)
        {
            CatchingUpFrames++;
            CatchingUpSeconds += realSeconds;
            CurrentCatchingUpForSeconds += realSeconds;
            if (CurrentCatchingUpForSeconds > LongestCatchingUpEpisodeSeconds)
                LongestCatchingUpEpisodeSeconds = CurrentCatchingUpForSeconds;
        }
        else if (CurrentCatchingUpForSeconds > 0)
        {
            CatchingUpEpisodes++;
            CurrentCatchingUpForSeconds = 0;
        }
    }

    public void ClearBacklog()
    {
        _accumulator = 0;
        IsCatchingUp = false;
        // Closing an open catch-up episode here keeps pause/reset/load from
        // inflating the sustained-catch-up statistics.
        UpdateCatchingUpTelemetry(0f);
    }

    private static float GetMaxBacklogSeconds(float timeScale) => timeScale switch
    {
        // Keep enough work queued for the requested speed. The old values
        // capped x100-x500 at only 5 simulation steps per frame, so x25 was
        // visibly faster than every higher setting.
        >= 500f => 6.4f,
        >= 250f => 4.8f,
        >= 100f => 3.2f,
        >= 50f => 2f,
        >= 25f => 1.2f,
        _ => MaxBacklogSeconds,
    };

    private static int GetMaxStepsPerFrame(float timeScale) => timeScale switch
    {
        // Secondary safety bound on top of the wall-clock frame budget; keeps
        // worst-case work per call predictable even if a single Step is cheap.
        >= 500f => 64,
        >= 250f => 48,
        >= 100f => 32,
        >= 50f => 20,
        >= 25f => 12,
        >= 10f => 12,
        >= 5f => 18,
        _ => MaxStepsPerFrame,
    };

    private void Step(float dt)
    {
        Tick++;
        var profileThisTick = StepPerformanceProfiler.ShouldSample(Tick);
        var stepStarted = profileThisTick ? Stopwatch.GetTimestamp() : 0;
        UpdateColonyEcology();

        // ========== First Cycle: Process pending interventions at tick boundary ==========
        ProcessPendingInterventions();

        // Regenerate Resonance
        if (Tick > 0 && Tick % _resonanceRegenTick == 0 && _resonance < 10)
        {
            _resonance = Math.Min(10, _resonance + 1);
        }

        // Update active intervention effects
        UpdateActiveInterventions();

        // Recalculate score periodically
        if (Tick - _scoreLastTick >= 100)
        {
            RecalculateScore();
            _scoreLastTick = Tick;
        }

        _deadAgentsRemovedThisTick = 0;
        _birthsThisTick = 0;

        // LOD: Update agent classification based on population
        UpdateAgentLOD();

        // LOD: Update dormant agents periodically
        if (++_dormantOffset >= DormantUpdateInterval)
        {
            _dormantOffset = 0;
            UpdateDormantAgents();
        }

        var agentLoopStarted = profileThisTick ? Stopwatch.GetTimestamp() : 0;
        foreach (var agent in _agents)
        {
            if (!agent.Alive)
                continue;

            // LOD: Skip detailed simulation for dormant/aggregated agents
            if (LodEnabled && _agentLodById.TryGetValue(agent.Id, out var lod))
            {
                if (lod is AgentLOD.Dormant or AgentLOD.Aggregated)
                {
                    // Simplified update for dormant/aggregated agents
                    agent.Age += dt;
                    agent.FeedingCooldown = MathF.Max(0, agent.FeedingCooldown - dt);
                    agent.ShoutCooldown = MathF.Max(0, agent.ShoutCooldown - dt);
                    TryConsumeStoredFood(agent);
                    agent.Energy -= dt * _aggregatedFoodConsumptionPerAgent;

                    // Aging for dormant agents
                    if (agent.Age >= agent.MaxAge)
                    {
                        ReleaseFoodReservation(agent);
                        agent.Energy = 0;
                        agent.Alive = false;
                        agent.Action = AgentAction.Dead;
                        _deadAgents.Add(agent);
                        Deaths++;
                        EnsureFaction(agent.FactionId).Deaths++;
                        RecordEvent(WorldEventType.ElderDeath,
                            $"Старейшина #{agent.Id} поколения {agent.Generation} умер от старости (возраст {agent.Age:0})",
                            WorldEventImportance.Minor, agent.FactionId, agent.Cell);
                        InvalidateStats();
                        continue;
                    }

                    // Elder transition for dormant agents
                    if (!agent.IsElder && agent.Age >= agent.MaxAge * 0.75f)
                    {
                        agent.IsElder = true;
                        var positiveBias = Math.Max(0, agent.FoodUtilityBias) + Math.Max(0, agent.BuildUtilityBias) +
                                           Math.Max(0, agent.ExploreUtilityBias) + Math.Max(0, agent.RestUtilityBias);
                        agent.ElderWisdomBonus = MathF.Min(0.15f, positiveBias * 0.05f);
                        if (CanRecordOnce(WorldEventType.FirstElder))
                        {
                            MarkRecordedOnce(WorldEventType.FirstElder);
                            RecordEvent(WorldEventType.FirstElder,
                                $"Старейшина #{agent.Id} поколения {agent.Generation} обрел мудрость",
                                WorldEventImportance.Major, agent.FactionId, agent.Cell);
                        }
                    }

                    if (agent.Energy <= 0)
                    {
                        ReleaseFoodReservation(agent);
                        agent.Energy = 0;
                        agent.Alive = false;
                        agent.Action = AgentAction.Dead;
                        _deadAgents.Add(agent);
                        Deaths++;
                        StarvationDeaths++;
                        EnsureFaction(agent.FactionId).Deaths++;
                        RecordFirstDeath(agent);
                        InvalidateStats();
                    }
                    continue;
                }
            }

            agent.Age += dt;
            agent.Energy -= dt * (agent.Action == AgentAction.Resting ? 0.45f : 0.6f);

            // Aging: check for old age death and elder transition
            if (agent.Age >= agent.MaxAge)
            {
                ReleaseFoodReservation(agent);
                agent.Energy = 0;
                agent.Alive = false;
                agent.Action = AgentAction.Dead;
                _deadAgents.Add(agent);
                Deaths++;
                EnsureFaction(agent.FactionId).Deaths++;
                RecordEvent(WorldEventType.ElderDeath,
                    $"Старейшина #{agent.Id} поколения {agent.Generation} умер от старости (возраст {agent.Age:0})",
                    WorldEventImportance.Minor, agent.FactionId, agent.Cell);
                InvalidateStats();
                continue;
            }

            // Elder transition: agents become elders at 75% of max age
            if (!agent.IsElder && agent.Age >= agent.MaxAge * 0.75f)
            {
                agent.IsElder = true;
                // Elder wisdom bonus: average of their positive biases, scaled
                var positiveBias = Math.Max(0, agent.FoodUtilityBias) + Math.Max(0, agent.BuildUtilityBias) +
                                   Math.Max(0, agent.ExploreUtilityBias) + Math.Max(0, agent.RestUtilityBias);
                agent.ElderWisdomBonus = MathF.Min(0.15f, positiveBias * 0.05f);

                // Chronicle: first elder
                if (CanRecordOnce(WorldEventType.FirstElder))
                {
                    MarkRecordedOnce(WorldEventType.FirstElder);
                    RecordEvent(WorldEventType.FirstElder,
                        $"Старейшина #{agent.Id} поколения {agent.Generation} обрел мудрость",
                        WorldEventImportance.Major, agent.FactionId, agent.Cell);
                }
            }

            agent.MoveCooldown = MathF.Max(0, agent.MoveCooldown - dt);
            agent.RestTimer = MathF.Max(0, agent.RestTimer - dt);
            agent.BuildCooldown = MathF.Max(0, agent.BuildCooldown - dt);
            agent.FeedingCooldown = MathF.Max(0, agent.FeedingCooldown - dt);
            agent.ExplorationCooldown = MathF.Max(0, agent.ExplorationCooldown - dt);
            agent.ShoutCooldown = MathF.Max(0, agent.ShoutCooldown - dt);
            agent.RoleExperience = MathF.Min(1f, agent.RoleExperience + dt * GetRoleExperienceRate(agent));
            agent.DangerKnowledge = MathF.Max(0, agent.DangerKnowledge - dt * 0.00008f);

            // Stored food is a real survival mechanic now. An agent first
            // checks a nearby pile, then uses the global reserve only when its
            // energy is critical and no local pile can be reached.
            TryConsumeStoredFood(agent);

            if (agent.Energy <= 0)
            {
                ReleaseFoodReservation(agent);
                agent.Energy = 0;
                agent.Alive = false;
                agent.Action = AgentAction.Dead;
                _deadAgents.Add(agent);
                Deaths++;
                StarvationDeaths++;
                EnsureFaction(agent.FactionId).Deaths++;
                RecordFirstDeath(agent);
                InvalidateStats();
                continue;
            }

            UpdateAgentState(agent);
            MoveAgent(agent);
            ResolveAction(agent);
            UpdateRoleFromExperience(agent);

            // Rest conserves energy; only consumed food replenishes it.
        }
        var agentLoopElapsed = profileThisTick ? Stopwatch.GetTimestamp() - agentLoopStarted : 0;

        _birthCooldown = MathF.Max(0, _birthCooldown - dt);
        _eventCooldown = Math.Max(0, _eventCooldown - 1);
        EventTicksRemaining = Math.Max(0, EventTicksRemaining - 1);
        if (EventTicksRemaining == 0 && CurrentEvent != "quiet")
            CurrentEvent = "quiet";

        // Time-sliced heavy systems - distribute load across ticks
        // ShareFoodKnowledge: every 25 ticks, but offset to avoid sync spikes
        _knowledgeShareOffset = (_knowledgeShareOffset + 1) % 25;
        if (_knowledgeShareOffset == 0)
            ShareFoodKnowledge();

        // Local communication is intentionally slower and bounded. It runs
        // after ordinary knowledge sharing so a shout can reinforce the same
        // memory without adding another full-population pass every tick.
        if (Tick % ShoutIntervalTicks == 0)
            ProcessShouts();

        // World events: every 100 ticks, offset
        _worldEventOffset = (_worldEventOffset + 1) % 100;
        if (_worldEventOffset == 0)
            TryTriggerWorldEvent();

        // Collective culture is intentionally slower than individual action
        // selection. This keeps the feedback loop visible without scanning the
        // population on every tick.
        _factionCultureOffset = (_factionCultureOffset + 1) % 25;
        if (_factionCultureOffset == 0)
            UpdateFactionCulture();

        // Food crisis state machine: every 50 ticks, decoupled phase.
        _foodCrisisOffset = (_foodCrisisOffset + 1) % 50;
        if (_foodCrisisOffset == 0)
            CheckFoodCrisis();

        // Remote cluster detection: every 100 ticks, decoupled phase.
        _clusterCheckOffset = (_clusterCheckOffset + 1) % 100;
        if (_clusterCheckOffset == 0)
            CheckRemoteClusters();

        // Settlement updates: every 100 ticks, decoupled phase.
        _settlementUpdateOffset = (_settlementUpdateOffset + 1) % 100;
        if (_settlementUpdateOffset == 0)
            UpdateSettlements();

        // Aggregated LOD update: every 200 ticks, decoupled phase
        _aggregatedLodOffset = (_aggregatedLodOffset + 1) % 200;
        if (_aggregatedLodOffset == 0)
            UpdateAggregatedChunks();

        // Regrow food: every 50 ticks, offset
        _buildingCandidatesOffset = (_buildingCandidatesOffset + 1) % 50;
        if (_buildingCandidatesOffset == 0)
            RegrowFood();

        var distanceGridPreparationStarted = profileThisTick ? Stopwatch.GetTimestamp() : 0;
        // Batched multi-source pathfinding distance grids
        // Exploration grid: every 50 ticks (phase shifted from regrow)
        _explorationGridOffset = (_explorationGridOffset + 1) % 50;
        if (_explorationGridOffset == 0)
        {
            var exploreTargets = _chunks.GetLowActivityChunkCenters().ToList();
            if (exploreTargets.Count > 0)
                _explorationDistanceGrid = _pathFinder.ComputeDistanceToNearestTarget(exploreTargets);
        }

        // Wall approach grid: every 25 ticks (phase shifted from knowledge share)
        _wallApproachGridOffset = (_wallApproachGridOffset + 1) % 25;
        if (_wallApproachGridOffset == 0)
        {
            var wallTargets = _map.FindWalkableWallApproachCells();
            _wallApproachDistanceGrid = wallTargets.Count > 0
                ? _pathFinder.ComputeDistanceToNearestTarget(wallTargets)
                : null;
        }

        // Migration grid: every 100 ticks (phase shifted from world events)
        _migrationGridOffset = (_migrationGridOffset + 1) % 100;
        if (_migrationGridOffset == 0)
        {
            var migrationTargets = _chunks.GetHighActivityChunkCenters().ToList();
            if (migrationTargets.Count > 0)
                _migrationDistanceGrid = _pathFinder.ComputeDistanceToNearestTarget(migrationTargets);
        }
        // This phase measures periodic multi-source distance-grid preparation,
        // not all pathfinding. Per-agent path searches are included in AgentLoop.
        var distanceGridPreparationElapsed = profileThisTick
            ? Stopwatch.GetTimestamp() - distanceGridPreparationStarted
            : 0;

        // Deferred dead agent cleanup - remove from agents list after iteration
        if (_deadAgents.Count > 0)
        {
            foreach (var dead in _deadAgents)
            {
                _agents.Remove(dead);
                _chunks.UnregisterAgent(dead.Cell, dead.FactionId);
                _deadAgentsRemovedThisTick++;
            }
            _deadAgents.Clear();
        }

        _agentIndex.UpdateIncremental(_agents, Tick);
        TryBirth();
        RefreshStats();
        if (profileThisTick)
            _stepPerformanceProfiler.Record(Stopwatch.GetTimestamp() - stepStarted, agentLoopElapsed, distanceGridPreparationElapsed);
    }

    private void RefreshStats()
    {
        if (!_statsDirty)
            return;

        int alive = 0;
        int lowEnergy = 0;
        int activeBuilders = 0;
        int activeExplorers = 0;
        float energySum = 0f;
        float intelSum = 0f;
        float learningSum = 0f;
        float foodBiasSum = 0f;
        float buildBiasSum = 0f;
        float exploreBiasSum = 0f;
        int decisions = 0;
        int learningUpdates = 0;
        int positiveOutcomes = 0;
        int negativeOutcomes = 0;
        int foragers = 0, builders = 0, scouts = 0, keepers = 0, pathfinders = 0;

        foreach (var agent in _agents)
        {
            if (!agent.Alive)
                continue;

            alive++;
            energySum += agent.Energy;
            intelSum += agent.Intelligence;
            learningSum += agent.LearningRate;
            foodBiasSum += agent.FoodUtilityBias;
            buildBiasSum += agent.BuildUtilityBias;
            exploreBiasSum += agent.ExploreUtilityBias;
            decisions += agent.DecisionsMade;
            learningUpdates += agent.LearningUpdates;
            positiveOutcomes += agent.PositiveOutcomes;
            negativeOutcomes += agent.NegativeOutcomes;

            if (agent.Energy < 30f)
                lowEnergy++;
            if (agent.Action == AgentAction.Building)
                activeBuilders++;
            if (agent.Action == AgentAction.Exploring)
                activeExplorers++;

            switch (agent.Role)
            {
                case AgentRole.Forager: foragers++; break;
                case AgentRole.Builder: builders++; break;
                case AgentRole.Scout: scouts++; break;
                case AgentRole.Keeper: keepers++; break;
                case AgentRole.Pathfinder: pathfinders++; break;
            }
        }

        _cachedAlivePopulation = alive;
        _cachedLowEnergyAgents = lowEnergy;
        _cachedActiveBuilders = activeBuilders;
        _cachedActiveExplorers = activeExplorers;
        _cachedAverageEnergy = alive > 0 ? energySum / alive : 0f;
        _cachedAverageIntelligence = alive > 0 ? intelSum / alive : 0f;
        _cachedAverageLearning = alive > 0 ? learningSum / alive : 0f;
        _cachedAverageFoodBias = alive > 0 ? foodBiasSum / alive : 0f;
        _cachedAverageBuildBias = alive > 0 ? buildBiasSum / alive : 0f;
        _cachedAverageExploreBias = alive > 0 ? exploreBiasSum / alive : 0f;
        _cachedDecisions = decisions;
        _cachedLearningUpdates = learningUpdates;
        _cachedPositiveOutcomes = positiveOutcomes;
        _cachedNegativeOutcomes = negativeOutcomes;
        _cachedForagers = foragers;
        _cachedBuilders = builders;
        _cachedScouts = scouts;
        _cachedKeepers = keepers;
        _cachedPathfinders = pathfinders;

        _statsDirty = false;
    }

    private FactionState EnsureFaction(int factionId)
    {
        var normalizedId = Math.Max(0, factionId);
        while (_factions.Count <= normalizedId)
        {
            var id = _factions.Count;
            _factions.Add(new FactionState
            {
                Id = id,
                FoodFocus = id % 2 == 0 ? 0.58f : 0.46f,
                BuildFocus = id % 2 == 0 ? 0.46f : 0.58f,
                ExploreFocus = id % 2 == 0 ? 0.52f : 0.62f,
                Cohesion = 0.48f,
            });
        }

        return _factions[normalizedId];
    }

    private void UpdateFactionCulture()
    {
        foreach (var faction in _factions)
        {
            var population = 0;
            var energySum = 0f;
            var foodBiasSum = 0f;
            var buildBiasSum = 0f;
            var exploreBiasSum = 0f;
            var socialSum = 0f;
            var foragers = 0;
            var builders = 0;
            var explorers = 0;

            foreach (var agent in _agents)
            {
                if (!agent.Alive || agent.FactionId != faction.Id)
                    continue;

                population++;
                energySum += agent.Energy;
                foodBiasSum += agent.FoodUtilityBias;
                buildBiasSum += agent.BuildUtilityBias;
                exploreBiasSum += agent.ExploreUtilityBias;
                socialSum += agent.SocialAwareness;
                if (agent.Role == AgentRole.Forager) foragers++;
                if (agent.Role == AgentRole.Builder) builders++;
                if (agent.Role is AgentRole.Scout or AgentRole.Pathfinder) explorers++;
            }

            faction.Population = population;
            faction.AverageEnergy = population > 0 ? energySum / population : 0f;
            if (population == 0)
                continue;

            var foodRatio = foodBiasSum / population;
            var buildRatio = buildBiasSum / population;
            var exploreRatio = exploreBiasSum / population;
            var targetFood = Math.Clamp(0.35f + foodRatio * 0.22f + foragers / (float)population * 0.25f +
                                       MathF.Max(0f, 50f - faction.AverageEnergy) / 200f, 0f, 1f);
            var targetBuild = Math.Clamp(0.32f + buildRatio * 0.22f + builders / (float)population * 0.3f, 0f, 1f);
            var targetExplore = Math.Clamp(0.3f + exploreRatio * 0.22f + explorers / (float)population * 0.3f, 0f, 1f);
            var smoothing = 0.12f;

            faction.FoodFocus += (targetFood - faction.FoodFocus) * smoothing;
            faction.BuildFocus += (targetBuild - faction.BuildFocus) * smoothing;
            faction.ExploreFocus += (targetExplore - faction.ExploreFocus) * smoothing;
            faction.Cohesion += (Math.Clamp(0.25f + socialSum / population * 0.65f, 0f, 1f) - faction.Cohesion) * smoothing;
            faction.TerritoryPressure = Math.Clamp(population * 5f / (Width * Height * 0.1f), 0f, 1f);

            var foodPerAgent = StoredFoodUnits / (float)Math.Max(1, AlivePopulation);
            if (foodPerAgent < 2f || faction.AverageEnergy < 38f)
                faction.Goal = FactionGoal.Forage;
            else if (CurrentEvent == "migration_wave")
                faction.Goal = FactionGoal.Migrate;
            else if (faction.BuildFocus >= faction.ExploreFocus && faction.BuildFocus >= faction.FoodFocus)
                faction.Goal = FactionGoal.Build;
            else
                faction.Goal = FactionGoal.Explore;

            if (!_lastFactionGoal.TryGetValue(faction.Id, out var previousGoal) || previousGoal != faction.Goal)
            {
                _lastFactionGoal[faction.Id] = faction.Goal;
                RecordEvent(WorldEventType.FactionGoalChanged,
                    $"Фракция {faction.Id + 1} сменила цель: {FormatGoal(faction.Goal)}",
                    WorldEventImportance.Minor, faction.Id);
            }
        }
    }

    private void InvalidateStats()
    {
        _statsDirty = true;
    }

    private void UpdateAgentLOD()
    {
        int aliveCount = _agents.Count(a => a.Alive);
        _agentLodById.Clear();
        ActiveAgentCount = 0;
        DormantAgentCount = 0;
        // NOTE: AggregatedAgentCount is intentionally NOT touched here. It
        // belongs to the chunk-aggregation subsystem and is rebuilt inside
        // UpdateAggregatedChunks; zeroing it per tick left the counter at 0
        // for ~199 of every 200 ticks while chunk flags stayed set.

        if (!LodEnabled || aliveCount <= ActiveAgentThreshold)
        {
            foreach (var agent in _agents)
            {
                if (!agent.Alive)
                    continue;
                _agentLodById[agent.Id] = AgentLOD.Active;
                ActiveAgentCount++;
            }
            return;
        }

        // Classify agents based on role, energy, and activity
        int activeCount = 0;
        foreach (var agent in _agents)
        {
            if (!agent.Alive)
                continue;

            // Always active: builders, explorers, low energy agents (survival priority)
            bool forceActive = agent.Action == AgentAction.Building ||
                              agent.Action == AgentAction.Exploring ||
                              agent.Action == AgentAction.Migrating ||
                              (agent.Action is not AgentAction.Idle and
                               not AgentAction.Resting and
                               not AgentAction.SearchingFood) ||
                              agent.Energy < 60f ||
                              agent.Role == AgentRole.Builder ||
                              agent.Role == AgentRole.Scout ||
                              agent.Role == AgentRole.Pathfinder;

            if (forceActive || activeCount < ActiveAgentBudgetLimit)
            {
                _agentLodById[agent.Id] = AgentLOD.Active;
                activeCount++;
                ActiveAgentCount++;
            }
            else
            {
                // Demote to dormant
                _agentLodById[agent.Id] = AgentLOD.Dormant;
                DormantAgentCount++;
            }
        }
    }

    private void UpdateDormantAgents()
    {
        if (!LodEnabled) return;

        foreach (var agent in _agents)
        {
            if (!agent.Alive) continue;

            if (!_agentLodById.TryGetValue(agent.Id, out var lod) || lod != AgentLOD.Dormant)
                continue;

            // Chance to become active again if energy recovers or role demands it
            if (agent.Energy < 20f || agent.Action != AgentAction.Idle || _rng.NextDouble() < 0.05)
            {
                _agentLodById[agent.Id] = AgentLOD.Active;
            }
        }
    }

    private void UpdateAggregatedChunks()
    {
        if (!LodEnabled) return;

        int chunkWidth = _chunks.ChunkWidth;
        int chunkHeight = _chunks.ChunkHeight;

        AggregatedAgentCount = 0;

        // Iterate all chunks, update aggregated stats for those not actively simulated
        for (int cy = 0; cy < chunkHeight; cy++)
        {
            for (int cx = 0; cx < chunkWidth; cx++)
            {
                var chunk = _chunks.GetChunk(cx, cy);

                // Check if any agent in this chunk is Active
                bool anyActive = false;
                foreach (var agent in _agents)
                {
                    if (!agent.Alive) continue;
                    if (agent.Cell.X / WorldChunks.ChunkSize == cx && agent.Cell.Y / WorldChunks.ChunkSize == cy)
                    {
                        if (_agentLodById.TryGetValue(agent.Id, out var lod) && lod == AgentLOD.Active)
                        {
                            anyActive = true;
                            break;
                        }
                    }
                }

                if (!anyActive && chunk.AgentCount > 0)
                {
                    // Mark as aggregated - compute stats from agents in this chunk
                    chunk.IsAggregated = true;
                    chunk.AggregatedPopulation = chunk.AgentCount;
                    chunk.LastAggregatedUpdateTick = Tick;

                    // Compute averages from agents in this chunk
                    float energySum = 0f;
                    int foodStockpile = 0;
                    byte factionGoal = 0;
                    float cohesion = 0f;

                    foreach (var agent in _agents)
                    {
                        if (!agent.Alive) continue;
                        if (agent.Cell.X / WorldChunks.ChunkSize == cx && agent.Cell.Y / WorldChunks.ChunkSize == cy)
                        {
                            energySum += agent.Energy;
                            // Add faction stats
                            var faction = _factions.FirstOrDefault(f => f.Id == agent.FactionId);
                            if (faction != null)
                            {
                                factionGoal = (byte)faction.Goal;
                                cohesion = faction.Cohesion;
                                foodStockpile += faction.FoodStored;
                            }
                        }
                    }

                    if (chunk.AggregatedPopulation > 0)
                    {
                        chunk.AggregatedAvgEnergy = energySum / chunk.AggregatedPopulation;
                    }
                    chunk.AggregatedFoodStockpile = foodStockpile;
                    chunk.AggregatedFactionGoal = factionGoal;
                    chunk.AggregatedCohesion = cohesion;

                    // Add to aggregated count
                    AggregatedAgentCount += chunk.AggregatedPopulation;
                }
                else if (chunk.IsAggregated)
                {
                    // Update existing aggregated chunk stats
                    int population = chunk.AgentCount;
                    float energySum = 0f;
                    int foodStockpile = 0;
                    byte factionGoal = 0;
                    float cohesion = 0f;

                    foreach (var agent in _agents)
                    {
                        if (!agent.Alive) continue;
                        if (agent.Cell.X / WorldChunks.ChunkSize == cx && agent.Cell.Y / WorldChunks.ChunkSize == cy)
                        {
                            energySum += agent.Energy;
                            var faction = _factions.FirstOrDefault(f => f.Id == agent.FactionId);
                            if (faction != null)
                            {
                                factionGoal = (byte)faction.Goal;
                                cohesion = faction.Cohesion;
                                foodStockpile += faction.FoodStored;
                            }
                        }
                    }

                    chunk.AggregatedPopulation = population;
                    chunk.AggregatedAvgEnergy = population > 0 ? energySum / population : 0f;
                    chunk.AggregatedFoodStockpile = foodStockpile;
                    chunk.AggregatedFactionGoal = factionGoal;
                    chunk.AggregatedCohesion = cohesion;
                    chunk.LastAggregatedUpdateTick = Tick;

                    AggregatedAgentCount += population;
                }
                else
                {
                    // Chunk has active agents or is empty - not aggregated
                    chunk.IsAggregated = false;
                    chunk.AggregatedPopulation = 0;
                }

                _chunks.SetChunk(cx, cy, chunk);
            }
        }
    }

    private bool TryConsumeStoredFood(AgentState agent)
    {
        if (!agent.Alive || agent.FeedingCooldown > 0 || agent.Energy > 65f)
            return false;

        if (agent.CarriedFood > 0)
        {
            agent.CarriedFood--;
            Nourish(agent);
            return true;
        }

        var piles = ReachableStorage(agent.Cell, 6);
        if (piles.Count == 0)
        {
            agent.FeedingCooldown = 1f; // bounded retry, not a search every tick
            return false;
        }
        var bestCell = piles[0];
        if (--_foodStorage[bestCell] == 0) _foodStorage.Remove(bestCell);

        FoodStockpile = Math.Max(0, FoodStockpile - 1);
        Nourish(agent);

        if (agent.Energy < 58f && agent.Action is AgentAction.SearchingFood or AgentAction.GoingToFood or AgentAction.Exploring)
        {
            agent.Path.Clear();
            agent.PathIndex = 0;
            agent.Action = AgentAction.Resting;
            agent.RestTimer = 1.5f;
        }

        return true;
    }

    private void ShareFoodKnowledge()
    {
        foreach (var sender in _agents)
        {
            if (!sender.Alive || (!sender.HasKnownFood && !sender.HasDangerMemory))
                continue;

            // Buffer and sort candidates by Id: spatial-index bucket order depends
            // on movement history, which can differ between an organic world and a
            // restored one. RNG is consumed per candidate, so the visit order must
            // be layout-independent for deterministic continuation.
            List<AgentState>? receivers = null;
            foreach (var candidate in _agentIndex.Nearby(sender.Cell, 5))
            {
                if (candidate.Id == sender.Id || candidate.SocialAwareness < 0.25f)
                    continue;
                receivers ??= new List<AgentState>();
                receivers.Add(candidate);
            }

            if (receivers == null)
                continue;
            receivers.Sort((a, b) => a.Id.CompareTo(b.Id));

            foreach (var receiver in receivers)
            {
                var distance = Math.Abs(sender.Cell.X - receiver.Cell.X) + Math.Abs(sender.Cell.Y - receiver.Cell.Y);
                if (distance > 5)
                    continue;

                var shareChance = 0.08f + receiver.SocialAwareness * 0.22f + sender.Intelligence * 0.08f;
                if (_rng.NextDouble() > shareChance)
                    continue;

                if (sender.HasKnownFood && sender.FoodKnowledge >= 0.2f &&
                    (!receiver.HasKnownFood || receiver.FoodKnowledge < sender.FoodKnowledge))
                {
                    receiver.KnownFoodCell = sender.KnownFoodCell;
                    receiver.HasKnownFood = true;
                    receiver.FoodKnowledge = MathF.Min(1f, sender.FoodKnowledge * 0.85f);
                    receiver.SharedMemories++;
                    FoodShared++;
                    EnsureFaction(sender.FactionId).KnowledgeShared++;
                }

                if (sender.HasDangerMemory && sender.DangerKnowledge >= 0.2f &&
                    (!receiver.HasDangerMemory || receiver.DangerKnowledge < sender.DangerKnowledge))
                {
                    receiver.KnownDangerCell = sender.KnownDangerCell;
                    receiver.HasDangerMemory = true;
                    receiver.DangerKnowledge = MathF.Min(1f, sender.DangerKnowledge * 0.8f);
                    receiver.SharedMemories++;
                    KnowledgeShared++;
                    EnsureFaction(sender.FactionId).KnowledgeShared++;
                }
            }
        }
    }

    private void ProcessShouts()
    {
        for (var i = _recentShouts.Count - 1; i >= 0; i--)
        {
            if (_recentShouts[i].ExpiresTick <= Tick)
                _recentShouts.RemoveAt(i);
        }

        var created = 0;
        foreach (var sender in _agents.OrderBy(agent => agent.Id))
        {
            if (created >= MaxShoutsPerTick || !sender.Alive || !IsAgentActive(sender) ||
                sender.ShoutCooldown > 0 || sender.SocialAwareness < 0.25f)
                continue;

            var type = SelectShoutType(sender);
            if (!type.HasValue)
                continue;

            var chance = Math.Clamp(0.12f + sender.SocialAwareness * 0.3f + sender.Intelligence * 0.08f, 0f, 0.5f);
            if (_rng.NextDouble() > chance)
                continue;

            var radius = Math.Clamp(5 + (int)(sender.SocialAwareness * 7f) + (int)(sender.Intelligence * 2f), 5, 14);
            var signal = new ShoutSignal
            {
                SenderId = sender.Id,
                FactionId = sender.FactionId,
                Cell = sender.Cell,
                Type = type.Value,
                Strength = Math.Clamp(0.45f + sender.SocialAwareness * 0.35f + sender.Intelligence * 0.2f, 0.4f, 1f),
                Radius = radius,
                CreatedTick = Tick,
                ExpiresTick = Tick + ShoutVisualLifetimeTicks,
            };

            sender.ShoutCooldown = 100f + (1f - sender.SocialAwareness) * 140f;
            sender.ShoutsMade++;
            sender.LastShoutType = signal.Type;
            sender.LastShoutTick = Tick;
            _shoutsMade++;
            _shoutTypeCounts[(int)signal.Type]++;
            _recentShouts.Add(signal);
            PropagateShout(signal);
            created++;
        }
    }

    private ShoutType? SelectShoutType(AgentState agent)
    {
        if (agent.HasKnownFood && agent.FoodKnowledge >= 0.45f &&
            agent.Action is AgentAction.GatheringFood or AgentAction.CarryingFood or AgentAction.StoringFood)
            return ShoutType.Food;
        if (agent.Action is AgentAction.Building or AgentAction.Migrating)
            return ShoutType.Rally;
        if (agent.HasDangerMemory && agent.DangerKnowledge >= 0.45f && agent.FailedFoodTrips > 0 &&
            Math.Abs(agent.Cell.X - agent.KnownDangerCell.X) + Math.Abs(agent.Cell.Y - agent.KnownDangerCell.Y) <= 14 &&
            (agent.Energy < 40 || !_map.IsWalkable(agent.KnownDangerCell) || IsDroughtCell(agent.KnownDangerCell)))
            return ShoutType.Danger;
        return null;
    }

    private void PropagateShout(ShoutSignal signal)
    {
        var receivers = _agentIndex.Nearby(signal.Cell, signal.Radius)
            .Where(agent => agent.Alive && agent.Id != signal.SenderId && IsAgentActive(agent))
            .OrderBy(agent => agent.Id)
            .ToList();

        foreach (var receiver in receivers)
        {
            var distance = Math.Abs(signal.Cell.X - receiver.Cell.X) + Math.Abs(signal.Cell.Y - receiver.Cell.Y);
            if (distance > signal.Radius)
                continue;

            // Food and rally calls are faction-local. Danger travels across
            // faction boundaries, which gives it a useful survival role.
            if ((signal.Type is ShoutType.Food or ShoutType.Rally) && receiver.FactionId != signal.FactionId)
                continue;

            var falloff = 1f - distance / (float)(signal.Radius + 1);
            var reception = signal.Strength * falloff * (0.45f + receiver.SocialAwareness * 0.25f + receiver.Intelligence * 0.15f);
            if (reception < 0.16f)
                continue;

            var reputation = GetOrCreateShoutReputation(receiver, signal.SenderId);
            reputation.HeardCount++;
            reputation.LastSeenTick = Tick;
            reception *= GetShoutTrustMultiplier(receiver, signal.Type);
            reception *= GetSourceTrustMultiplier(reputation, signal.Type);
            reception *= GetFactionReliabilityMultiplier(EnsureFaction(receiver.FactionId), signal.Type);
            RememberShout(receiver, signal, reception);

            receiver.ShoutsHeard++;
            receiver.LastHeardShoutType = signal.Type;
            receiver.LastHeardShoutTick = Tick;
            receiver.LastHeardShoutCell = signal.Cell;
            receiver.LastHeardShoutStrength = reception;
            receiver.LastHeardShoutSenderId = signal.SenderId;
            receiver.LastHeardShoutEvaluated = false;
            _shoutsHeard++;

            switch (signal.Type)
            {
                case ShoutType.Food:
                    receiver.KnownFoodCell = signal.Cell;
                    receiver.HasKnownFood = true;
                    receiver.FoodKnowledge = MathF.Max(receiver.FoodKnowledge, MathF.Min(1f, reception));
                    receiver.RouteKnowledge = MathF.Min(1f, receiver.RouteKnowledge + reception * 0.18f);
                    receiver.SharedMemories++;
                    KnowledgeShared++;
                    break;

                case ShoutType.Danger:
                    receiver.KnownDangerCell = signal.Cell;
                    receiver.HasDangerMemory = true;
                    receiver.DangerKnowledge = MathF.Max(receiver.DangerKnowledge, MathF.Min(1f, reception));
                    receiver.SharedMemories++;
                    KnowledgeShared++;
                    break;

                case ShoutType.Rally:
                    if (receiver.CarriedFood == 0 && receiver.Energy >= 55f &&
                        receiver.ShoutRallyTrust >= 0.25f &&
                        receiver.Action is AgentAction.Idle or AgentAction.Resting or AgentAction.Exploring)
                    {
                        SetPath(receiver, signal.Cell);
                        if (receiver.Path.Count > 0)
                            receiver.Action = AgentAction.Exploring;
                    }
                    break;
            }
        }
    }

    private bool IsAgentActive(AgentState agent) =>
        !LodEnabled || !_agentLodById.TryGetValue(agent.Id, out var lod) || lod == AgentLOD.Active;

    private static float GetShoutTrustMultiplier(AgentState agent, ShoutType type) => type switch
    {
        ShoutType.Food => 0.65f + agent.ShoutFoodTrust * 0.7f,
        ShoutType.Danger => 0.65f + agent.ShoutDangerTrust * 0.7f,
        ShoutType.Rally => 0.65f + agent.ShoutRallyTrust * 0.7f,
        _ => 1f,
    };

    private static float GetSourceTrustMultiplier(ShoutReputation reputation, ShoutType type) =>
        0.7f + GetSourceTrust(reputation, type) * 0.6f;

    private static float GetSourceTrust(ShoutReputation reputation, ShoutType type) => type switch
    {
        ShoutType.Food => reputation.FoodTrust,
        ShoutType.Danger => reputation.DangerTrust,
        ShoutType.Rally => reputation.RallyTrust,
        _ => 0.5f,
    };

    private ShoutReputation GetOrCreateShoutReputation(AgentState agent, int senderId)
    {
        agent.ShoutReputations ??= new List<ShoutReputation>(MaxShoutReputationsPerAgent);
        var existing = agent.ShoutReputations.FirstOrDefault(entry => entry.SenderId == senderId);
        if (existing is not null)
            return existing;

        if (agent.ShoutReputations.Count >= MaxShoutReputationsPerAgent)
        {
            var evicted = agent.ShoutReputations
                .OrderBy(entry => entry.LastSeenTick)
                .ThenBy(entry => entry.SenderId)
                .First();
            agent.ShoutReputations.Remove(evicted);
        }

        var created = new ShoutReputation { SenderId = senderId, LastSeenTick = Tick };
        agent.ShoutReputations.Add(created);
        return created;
    }

    private static float GetFactionReliabilityMultiplier(FactionState faction, ShoutType type) => type switch
    {
        ShoutType.Food => 0.85f + faction.FoodSignalReliability * 0.3f,
        ShoutType.Danger => 0.85f + faction.DangerSignalReliability * 0.3f,
        ShoutType.Rally => 0.85f + faction.RallySignalReliability * 0.3f,
        _ => 1f,
    };

    private ShoutMemory RememberShout(AgentState agent, ShoutSignal signal, float reception)
    {
        agent.ShoutMemories ??= new List<ShoutMemory>(MaxShoutMemoriesPerAgent);
        for (var i = 0; i < agent.ShoutMemories.Count; i++)
        {
            var existing = agent.ShoutMemories[i];
            if (existing.SenderId != signal.SenderId || existing.Type != signal.Type || existing.Cell != signal.Cell)
                continue;

            existing.Confidence = Math.Clamp(existing.Confidence * 0.75f + reception * 0.25f, 0f, 1f);
            existing.Strength = MathF.Max(existing.Strength, reception);
            existing.HeardTick = Tick;
            return existing;
        }

        if (agent.ShoutMemories.Count >= MaxShoutMemoriesPerAgent)
        {
            var evictionIndex = 0;
            var evictionScore = float.MaxValue;
            for (var i = 0; i < agent.ShoutMemories.Count; i++)
            {
                var candidate = agent.ShoutMemories[i];
                var age = Math.Clamp((Tick - candidate.HeardTick) / (float)ShoutMemoryLifetimeTicks, 0f, 1f);
                var score = candidate.Confidence * 0.7f + candidate.Strength * 0.2f - age * 0.1f;
                if (score < evictionScore ||
                    (score == evictionScore && candidate.HeardTick < agent.ShoutMemories[evictionIndex].HeardTick) ||
                    (score == evictionScore && candidate.HeardTick == agent.ShoutMemories[evictionIndex].HeardTick && candidate.SenderId < agent.ShoutMemories[evictionIndex].SenderId))
                {
                    evictionIndex = i;
                    evictionScore = score;
                }
            }
            agent.ShoutMemories.RemoveAt(evictionIndex);
        }

        var created = new ShoutMemory
        {
            SenderId = signal.SenderId,
            FactionId = signal.FactionId,
            Cell = signal.Cell,
            Type = signal.Type,
            Confidence = Math.Clamp(reception, 0f, 1f),
            Strength = Math.Clamp(reception, 0f, 1f),
            HeardTick = Tick,
        };
        agent.ShoutMemories.Add(created);
        return created;
    }

    private static ShoutMemory? FindShoutMemory(AgentState agent, ShoutType type, Point cell, int senderId = -1)
    {
        if (agent.ShoutMemories is null)
            return null;

        ShoutMemory? best = null;
        foreach (var memory in agent.ShoutMemories)
        {
            if (memory.Type != type || memory.Cell != cell || (senderId >= 0 && memory.SenderId != senderId))
                continue;
            if (best is null || memory.HeardTick > best.HeardTick ||
                (memory.HeardTick == best.HeardTick && memory.SenderId < best.SenderId))
                best = memory;
        }
        return best;
    }

    private float GetShoutMemoryConfidenceAt(AgentState agent, ShoutType type, Point cell)
    {
        if (agent.ShoutMemories is null)
            return 0f;

        var confidence = 0f;
        foreach (var memory in agent.ShoutMemories)
        {
            if (memory.Type != type || memory.Cell != cell)
                continue;

            var age = Tick - memory.HeardTick;
            if (age < 0 || age > ShoutMemoryLifetimeTicks)
                continue;

            var freshness = 1f - age / (float)ShoutMemoryLifetimeTicks;
            var effectiveConfidence = memory.Confidence * (0.55f + freshness * 0.45f);
            if (effectiveConfidence > confidence)
                confidence = effectiveConfidence;
        }
        return confidence;
    }

    private void LearnShoutOutcome(AgentState agent, ShoutType type, bool successful, Point outcomeCell)
    {
        if (agent.LastHeardShoutEvaluated || agent.LastHeardShoutTick < 0 ||
            agent.LastHeardShoutType != type || Tick - agent.LastHeardShoutTick > 600)
            return;

        var distance = Math.Abs(agent.LastHeardShoutCell.X - outcomeCell.X) +
                       Math.Abs(agent.LastHeardShoutCell.Y - outcomeCell.Y);
        if (distance > 3)
            return;

        var target = successful ? 1f : 0f;
        var rate = Math.Clamp(0.04f + agent.LearningRate * 0.12f, 0.04f, 0.16f);
        switch (type)
        {
            case ShoutType.Food:
                agent.ShoutFoodTrust = Math.Clamp(agent.ShoutFoodTrust + (target - agent.ShoutFoodTrust) * rate, 0f, 1f);
                break;
            case ShoutType.Danger:
                agent.ShoutDangerTrust = Math.Clamp(agent.ShoutDangerTrust + (target - agent.ShoutDangerTrust) * rate, 0f, 1f);
                break;
            case ShoutType.Rally:
                agent.ShoutRallyTrust = Math.Clamp(agent.ShoutRallyTrust + (target - agent.ShoutRallyTrust) * rate, 0f, 1f);
                break;
        }

        if (agent.LastHeardShoutSenderId >= 0)
        {
            var reputation = GetOrCreateShoutReputation(agent, agent.LastHeardShoutSenderId);
            switch (type)
            {
                case ShoutType.Food:
                    reputation.FoodTrust = Math.Clamp(reputation.FoodTrust + (target - reputation.FoodTrust) * rate, 0f, 1f);
                    break;
                case ShoutType.Danger:
                    reputation.DangerTrust = Math.Clamp(reputation.DangerTrust + (target - reputation.DangerTrust) * rate, 0f, 1f);
                    break;
                case ShoutType.Rally:
                    reputation.RallyTrust = Math.Clamp(reputation.RallyTrust + (target - reputation.RallyTrust) * rate, 0f, 1f);
                    break;
            }
            reputation.LearningEvents++;
            reputation.LastSeenTick = Tick;
        }

        var memory = FindShoutMemory(agent, type, agent.LastHeardShoutCell, agent.LastHeardShoutSenderId);
        if (memory is not null)
        {
            memory.Confidence = Math.Clamp(memory.Confidence + (target - memory.Confidence) * rate, 0f, 1f);
            memory.LastOutcomeTick = Tick;
            if (successful)
                memory.SuccessfulOutcomes++;
            else
                memory.FailedOutcomes++;
        }

        var faction = EnsureFaction(agent.FactionId);
        switch (type)
        {
            case ShoutType.Food:
                faction.FoodSignalReliability = Math.Clamp(faction.FoodSignalReliability + (target - faction.FoodSignalReliability) * rate, 0f, 1f);
                break;
            case ShoutType.Danger:
                faction.DangerSignalReliability = Math.Clamp(faction.DangerSignalReliability + (target - faction.DangerSignalReliability) * rate, 0f, 1f);
                break;
            case ShoutType.Rally:
                faction.RallySignalReliability = Math.Clamp(faction.RallySignalReliability + (target - faction.RallySignalReliability) * rate, 0f, 1f);
                break;
        }
        faction.ShoutLearningEvents++;

        agent.LastHeardShoutEvaluated = true;
        agent.ShoutLearningEvents++;
        if (successful)
        {
            agent.SuccessfulShoutLessons++;
            _successfulShoutLessons++;
        }
        else
        {
            agent.FailedShoutLessons++;
            _failedShoutLessons++;
        }
        _shoutLearningEvents++;
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
                else
                    ChooseNextAction(agent);
                break;

            case AgentAction.Resting:
                if (agent.RestTimer > 0)
                    break;

                if (agent.CarriedFood > 0)
                {
                    agent.Action = AgentAction.ReturningToWall;
                    SetPathToNearestWall(agent);
                }
                else
                    ChooseNextAction(agent);
                break;

            case AgentAction.SearchingFood:
                var foodTarget = FindNearestFood(agent);
                if (foodTarget.HasValue && SetFoodTarget(agent, foodTarget.Value))
                {
                    agent.Action = AgentAction.GoingToFood;
                }
                else
                    ChooseNextAction(agent, includeFood: false);
                break;

            case AgentAction.Exploring:
                if (agent.Cell == agent.TargetCell)
                {
                    if (agent.HasBeaconTarget && agent.BeaconTargetCell == agent.Cell)
                        _beaconArrivals++;
                    agent.HasBeaconTarget = false;
                    agent.ExplorationTrips++;
                    LearnShoutOutcome(agent, ShoutType.Rally, successful: true, outcomeCell: agent.Cell);
                    ApplyLearning(agent, AgentAction.Exploring, 0.25f);
                    agent.Action = AgentAction.Resting;
                    agent.RestTimer = 1.5f;
                }
                else if (agent.Path.Count == 0 || agent.PathIndex >= agent.Path.Count)
                {
                    agent.HasBeaconTarget = false;
                    LearnShoutOutcome(agent, ShoutType.Rally, successful: false, outcomeCell: agent.Cell);
                    agent.Action = AgentAction.Idle;
                }
                break;

            case AgentAction.Migrating:
                if (agent.Cell == agent.TargetCell)
                {
                    agent.ExplorationTrips++;
                    agent.Role = AgentRole.Pathfinder;
                    agent.RoleExperience = MathF.Min(1f, agent.RoleExperience + 0.12f);
                    ApplyLearning(agent, AgentAction.Migrating, 0.3f);
                    agent.Action = AgentAction.Resting;
                    agent.RestTimer = 2.5f;
                }
                else if (agent.Path.Count == 0 || agent.PathIndex >= agent.Path.Count)
                {
                    agent.Action = AgentAction.Idle;
                }
                break;

            case AgentAction.GoingToFood:
                if (agent.Cell == agent.TargetCell)
                {
                    var targetFood = FindFoodAt(agent.TargetCell);
                    if (targetFood is not null && targetFood.Amount > 0)
                    {
                        if (agent.HasInsightRouteAttribution && !agent.InsightRouteArrived &&
                            agent.InsightRouteCell == targetFood.Cell && agent.FoodTargetCell == targetFood.Cell)
                        {
                            _insightFoodArrivals++;
                            agent.InsightRouteArrived = true;
                        }
                        agent.Action = AgentAction.GatheringFood;
                        agent.Path.Clear();
                        agent.PathIndex = 0;
                    }
                    else
                    {
                        MarkFoodFailure(agent);
                        ReleaseFoodReservation(agent);
                        agent.Action = AgentAction.SearchingFood;
                        agent.Path.Clear();
                        agent.PathIndex = 0;
                    }
                }
                else if (agent.Path.Count == 0 || agent.PathIndex >= agent.Path.Count)
                {
                    MarkFoodFailure(agent);
                    ReleaseFoodReservation(agent);
                    agent.Action = AgentAction.SearchingFood;
                    agent.Path.Clear();
                    agent.PathIndex = 0;
                }
                break;

            case AgentAction.GatheringFood:
                var foodAtCell = FindFoodAt(agent.Cell);
                var returnEnergy = 45f + (1f - agent.RiskTolerance) * 20f;
                if (agent.CarriedFood >= 3 || agent.Energy < returnEnergy)
                {
                    ClearInsightRouteAttribution(agent);
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
                // The target is reached before the block is actually placed.
                break;

            case AgentAction.Digging:
                // A short rest follows opening a passage through a wall.
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

        var oldCell = agent.Cell;
        var delta = new Point(nextCell.X - agent.Cell.X, nextCell.Y - agent.Cell.Y);
        agent.Cell = nextCell;
        if (_map[nextCell] == CellType.Door)
        {
            _passageTraversals++;
            if (_playerPassageCells.Contains(nextCell))
            {
                _playerPassageTraversals++;
                if (_announcedPassageImpactCells.Add(nextCell))
                    RecordEvent(WorldEventType.PlayerIntervention,
                        $"Проход работает: житель #{agent.Id} впервые прошёл через открытый вами блок ({nextCell.X},{nextCell.Y}).",
                        WorldEventImportance.Major, agent.FactionId, nextCell);
            }
        }
        if (delta.X != 0 || delta.Y != 0)
            agent.Facing = new Point(Math.Sign(delta.X), Math.Sign(delta.Y));
        agent.PathIndex++;
        agent.MoveCooldown = agent.Action == AgentAction.ReturningToWall ? 0.1f : 0.2f;

        // Update chunk registration for moved agent
        if (oldCell != nextCell)
        {
            _chunks.UnregisterAgent(oldCell, agent.FactionId);
            _chunks.RegisterAgent(nextCell, agent.FactionId);
        }
    }

    private void ResolveAction(AgentState agent)
    {
        if (agent.Action == AgentAction.GatheringFood)
        {
            var node = FindFoodAt(agent.Cell);
            if (node is not null && node.Amount > 0)
            {
                if (agent.HasInsightRouteAttribution && agent.InsightRouteArrived &&
                    agent.InsightRouteCell == node.Cell && agent.Cell == node.Cell)
                    _insightFoodHarvested++;

                var firstUnit = agent.CarriedFood == 0;
                node.Amount--;
                ConsumeBloomFood(node);
                agent.CarriedFood++;
                FoodGathered++;
                // Harvesting fills inventory, not the organism's stomach.
                if (firstUnit)
                {
                    if (FoodGathered == 1 && CanRecordOnce(WorldEventType.FirstFoodGathered))
                    {
                        MarkRecordedOnce(WorldEventType.FirstFoodGathered);
                        RecordEvent(WorldEventType.FirstFoodGathered,
                            "Первая добыча еды",
                            WorldEventImportance.Major, agent.FactionId, agent.Cell);
                    }
                    agent.KnownFoodCell = agent.Cell;
                    agent.HasKnownFood = true;
                    agent.FoodKnowledge = MathF.Min(
                        1f,
                        agent.FoodKnowledge + 0.05f + agent.LearningRate * 0.1f + agent.Intelligence * 0.06f);
                    agent.RouteKnowledge = MathF.Min(1f, agent.RouteKnowledge + 0.04f + agent.LearningRate * 0.08f);
                    if (agent.HasDangerMemory && agent.KnownDangerCell == agent.Cell)
                    {
                        agent.DangerKnowledge = MathF.Max(0, agent.DangerKnowledge - 0.3f);
                        if (agent.DangerKnowledge <= 0.05f)
                            agent.HasDangerMemory = false;
                    }
                    agent.SuccessfulFoodTrips++;
                    LearnShoutOutcome(agent, ShoutType.Food, successful: true, outcomeCell: agent.Cell);
                    if (agent.LastHeardShoutType == ShoutType.Danger)
                        LearnShoutOutcome(agent, ShoutType.Danger, successful: false, outcomeCell: agent.Cell);
                    ApplyLearning(agent, AgentAction.SearchingFood, 0.45f);
                }
                if (node.Amount <= 0)
                {
                    _chunks.UnregisterFood(node.Cell);
                    if (CanRecordCooldown(WorldEventType.SourceDepleted, 150))
                    {
                        MarkRecordedCooldown(WorldEventType.SourceDepleted);
                        RecordEvent(WorldEventType.SourceDepleted,
                            $"Источник еды исчерпан ({node.Cell.X},{node.Cell.Y})",
                            WorldEventImportance.Minor, agent.FactionId, node.Cell);
                    }
                    ClearInsightRouteAttribution(agent);
                    ReleaseFoodReservation(agent);
                }
            }
        }

        if (agent.Action == AgentAction.StoringFood && agent.CarriedFood > 0)
        {
            ClearInsightRouteAttribution(agent);
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
            // Depositing food is work, not free nutrition.
            EnsureFaction(agent.FactionId).FoodStored += stored;
            ApplyLearning(agent, AgentAction.StoringFood, 0.3f);
            agent.Action = AgentAction.Resting;
            agent.RestTimer = 2f;
        }

        if (agent.Action == AgentAction.Building)
        {
            if (agent.Cell == agent.TargetCell && agent.Energy >= WallBuildEnergyCost + 40f && CanBuildAt(agent.TargetCell, agent.Id))
            {
                _map.BuildWallCell(agent.TargetCell);
                _chunks.RegisterWall(agent.TargetCell);
                _wallIndex.Rebuild(_map.WallCells);
                _pathFinder.InvalidatePathCache();
                agent.Energy -= WallBuildEnergyCost;
                agent.BuildCooldown = 4f;
                agent.HomeWallCell = agent.TargetCell;
                agent.HasHomeWall = true;
                agent.RoleExperience = MathF.Min(1f, agent.RoleExperience + 0.08f);
                WallBlocksBuilt++;
                if (WallBlocksBuilt == 1 && CanRecordOnce(WorldEventType.FirstWallBuilt))
                {
                    MarkRecordedOnce(WorldEventType.FirstWallBuilt);
                    RecordEvent(WorldEventType.FirstWallBuilt,
                        "Первая стена поселения",
                        WorldEventImportance.Major, agent.FactionId, agent.TargetCell);
                }
                else if (_lastWallMilestoneIndex < WallMilestones.Length &&
                         WallBlocksBuilt >= WallMilestones[_lastWallMilestoneIndex])
                {
                    var milestone = WallMilestones[_lastWallMilestoneIndex];
                    _lastWallMilestoneIndex++;
                    RecordEvent(WorldEventType.WallMilestone,
                        $"Поселение достигло {milestone} стен",
                        WorldEventImportance.Major, agent.FactionId, agent.TargetCell);
                }
                ApplyLearning(agent, AgentAction.Building, 0.4f);
            }
            else if (agent.Cell != agent.TargetCell && agent.PathIndex < agent.Path.Count)
            {
                return;
            }

            agent.Action = AgentAction.Resting;
            agent.RestTimer = 3f;
        }

        if (agent.Action == AgentAction.Digging)
        {
            agent.Action = AgentAction.Resting;
            agent.RestTimer = 2f;
        }
    }

    /// <summary>
    /// Chooses among several needs using a small individual utility policy.
    /// The policy is deliberately deterministic and compact: it is an
    /// inspectable learning system, not an opaque neural network per agent.
    /// </summary>
    private void ChooseNextAction(AgentState agent, bool includeFood = true)
    {
        var options = new List<(AgentAction Action, float Score)>(5);
        if (includeFood)
            options.Add((AgentAction.SearchingFood, ScoreFoodNeed(agent)));

        if (agent.Energy >= WallBuildEnergyCost + 40f)
            options.Add((AgentAction.Building, ScoreBuildNeed(agent)));
        if (agent.Energy >= 55f)
            options.Add((AgentAction.Exploring, ScoreExploreNeed(agent)));
        if (agent.Energy >= 65f && CurrentEvent == "migration_wave")
            options.Add((AgentAction.Migrating, ScoreMigrationNeed(agent)));
        if (agent.Energy >= 20f && _map.IsNearWall(agent.Cell))
            options.Add((AgentAction.Digging, ScoreDigNeed(agent)));
        if (agent.Energy is >= 40f and < 55f && _map.IsNearWall(agent.Cell))
            options.Add((AgentAction.Resting, ScoreRestNeed(agent)));

        options.Sort((left, right) => right.Score.CompareTo(left.Score));
        agent.DecisionsMade++;

        foreach (var option in options)
        {
            var started = option.Action switch
            {
                AgentAction.SearchingFood => StartSearchingFood(agent),
                AgentAction.Building => TryStartBuilding(agent),
                AgentAction.Exploring => TryStartExploration(agent),
                AgentAction.Migrating => TryStartMigration(agent),
                AgentAction.Digging => TryOpenPassage(agent),
                AgentAction.Resting => StartResting(agent),
                _ => false,
            };

            if (!started)
                continue;

            agent.LastDecisionAction = option.Action;
            agent.LastDecisionScore = option.Score;
            return;
        }

        agent.LastDecisionAction = AgentAction.Idle;
        agent.LastDecisionScore = 0f;
        agent.Action = includeFood ? AgentAction.SearchingFood : AgentAction.Idle;
    }

    private static bool StartSearchingFood(AgentState agent)
    {
        agent.Action = AgentAction.SearchingFood;
        return true;
    }

    private static bool StartResting(AgentState agent)
    {
        agent.Action = AgentAction.Resting;
        agent.RestTimer = MathF.Max(agent.RestTimer, 1.2f);
        return true;
    }

    private float ScoreFoodNeed(AgentState agent)
    {
        var faction = EnsureFaction(agent.FactionId);
        var energyNeed = MathF.Max(0f, (62f - agent.Energy) / 14f);
        var knowledge = agent.FoodKnowledge * 1.2f + agent.RouteKnowledge * 0.7f;
        var roleBonus = agent.Role == AgentRole.Forager ? 0.8f : 0f;
        var dangerPenalty = agent.HasDangerMemory ? agent.DangerKnowledge * 0.45f : 0f;
        var goalBonus = faction.Goal == FactionGoal.Forage ? 1.1f : 0f;
        return 1.3f + energyNeed + knowledge + roleBonus + faction.FoodFocus * 0.7f +
               goalBonus + agent.FoodUtilityBias - dangerPenalty;
    }

    private float ScoreBuildNeed(AgentState agent)
    {
        var faction = EnsureFaction(agent.FactionId);
        var capacityPressure = Math.Clamp(
            _map.WallCells.Count / (float)Math.Max(1, CurrentWallCapacity),
            0f,
            1f);
        var roleBonus = agent.Role == AgentRole.Builder ? 1.1f :
                        agent.Role == AgentRole.Keeper ? 0.35f : 0f;
        var goalBonus = faction.Goal == FactionGoal.Build ? 1.15f : 0f;
        return 0.35f + agent.BuildDrive * 2f + agent.PlanningSkill * 0.8f +
               roleBonus + faction.BuildFocus * 0.7f + goalBonus + agent.BuildUtilityBias - capacityPressure * 1.5f;
    }

    private float ScoreExploreNeed(AgentState agent)
    {
        var faction = EnsureFaction(agent.FactionId);
        var roleBonus = agent.Role is AgentRole.Scout or AgentRole.Pathfinder ? 0.9f : 0f;
        var foodPressure = FoodStockpile < Math.Max(8, AlivePopulation * 2) ? 0.7f : 0f;
        var goalBonus = faction.Goal == FactionGoal.Explore ? 1.05f : 0f;
        return 0.2f + agent.ExplorationDrive * 1.8f + agent.Intelligence * 0.65f +
               roleBonus + faction.ExploreFocus * 0.7f + goalBonus + agent.ExploreUtilityBias - foodPressure;
    }

    private float ScoreMigrationNeed(AgentState agent)
    {
        var faction = EnsureFaction(agent.FactionId);
        var roleBonus = agent.Role is AgentRole.Scout or AgentRole.Pathfinder ? 0.75f : 0f;
        var goalBonus = faction.Goal == FactionGoal.Migrate ? 1.3f : 0f;
        return 0.6f + agent.ExplorationDrive + agent.Intelligence * 0.6f + roleBonus + goalBonus;
    }

    private float ScoreDigNeed(AgentState agent)
    {
        return 0.25f + agent.ExplorationDrive * 0.9f + agent.PlanningSkill * 0.25f;
    }

    private float ScoreRestNeed(AgentState agent)
    {
        return MathF.Max(0f, (55f - agent.Energy) / 12f) + agent.RestUtilityBias;
    }

    private void ApplyLearning(AgentState agent, AgentAction action, float reward)
    {
        if (MathF.Abs(reward) < 0.001f)
            return;

        var delta = reward * Math.Clamp(agent.LearningRate * 0.08f, 0.01f, 0.08f);
        switch (action)
        {
            case AgentAction.SearchingFood:
            case AgentAction.CarryingFood:
            case AgentAction.ReturningToWall:
            case AgentAction.StoringFood:
                agent.FoodUtilityBias = Math.Clamp(agent.FoodUtilityBias + delta, -2.5f, 2.5f);
                break;
            case AgentAction.Building:
                agent.BuildUtilityBias = Math.Clamp(agent.BuildUtilityBias + delta, -2.5f, 2.5f);
                break;
            case AgentAction.Exploring:
            case AgentAction.Migrating:
            case AgentAction.Digging:
                agent.ExploreUtilityBias = Math.Clamp(agent.ExploreUtilityBias + delta, -2.5f, 2.5f);
                break;
            case AgentAction.Resting:
                agent.RestUtilityBias = Math.Clamp(agent.RestUtilityBias + delta, -2.5f, 2.5f);
                break;
        }

        agent.LearningUpdates++;
        if (reward > 0)
            agent.PositiveOutcomes++;
        else
            agent.NegativeOutcomes++;
    }

    private bool TryStartBuilding(AgentState agent)
    {
        if (agent.BuildCooldown > 0 || agent.Energy < WallBuildEnergyCost + 40f ||
            _map.WallCells.Count + 1 > CurrentWallCapacity || agent.CarriedFood > 0)
            return false;
        var roleBonus = agent.Role == AgentRole.Builder ? 0.18f :
                        agent.Role == AgentRole.Keeper ? 0.06f :
                        agent.Role == AgentRole.Scout ? -0.04f : 0f;
        if (_rng.NextDouble() > Math.Clamp(0.08f + agent.BuildDrive * 0.55f + roleBonus, 0.03f, 0.82f))
            return false;

        // Construction is intentionally block-by-block, but decisions are made
        // against a line plan. The old radius search rewarded touching many
        // walls, which inevitably produced blobs and closed cells into huts.
        // Here an agent normally chooses an endpoint of the longest straight
        // run and extends it by exactly one block.
        var lineCandidates = FindWallLineCandidates(agent);
        var startNewLine = ShouldStartNewWallLine(agent);
        var scored = startNewLine
            ? FindNewWallLineCandidates(agent)
            : lineCandidates;
        if (scored.Count == 0)
            scored = lineCandidates.Count > 0
                ? lineCandidates
                : FindNewWallLineCandidates(agent);
        if (scored.Count == 0)
            return false;

        // Score desc, then by cell. List.Sort is unstable, so equal-score
        // candidates would otherwise be ordered by wall-index layout, which can
        // differ between an organic world and one restored from a save.
        scored.Sort((left, right) =>
        {
            var cmp = right.Score.CompareTo(left.Score);
            if (cmp != 0)
                return cmp;
            var leftKey = left.Cell.Y * Width + left.Cell.X;
            var rightKey = right.Cell.Y * Width + right.Cell.X;
            return leftKey.CompareTo(rightKey);
        });
        var choiceCount = scored[0].Score >= 100
            ? Math.Min(scored.Count, 1 + (int)MathF.Round(agent.ExplorationDrive * 2f))
            : Math.Min(scored.Count, 1 + (int)MathF.Round(agent.ExplorationDrive * 3f));
        var chosen = scored[_rng.Next(choiceCount)].Cell;
        if (!CanBuildAt(chosen))
            return false;

        var path = _pathFinder.FindPath(agent.Cell, chosen);
        if (path.Count == 0 || path.Count > 18)
            return false;

        agent.TargetCell = chosen;
        agent.Path = path;
        agent.PathIndex = 0;
        agent.Action = AgentAction.Building;
        return true;
    }

    private List<(Point Cell, int Score)> FindWallLineCandidates(AgentState agent)
    {
        var candidates = new List<(Point Cell, int Score)>();
        var preferHorizontal = agent.PreferredBuildDirection % 2 == 0;

        // Use spatial index to get walls near the agent instead of scanning all walls
        var nearbyWalls = _wallIndex.GetWallsNear(agent.Cell, chunkRadius: 2).ToList();

        foreach (var wall in nearbyWalls)
        {
            foreach (var direction in CardinalDirections)
            {
                var candidate = new Point(wall.X + direction.X, wall.Y + direction.Y);
                if (!CanBuildAt(candidate) || _map.CountCardinalWalls(candidate) != 1)
                    continue;

                // Only endpoints are valid. This prevents a new block from
                // growing sideways out of the middle of an existing avenue.
                var runLength = CountWallRun(wall, new Point(-direction.X, -direction.Y));
                if (IsWall(wall.X - direction.X, wall.Y - direction.Y) && runLength < 2)
                    continue;

                var horizontal = direction.X != 0;
                var score = 180 + runLength * 34;
                if (horizontal == preferHorizontal)
                    score += 30;
                if (runLength >= 4)
                    score += 35;

                // Keep the floor beside a wall open. A wall that touches a
                // second wall on the side is a corner/choke point, not a street.
                if (HasSideWall(candidate, direction))
                    score -= 130;
                if (_map.CountAdjacentWalls(candidate) > 2)
                    score -= 80;

                candidates.Add((candidate, score));
            }
        }

        // Fallback: if no candidates found nearby, do a broader search
        if (candidates.Count == 0)
        {
            var allWalls = _wallIndex.GetWallsNear(agent.Cell, chunkRadius: 4).ToList();
            foreach (var wall in allWalls)
            {
                foreach (var direction in CardinalDirections)
                {
                    var candidate = new Point(wall.X + direction.X, wall.Y + direction.Y);
                    if (!CanBuildAt(candidate) || _map.CountCardinalWalls(candidate) != 1)
                        continue;

                    var runLength = CountWallRun(wall, new Point(-direction.X, -direction.Y));
                    if (IsWall(wall.X - direction.X, wall.Y - direction.Y) && runLength < 2)
                        continue;

                    var horizontal = direction.X != 0;
                    var score = 180 + runLength * 34;
                    if (horizontal == preferHorizontal)
                        score += 30;
                    if (runLength >= 4)
                        score += 35;

                    if (HasSideWall(candidate, direction))
                        score -= 130;
                    if (_map.CountAdjacentWalls(candidate) > 2)
                        score -= 80;

                    candidates.Add((candidate, score));
                }
            }
        }

        return candidates;
    }

    private List<(Point Cell, int Score)> FindNewWallLineCandidates(AgentState agent)
    {
        var candidates = new List<(Point Cell, int Score)>();
        var preferred = PreferredCardinalDirection(agent);
        var directions = new[]
        {
            preferred,
            new Point(-preferred.X, -preferred.Y),
            new Point(preferred.Y, -preferred.X),
            new Point(-preferred.Y, preferred.X),
        };

        for (var i = 0; i < directions.Length; i++)
        {
            var candidate = new Point(agent.Cell.X + directions[i].X, agent.Cell.Y + directions[i].Y);
            if (!CanBuildAt(candidate) || _map.CountCardinalWalls(candidate) != 0)
                continue;

            // A new avenue begins as a clean one-cell seed. Subsequent builders
            // will only extend its endpoints, so the seed cannot become a blob.
            var score = i == 0 ? 90 : 72 - i * 4;
            if (_map.WallCells.Count == 0)
                score += 80;
            candidates.Add((candidate, score));
        }

        return candidates;
    }

    private bool ShouldStartNewWallLine(AgentState agent)
    {
        if (_map.WallCells.Count == 0)
            return true;

        var alive = _agents.Count(candidate => candidate.Alive);
        if (alive < 6 || agent.ExplorationDrive < 0.55f)
            return false;

        // New avenues are uncommon. This preserves a dominant main wall while
        // allowing a growing population to form parallel streets over time.
        return _rng.NextDouble() < 0.045 + agent.ExplorationDrive * 0.035;
    }

    private int CountWallRun(Point start, Point direction)
    {
        var length = 0;
        var cell = start;
        while (IsWall(cell.X, cell.Y))
        {
            length++;
            cell = new Point(cell.X + direction.X, cell.Y + direction.Y);
        }

        return length;
    }

    private bool HasSideWall(Point candidate, Point direction)
    {
        var side = new Point(-direction.Y, direction.X);
        return IsWall(candidate.X + side.X, candidate.Y + side.Y) ||
               IsWall(candidate.X - side.X, candidate.Y - side.Y);
    }

    private static Point PreferredCardinalDirection(AgentState agent) => (agent.PreferredBuildDirection % 4) switch
    {
        0 => new Point(1, 0),
        1 => new Point(0, 1),
        2 => new Point(-1, 0),
        _ => new Point(0, -1),
    };

    private static readonly Point[] CardinalDirections =
    {
        new(1, 0),
        new(-1, 0),
        new(0, 1),
        new(0, -1),
    };

    private bool IsWall(int x, int y) => _map.InBounds(x, y) && _map[x, y] == CellType.Wall;

    private bool TryOpenPassage(AgentState agent)
    {
        if (agent.Energy < 20f || !_map.IsNearWall(agent.Cell) || agent.ExplorationDrive < 0.35f ||
            _rng.NextDouble() > 0.03 + agent.ExplorationDrive * 0.08)
            return false;

        var candidates = new List<Point>();
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var wall = new Point(agent.Cell.X + dx, agent.Cell.Y + dy);
                if (!_map.InBounds(wall) || _map[wall] != CellType.Wall)
                    continue;
                var neighbors = _map.CountAdjacentWalls(wall);
                if (neighbors >= 2 && neighbors <= 4)
                    candidates.Add(wall);
            }
        }

        if (candidates.Count == 0)
            return false;

        var chosen = candidates[_rng.Next(candidates.Count)];
        if (!_map.RemoveWallCell(chosen))
            return false;

        _chunks.UnregisterWall(chosen);
        _wallIndex.Rebuild(_map.WallCells);
        _pathFinder.InvalidatePathCache();
        agent.Energy = MathF.Max(0, agent.Energy - 8f);
        agent.BuildCooldown = 3f;
        agent.Action = AgentAction.Digging;
        agent.Path.Clear();
        agent.PathIndex = 0;
        WallBlocksRemoved++;
        ApplyLearning(agent, AgentAction.Digging, 0.25f);
        return true;
    }

    private bool CanBuildAt(Point cell, int ignoreAgentId = -1)
    {
        if (!_map.CanBuildWallCell(cell))
            return false;

        return !_agents.Any(agent => agent.Alive && agent.Id != ignoreAgentId && agent.Cell == cell) && FindFoodAt(cell) is null;
    }

    private int CurrentWallCapacity => Math.Min(
        MaxWallCells,
        128 + _agents.Count(agent => agent.Alive) * 24);

    private bool SetPathToNearestWall(AgentState agent)
    {
        // Reconstruct a path directly from the precomputed walkable approach
        // cells. The old implementation only used the grid as a yes/no gate
        // and then scanned every wall, which restored the O(agents x walls)
        // hotspot precisely when food delivery was busiest.
        if (_wallApproachDistanceGrid != null)
        {
            int agentIdx = agent.Cell.Y * Width + agent.Cell.X;
            if (agentIdx >= 0 && agentIdx < _wallApproachDistanceGrid.Length)
            {
                var path = _pathFinder.GetPathFromDistanceGrid(agent.Cell, _wallApproachDistanceGrid);
                if (path.Count > 0)
                {
                    agent.Path = path;
                    agent.PathIndex = 0;
                    agent.TargetCell = path[^1];
                    return true;
                }
            }
        }

        // Fallback to original search
        var fallbackApproach = _map.FindNearestWallApproach(agent.Cell);
        if (!fallbackApproach.HasValue)
            return false;

        SetPath(agent, fallbackApproach.Value);
        return true;
    }

    private void SetPath(AgentState agent, Point target)
    {
        agent.Path = _pathFinder.FindPath(agent.Cell, target);
        agent.PathIndex = 0;
        agent.TargetCell = target;
    }

    private bool TryStartExploration(AgentState agent)
    {
        if (agent.ExplorationCooldown > 0 || agent.Energy < 55f || agent.ExplorationDrive < 0.42f)
            return false;

        var roleBonus = agent.Role is AgentRole.Scout or AgentRole.Pathfinder ? 0.18f : 0f;
        var chance = 0.04f + agent.ExplorationDrive * 0.18f + agent.Intelligence * 0.06f + roleBonus;
        // First Cycle: prefer the closest beacon with a viable path. If that
        // point is blocked or too close to be a useful trip, try the next one.
        // Stable coordinate ties keep dictionary insertion order out of RNG.
        var beaconCandidates = _activeBeacons.Values
            .Where(beacon => Math.Abs(beacon.Cell.X - agent.Cell.X) + Math.Abs(beacon.Cell.Y - agent.Cell.Y) <= 30)
            .OrderBy(beacon => Math.Abs(beacon.Cell.X - agent.Cell.X) + Math.Abs(beacon.Cell.Y - agent.Cell.Y))
            .ThenBy(beacon => beacon.Cell.Y)
            .ThenBy(beacon => beacon.Cell.X)
            .ToArray();
        var beacon = beaconCandidates.FirstOrDefault();

        var beaconDistance = beacon is null
            ? int.MaxValue
            : Math.Abs(beacon.Cell.X - agent.Cell.X) + Math.Abs(beacon.Cell.Y - agent.Cell.Y);
        var beaconBoost = beacon is null ? 0f : 0.55f * beacon.Strength * (1f - beaconDistance / 31f);
        if (_rng.NextDouble() > Math.Min(0.95f, chance + beaconBoost))
            return false;

        foreach (var candidateBeacon in beaconCandidates)
        {
            var path = _pathFinder.FindPath(agent.Cell, candidateBeacon.Cell);
            if (path.Count < 4)
                continue;

            agent.Path = path;
            agent.PathIndex = 0;
            agent.TargetCell = candidateBeacon.Cell;
            agent.HasBeaconTarget = true;
            agent.BeaconTargetCell = candidateBeacon.Cell;
            agent.ExplorationCooldown = MathF.Max(4f, 12f - agent.Intelligence * 5f);
            agent.Action = AgentAction.Exploring;
            candidateBeacon.ExplorationTrips++;
            _beaconExplorationStarts++;
            if (!candidateBeacon.ImpactAnnounced)
            {
                candidateBeacon.ImpactAnnounced = true;
                RecordEvent(WorldEventType.PlayerIntervention,
                    $"Маяк сработал: исследователь #{agent.Id} начал путь к сигналу ({candidateBeacon.Cell.X},{candidateBeacon.Cell.Y}).",
                    WorldEventImportance.Major, agent.FactionId, candidateBeacon.Cell);
            }
            return true;
        }

        // Use precomputed exploration distance grid to pick target
        if (_explorationDistanceGrid != null)
        {
            int agentIdx = agent.Cell.Y * Width + agent.Cell.X;
            if (agentIdx >= 0 && agentIdx < _explorationDistanceGrid.Length)
            {
                // Find a target from low-activity chunks that's far enough
                var targets = _chunks.GetLowActivityChunkCenters();
                foreach (var target in targets)
                {
                    var distance = Math.Abs(target.X - agent.Cell.X) + Math.Abs(target.Y - agent.Cell.Y);
                    if (distance < 8 || target == agent.Cell)
                        continue;

                    var path = _pathFinder.FindPath(agent.Cell, target);
                    if (path.Count < 4)
                        continue;

                    agent.Path = path;
                    agent.PathIndex = 0;
                    agent.TargetCell = target;
                    agent.ExplorationCooldown = MathF.Max(4f, 12f - agent.Intelligence * 5f);
                    agent.Action = AgentAction.Exploring;
                    return true;
                }
            }
        }

        // Fallback to random search
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var target = _map.FindRandomFloorCell(_rng);
            var distance = Math.Abs(target.X - agent.Cell.X) + Math.Abs(target.Y - agent.Cell.Y);
            if (distance < 8 || target == agent.Cell)
                continue;

            var path = _pathFinder.FindPath(agent.Cell, target);
            if (path.Count < 4)
                continue;

            agent.Path = path;
            agent.PathIndex = 0;
            agent.TargetCell = target;
            agent.ExplorationCooldown = MathF.Max(4f, 12f - agent.Intelligence * 5f);
            agent.Action = AgentAction.Exploring;
            return true;
        }

        return false;
    }

    private bool TryStartMigration(AgentState agent)
    {
        if (CurrentEvent != "migration_wave" || EventTicksRemaining <= 0 ||
            agent.Action is AgentAction.Migrating or AgentAction.GoingToFood or AgentAction.GatheringFood ||
            agent.Energy < 65f || agent.ExplorationDrive < 0.55f)
            return false;

        var roleBonus = agent.Role is AgentRole.Scout or AgentRole.Pathfinder ? 0.12f : 0f;
        var chance = 0.025f + agent.ExplorationDrive * 0.06f + agent.Intelligence * 0.04f + roleBonus;
        if (_rng.NextDouble() > chance)
            return false;

        // Use precomputed migration distance grid to pick target
        if (_migrationDistanceGrid != null)
        {
            int agentIdx = agent.Cell.Y * Width + agent.Cell.X;
            if (agentIdx >= 0 && agentIdx < _migrationDistanceGrid.Length)
            {
                // Find a target from high-activity chunks that's far enough
                var targets = _chunks.GetHighActivityChunkCenters();
                foreach (var target in targets)
                {
                    var distance = Math.Abs(target.X - agent.Cell.X) + Math.Abs(target.Y - agent.Cell.Y);
                    if (distance < 15 || target == agent.Cell)
                        continue;

                    var path = _pathFinder.FindPath(agent.Cell, target);
                    if (path.Count < 8)
                        continue;

                    agent.Path = path;
                    agent.PathIndex = 0;
                    agent.TargetCell = target;
                    agent.ExplorationCooldown = MathF.Max(8f, 18f - agent.Intelligence * 6f);
                    agent.Action = AgentAction.Migrating;
                    return true;
                }
            }
        }

        // Fallback to random search
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var target = _map.FindRandomFloorCell(_rng);
            var distance = Math.Abs(target.X - agent.Cell.X) + Math.Abs(target.Y - agent.Cell.Y);
            if (distance < 15 || target == agent.Cell)
                continue;

            var path = _pathFinder.FindPath(agent.Cell, target);
            if (path.Count < 8)
                continue;

            agent.Path = path;
            agent.PathIndex = 0;
            agent.TargetCell = target;
            agent.ExplorationCooldown = MathF.Max(8f, 18f - agent.Intelligence * 6f);
            agent.Action = AgentAction.Migrating;
            return true;
        }

        return false;
    }

    private void TryBirth()
    {
        // One bounded attempt per local colony, with staggered deterministic phases.
        // Founders without a settlement share a bootstrap group.
        if (Tick % 10 != 0) return;
        var groups = _agents.Where(a => a.Alive).GroupBy(a => a.SettlementId).OrderBy(g => g.Key).ToArray();
        foreach (var group in groups)
        {
            var period = 180 + Math.Abs(group.Key % 5) * 20;
            if ((Tick + (group.Key + 1L) * 30) % period != 0) continue;
            TryColonyBirth(group.ToArray());
        }
    }

    private void TryColonyBirth(AgentState[] members)
    {
        var alive = _agents.Count(a => a.Alive);
        if (alive >= PopulationSafetyLimit) return;
        Ecology.BirthAttempts++;

        // Relax requirement: agents don't need to be near wall if there are few walls total
        // Allow birth if near wall OR if global wall count is low (agents still building settlement)
        var candidates = members
            .Where(a => (_map.IsNearWall(a.Cell) || _map.WallCells.Count < 10) && a.Energy > 75 && a.Age >= 120f)
            .OrderBy(a => a.Id)
            .ToArray();
        if (candidates.Length < 2) { Ecology.BirthBlockedParents++; return; }
        var offset = _rng.Next(candidates.Length);
        candidates = candidates.Skip(offset).Concat(candidates.Take(offset)).ToArray();

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
        { Ecology.BirthBlockedParents++; return; }

        var spawnCell = FindFreeHomeCell(firstParent.Cell);
        if (!spawnCell.HasValue)
        { Ecology.BirthBlockedSpace++; return; }

        if (!SpendLocalBirthFood(firstParent.Cell, members.Length))
        { Ecology.BirthBlockedFood++; return; }
        firstParent.Energy -= 8;
        secondParent.Energy -= 8;
        var child = new AgentState
        {
            Id = _nextAgentId++,
            FactionId = firstParent.FactionId,
            Cell = spawnCell.Value,
            TargetCell = spawnCell.Value,
            Action = AgentAction.Resting,
            RestTimer = 3f,
            Energy = 80f,
            BuildDrive = Mutate((firstParent.BuildDrive + secondParent.BuildDrive) / 2f),
            ExplorationDrive = Mutate((firstParent.ExplorationDrive + secondParent.ExplorationDrive) / 2f),
            RiskTolerance = Mutate((firstParent.RiskTolerance + secondParent.RiskTolerance) / 2f),
            LearningRate = Mutate((firstParent.LearningRate + secondParent.LearningRate) / 2f),
            Intelligence = Mutate((firstParent.Intelligence + secondParent.Intelligence) / 2f),
            PlanningSkill = Mutate((firstParent.PlanningSkill + secondParent.PlanningSkill) / 2f),
            SocialAwareness = Mutate((firstParent.SocialAwareness + secondParent.SocialAwareness) / 2f),
            FoodUtilityBias = MutateBias((firstParent.FoodUtilityBias + secondParent.FoodUtilityBias) / 2f),
            BuildUtilityBias = MutateBias((firstParent.BuildUtilityBias + secondParent.BuildUtilityBias) / 2f),
            ExploreUtilityBias = MutateBias((firstParent.ExploreUtilityBias + secondParent.ExploreUtilityBias) / 2f),
            RestUtilityBias = MutateBias((firstParent.RestUtilityBias + secondParent.RestUtilityBias) / 2f),
            PreferredBuildDirection = _rng.Next(8),
        };
        AssignRole(child);
        _agents.Add(child);
        _chunks.RegisterAgent(child.Cell, child.FactionId);
        child.PreviousCell = child.Cell;

        // Generation and lineage
        child.Generation = Math.Max(firstParent.Generation, secondParent.Generation) + 1;
        child.ParentId1 = firstParent.Id;
        child.ParentId2 = secondParent.Id;
        child.SettlementId = firstParent.SettlementId;
        if (_settlements.TryGetValue(child.SettlementId, out var birthSettlement)) birthSettlement.Births++;

        // Inherit elder wisdom: small bonus to biases from parents' ElderWisdomBonus
        float inheritedWisdom = (firstParent.ElderWisdomBonus + secondParent.ElderWisdomBonus) / 2f;
        if (inheritedWisdom > 0)
        {
            child.FoodUtilityBias = MathF.Min(2.5f, child.FoodUtilityBias + inheritedWisdom * 0.5f);
            child.BuildUtilityBias = MathF.Min(2.5f, child.BuildUtilityBias + inheritedWisdom * 0.5f);
            child.ExploreUtilityBias = MathF.Min(2.5f, child.ExploreUtilityBias + inheritedWisdom * 0.5f);
            child.RestUtilityBias = MathF.Min(2.5f, child.RestUtilityBias + inheritedWisdom * 0.5f);
        }

        EnsureFaction(child.FactionId).Births++;
        Births++;
        _birthsThisTick++;
        if (Births == 1 && CanRecordOnce(WorldEventType.FirstBirth))
        {
            MarkRecordedOnce(WorldEventType.FirstBirth);
            RecordEvent(WorldEventType.FirstBirth,
                "Родился первый агент нового поколения",
                WorldEventImportance.Major, child.FactionId, child.Cell);
        }
        // Chronicle: generation milestone
        if (child.Generation % 5 == 0 && _recordedGenerations.Add(child.Generation))
        {
            RecordEvent(WorldEventType.FactionGoalChanged,
                $"Поколение {child.Generation}: наследуется мудрость старейшин",
                WorldEventImportance.Major, child.FactionId, child.Cell);
        }
        InvalidateStats();
        // Local phase scheduling replaces the former world-wide birth cooldown.
    }

    // Track which generation milestones we've recorded to avoid spam
    private readonly HashSet<int> _recordedGenerations = new();

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
        foreach (var cell in _foodStorage.Keys
                     .OrderBy(point => point.Y)
                     .ThenBy(point => point.X)
                     .ToArray())
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
            var agent = new AgentState
            {
                Id = _nextAgentId++,
                FactionId = i % 2,
                Cell = spawnCell,
                TargetCell = spawnCell,
                Action = AgentAction.SearchingFood,
                BuildDrive = (float)_rng.NextDouble(),
                ExplorationDrive = (float)_rng.NextDouble(),
                RiskTolerance = (float)_rng.NextDouble(),
                LearningRate = 0.25f + (float)_rng.NextDouble() * 0.75f,
                Intelligence = 0.25f + (float)_rng.NextDouble() * 0.75f,
                PlanningSkill = (float)_rng.NextDouble(),
                SocialAwareness = (float)_rng.NextDouble(),
                PreferredBuildDirection = _rng.Next(8),
            };
            AssignRole(agent);
            _agents.Add(agent);
            _chunks.RegisterAgent(agent.Cell, agent.FactionId);
            agent.PreviousCell = agent.Cell;
        }
    }

    private void AssignRole(AgentState agent)
    {
        var scores = new[]
        {
            (Role: AgentRole.Forager, Score: 0.35f + agent.RiskTolerance * 0.2f + agent.LearningRate * 0.25f),
            (Role: AgentRole.Builder, Score: agent.BuildDrive * 0.85f + agent.PlanningSkill * 0.2f),
            (Role: AgentRole.Scout, Score: agent.ExplorationDrive * 0.8f + agent.Intelligence * 0.25f),
            (Role: AgentRole.Keeper, Score: agent.PlanningSkill * 0.7f + agent.SocialAwareness * 0.25f),
            (Role: AgentRole.Pathfinder, Score: agent.Intelligence * 0.45f + agent.ExplorationDrive * 0.45f),
        };

        var best = scores.OrderByDescending(entry => entry.Score).First();
        agent.Role = best.Score < 0.42f ? AgentRole.Generalist : best.Role;
        agent.RoleExperience = MathF.Max(agent.RoleExperience, best.Score < 0.42f ? 0.05f : 0.2f);
    }

    private void UpdateRoleFromExperience(AgentState agent)
    {
        if (agent.RoleExperience < 0.3f)
            return;

        var preferred = agent.Action switch
        {
            AgentAction.GatheringFood or AgentAction.GoingToFood or AgentAction.ReturningToWall => AgentRole.Forager,
            AgentAction.Building => AgentRole.Builder,
            AgentAction.Exploring => AgentRole.Scout,
            AgentAction.Migrating => AgentRole.Pathfinder,
            AgentAction.StoringFood => AgentRole.Keeper,
            _ => agent.Role,
        };

        if (preferred != agent.Role && _rng.NextDouble() < 0.02 + agent.LearningRate * 0.04)
        {
            agent.Role = preferred;
            agent.RoleExperience = 0.25f;
            InvalidateStats();
        }
    }

    private float GetRoleExperienceRate(AgentState agent) => agent.Action switch
    {
        AgentAction.GatheringFood or AgentAction.StoringFood => 0.0018f,
        AgentAction.Building => 0.0022f,
        AgentAction.Exploring or AgentAction.Migrating => 0.002f,
        _ => 0.0002f,
    } * (0.7f + agent.LearningRate * 0.6f);

    private int CountRole(AgentRole role) => _agents.Count(agent => agent.Alive && agent.Role == role);

    private void TryTriggerWorldEvent()
    {
        if (_eventCooldown > 0 || AlivePopulation < 4 || _rng.NextDouble() > 0.38)
            return;

        _eventCooldown = 1200;
        switch (_rng.Next(3))
        {
            case 0:
                var occupied = new HashSet<Point>(_food.Where(node => node.Amount > 0).Select(node => node.Cell));
                for (var i = 0; i < 10; i++)
                {
                    var cell = FindRandomFloorCell(occupied);
                    if (cell.HasValue)
                    {
                        occupied.Add(cell.Value);
                        AddFoodNode(cell.Value, _rng.Next(3, 8));
                    }
                }

                ResourceSurges++;
                CurrentEvent = "food_bloom";
                EventTicksRemaining = 240;
                break;

            case 1:
                foreach (var node in _food.Where(node => node.Amount > 0).ToArray())
                {
                    if (_rng.NextDouble() < 0.32 && node.Amount > 1)
                    {
                        node.Amount--;
                        ConsumeBloomFood(node, harvestedByAgents: false);
                    }
                }

                ScarcityEvents++;
                CurrentEvent = "scarcity";
                EventTicksRemaining = 300;
                break;

            default:
                MigrationWaves++;
                CurrentEvent = "migration_wave";
                EventTicksRemaining = 300;
                RecordEvent(WorldEventType.MigrationStarted,
                    "Началась миграционная волна",
                    WorldEventImportance.Major);
                break;
        }
    }

    private float Mutate(float value)
    {
        var mutation = (float)(_rng.NextDouble() * 0.24 - 0.12);
        return Math.Clamp(value + mutation, 0.05f, 0.95f);
    }

    private float MutateBias(float value)
    {
        var mutation = (float)(_rng.NextDouble() * 0.36 - 0.18);
        return Math.Clamp(value + mutation, -2.5f, 2.5f);
    }

    private void GenerateFood()
    {
        var occupied = new HashSet<Point>();
        for (var i = 0; i < 300; i++)
        {
            var cell = FindRandomFloorCell(occupied);
            if (!cell.HasValue)
                break;

            occupied.Add(cell.Value);
            AddFoodNode(cell.Value, _rng.Next(3, 9));
        }
    }

    private void RegrowFood()
    {
        // Aggressive regrowth - add food until we have enough total amount
        int totalFoodAmount = _food.Where(n => n.Amount > 0).Sum(n => n.Amount);
        if (totalFoodAmount >= 900)
            return;

        var occupied = new HashSet<Point>(_food.Where(n => n.Amount > 0).Select(n => n.Cell));

        // Add multiple food nodes per regrowth cycle
        for (int i = 0; i < 8; i++)
        {
            var cell = FindRandomFloorCell(occupied);
            if (!cell.HasValue)
                break;
            if (IsDroughtCell(cell.Value)) continue;
            occupied.Add(cell.Value);
            AddFoodNode(cell.Value, _rng.Next(4, 10));
        }
    }

    private void AddFoodNode(Point cell, int amount)
    {
        if (_foodByCell.TryGetValue(cell, out var existing))
        {
            var wasDepleted = existing.Amount <= 0;
            existing.Amount += amount;
            if (wasDepleted && existing.Amount > 0)
                _chunks.RegisterFood(cell);
            return;
        }

        var node = new ResourceNode { Cell = cell, Amount = amount };
        _food.Add(node);
        _foodByCell[cell] = node;
        _resourceIndex.Add(node);
        _chunks.RegisterFood(cell);
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
            ClearInsightRouteAttribution(agent);
            var node = FindFoodAt(target);
            if (node is null || node.Amount <= 0 || !node.CanReserve(agent.Id))
                return false;

            ReleaseFoodReservation(agent);
            node.ReservedBy.Add(agent.Id);
            agent.FoodTargetCell = target;
            agent.HasFoodTarget = true;

            // For food, always use FindPath to the specific reserved target
            // Distance grid finds NEAREST target, not necessarily the reserved one
            agent.Path = _pathFinder.FindPath(agent.Cell, target);
            agent.TargetCell = target;  // Also update TargetCell for UpdateAgentState check

            if (agent.Path.Count > 0)
            {
                if (agent.HasInsightFoodClue && agent.InsightFoodCell == target)
                {
                    _insightFoodRoutesStarted++;
                    agent.InsightRouteCell = target;
                    agent.HasInsightRouteAttribution = true;
                    agent.InsightRouteArrived = false;
                    agent.HasInsightFoodClue = false;
                }
                return true;
            }

            ReleaseFoodReservation(agent);
            return false;
        }

    private void ReleaseFoodReservation(AgentState agent)
    {
        if (!agent.HasFoodTarget)
            return;

        if (_foodByCell.TryGetValue(agent.FoodTargetCell, out var node))
            node.ReservedBy.Remove(agent.Id);
        agent.HasFoodTarget = false;
    }

    private Point? FindNearestFood(AgentState agent)
    {
        // Use spatial index for efficient nearby food search
        var nearby = _resourceIndex.FindNearbyExpanding(agent.Cell, agent.Id, minResults: 1, startRadius: 3, maxRadius: 30);

        if (nearby.Count == 0)
            return null;

        var options = new List<(Point Cell, int Score)>();
        foreach (var (node, distance) in nearby)
        {
            if (node.Amount <= 0 || !node.CanReserve(agent.Id))
                continue;

            var reservationPenalty = node.ReservedBy.Contains(agent.Id) ? 0 : node.ReservedBy.Count * 12;
            var rememberedFoodConfidence = GetShoutMemoryConfidenceAt(agent, ShoutType.Food, node.Cell);
            var knownFoodConfidence = agent.HasKnownFood && agent.KnownFoodCell == node.Cell
                ? agent.FoodKnowledge
                : 0f;
            var learnedBonus = (int)((25f + agent.Intelligence * 20f) * MathF.Max(knownFoodConfidence, rememberedFoodConfidence));
            var rememberedDangerConfidence = GetShoutMemoryConfidenceAt(agent, ShoutType.Danger, node.Cell);
            var knownDangerConfidence = agent.HasDangerMemory && agent.KnownDangerCell == node.Cell
                ? agent.DangerKnowledge
                : 0f;
            var dangerPenalty = (int)(35f * MathF.Max(knownDangerConfidence, rememberedDangerConfidence));
            var personalBias = Math.Abs((node.Cell.X * 31 + node.Cell.Y * 17 + agent.Id * 13) % 9);
            var planningDiscount = (int)(agent.PlanningSkill * Math.Min(distance, 12) * 0.2f);
            var roleDiscount = agent.Role == AgentRole.Forager ? Math.Min(8, distance / 3) : 0;
            var score = distance + reservationPenalty + dangerPenalty +
                        (int)(personalBias * (1f - agent.ExplorationDrive)) - learnedBonus - planningDiscount - roleDiscount;
            options.Add((node.Cell, score));
        }

        if (options.Count == 0)
            return null;

        options.Sort((left, right) => left.Score.CompareTo(right.Score));
        var choiceCount = Math.Min(
            options.Count,
            1 + (int)MathF.Round((1f - agent.Intelligence) * 3f + agent.ExplorationDrive * 3f));
        return options[_rng.Next(choiceCount)].Cell;
    }

    private void MarkFoodFailure(AgentState agent)
    {
        ClearInsightRouteAttribution(agent);
        agent.FailedFoodTrips++;
        LearnShoutOutcome(agent, ShoutType.Food, successful: false, outcomeCell: agent.FoodTargetCell);
        LearnShoutOutcome(agent, ShoutType.Danger, successful: true, outcomeCell: agent.FoodTargetCell);
        ApplyLearning(agent, AgentAction.SearchingFood, -0.55f);
        agent.FoodKnowledge = MathF.Max(0, agent.FoodKnowledge - (0.08f + agent.LearningRate * 0.12f));
        agent.KnownDangerCell = agent.FoodTargetCell;
        agent.HasDangerMemory = true;
        agent.DangerKnowledge = MathF.Min(1f, agent.DangerKnowledge + 0.18f + agent.LearningRate * 0.08f);
        agent.RouteKnowledge = MathF.Max(0, agent.RouteKnowledge - 0.06f);
        if (agent.HasKnownFood && agent.KnownFoodCell == agent.FoodTargetCell && agent.FoodKnowledge <= 0.05f)
            agent.HasKnownFood = false;
    }

    private static void ClearInsightRouteAttribution(AgentState agent)
    {
        agent.HasInsightRouteAttribution = false;
        agent.InsightRouteArrived = false;
        agent.InsightRouteCell = Point.Zero;
    }

    private void ConsumeBloomFood(ResourceNode node, bool harvestedByAgents = true)
    {
        if (_activeBlooms.TryGetValue(node.Cell, out var bloom) && bloom.RemainingBoost > 0)
        {
            bloom.RemainingBoost--;
            if (harvestedByAgents)
            {
                bloom.HarvestedUnits++;
                _bloomFoodHarvested++;
                if (!bloom.ImpactAnnounced)
                {
                    bloom.ImpactAnnounced = true;
                    RecordEvent(WorldEventType.PlayerIntervention,
                        $"Цветение дало пищу: житель собрал первый ресурс у ({node.Cell.X},{node.Cell.Y}).",
                        WorldEventImportance.Major, -1, node.Cell);
                }
            }
        }
    }

    private ResourceNode? FindFoodAt(Point cell)
    {
        return _foodByCell.TryGetValue(cell, out var node) && node.Amount > 0 ? node : null;
    }

    #region Settlement System

    /// <summary>
    /// Updates settlements: detects new settlements, merges nearby ones,
    /// updates stats, and handles abandonment.
    /// </summary>
    private void UpdateSettlements()
    {
        // Clear chunk settlement assignments
        foreach (var settlement in _settlements.Values)
        {
            settlement.Population = 0;
            settlement.WallCount = 0;
            settlement.FoodStored = 0;
            settlement.ElderCount = 0;
        }

        // Assign agents to nearest settlement or create new ones
        foreach (var agent in _agents)
        {
            if (!agent.Alive)
                continue;

            int settlementId = FindOrCreateSettlement(agent);
            agent.SettlementId = settlementId;
            if (settlementId >= 0)
            {
                var settlement = _settlements[settlementId];
                settlement.Population++;
                if (agent.IsElder)
                    settlement.ElderCount++;
                settlement.LastActiveTick = Tick;
            }
        }

        // Count walls and food per settlement
        foreach (var wallCell in _map.WallCells)
        {
            var settlementId = FindNearestSettlement(wallCell);
            if (settlementId >= 0)
                _settlements[settlementId].WallCount++;
        }

        foreach (var storage in _foodStorage)
        {
            var settlementId = FindNearestSettlement(storage.Key);
            if (settlementId >= 0)
                _settlements[settlementId].FoodStored += storage.Value;
        }

        // Recalculate centers and cohesion (ordered by Id so any Chronicle
        // events produced below keep a load-stable order)
        foreach (var settlement in _settlements.Values.OrderBy(s => s.Id))
        {
            if (settlement.Population > 0)
            {
                UpdateSettlementCenter(settlement);
                settlement.Cohesion = CalculateSettlementCohesion(settlement);
                settlement.AverageEnergy = CalculateSettlementAverageEnergy(settlement);
                settlement.Generation = CalculateSettlementGeneration(settlement);
            }
            else if (!settlement.IsAbandoned)
            {
                // Check if settlement should be abandoned (no population for 2000 ticks).
                // Ordered by Id so abandonment events keep a load-stable order.
                if (Tick - settlement.LastActiveTick > 2000)
                {
                    settlement.IsAbandoned = true;
                    RecordEvent(WorldEventType.SettlementAbandoned,
                        $"Поселение #{settlement.Id} фракции {settlement.FactionId} покинуто (жило {Tick - settlement.FoundedTick} тиков)",
                        WorldEventImportance.Major, settlement.FactionId, settlement.CenterCell);
                }
            }
        }

        // Remove abandoned settlements older than 5000 ticks
        var toRemove = _settlements.Where(kv => kv.Value.IsAbandoned && Tick - kv.Value.LastActiveTick > 5000).Select(kv => kv.Key).ToList();
        foreach (var id in toRemove)
            _settlements.Remove(id);

        // Record milestones. Iterating by Id keeps the Chronicle append order
        // independent of Dictionary layout, so a reloaded world records the
        // same event sequence as the original.
        foreach (var settlement in _settlements.Values.OrderBy(s => s.Id))
        {
            if (!settlement.IsAbandoned && settlement.Population > 0)
            {
                CheckSettlementMilestones(settlement);
            }
        }
    }

    private int FindOrCreateSettlement(AgentState agent)
    {
        // Try to find existing settlement within range
        var existing = _settlements.Values
            .Where(s => s.FactionId == agent.FactionId && !s.IsAbandoned)
            .OrderBy(s => Math.Abs(s.CenterCell.X - agent.Cell.X) + Math.Abs(s.CenterCell.Y - agent.Cell.Y))
            .ThenBy(s => s.Id)
            .FirstOrDefault();

        if (existing != null)
        {
            var distance = Math.Abs(existing.CenterCell.X - agent.Cell.X) + Math.Abs(existing.CenterCell.Y - agent.Cell.Y);
            if (distance <= 30) // Increased settlement radius for stability
                return existing.Id;
        }

        // Create new settlement if there are walls nearby AND enough agents (both required for stability)
        // AND no existing settlement is close enough
        var nearbyWalls = _map.WallCells.Count(w => Math.Abs(w.X - agent.Cell.X) + Math.Abs(w.Y - agent.Cell.Y) <= 15);
        var nearbyAgents = _agents.Count(a => a.Alive && a.FactionId == agent.FactionId &&
            Math.Abs(a.Cell.X - agent.Cell.X) + Math.Abs(a.Cell.Y - agent.Cell.Y) <= 15);

        // Only create if we have a solid core AND no existing settlement nearby
        if (nearbyWalls >= 5 && nearbyAgents >= 10)
        {
            // Double-check no existing settlement is close
            var tooClose = _settlements.Values.Any(s => s.FactionId == agent.FactionId && !s.IsAbandoned &&
                Math.Abs(s.CenterCell.X - agent.Cell.X) + Math.Abs(s.CenterCell.Y - agent.Cell.Y) <= 30);

            if (!tooClose)
            {
                var newId = _nextSettlementId++;
                var settlement = new SettlementState
                {
                    Id = newId,
                    FactionId = agent.FactionId,
                    CenterCell = agent.Cell,
                    FoundedTick = Tick,
                    LastActiveTick = Tick,
                };
                _settlements[newId] = settlement;

                RecordEvent(WorldEventType.SettlementFounded,
                    $"Основано поселение #{newId} фракции {agent.FactionId} (стены: {nearbyWalls}, агенты: {nearbyAgents})",
                    WorldEventImportance.Major, agent.FactionId, agent.Cell);
                return newId;
            }
        }

        return -1; // No settlement
    }

    private int FindNearestSettlement(Point cell)
    {
        var nearest = _settlements.Values
            .Where(s => !s.IsAbandoned && s.Population > 0)
            .OrderBy(s => Math.Abs(s.CenterCell.X - cell.X) + Math.Abs(s.CenterCell.Y - cell.Y))
            .ThenBy(s => s.Id)
            .FirstOrDefault();

        if (nearest != null)
        {
            var distance = Math.Abs(nearest.CenterCell.X - cell.X) + Math.Abs(nearest.CenterCell.Y - cell.Y);
            if (distance <= 30)
                return nearest.Id;
        }
        return -1;
    }

    private void UpdateSettlementCenter(SettlementState settlement)
    {
        var agents = _agents.Where(a => a.Alive && a.SettlementId == settlement.Id).ToList();
        if (agents.Count == 0)
            return;

        // Weighted center based on agent positions
        int sumX = 0, sumY = 0;
        foreach (var agent in agents)
        {
            sumX += agent.Cell.X;
            sumY += agent.Cell.Y;
        }
        settlement.CenterCell = new Point(sumX / agents.Count, sumY / agents.Count);
    }

    private float CalculateSettlementCohesion(SettlementState settlement)
    {
        var members = _agents.Where(a => a.Alive && a.SettlementId == settlement.Id).ToArray();
        if (members.Length < 2) return 1f;
        // Manhattan pair distances are the sum of independent X/Y distances.
        // Sorted prefix sums compute the exact same integer total in O(n log n).
        var xs = members.Select(a => a.Cell.X).Order().ToArray();
        var ys = members.Select(a => a.Cell.Y).Order().ToArray();
        long distance = 0, prefixX = 0, prefixY = 0;
        for (var i = 0; i < members.Length; i++)
        {
            distance += (long)xs[i] * i - prefixX + (long)ys[i] * i - prefixY;
            prefixX += xs[i];
            prefixY += ys[i];
        }
        var pairs = (long)members.Length * (members.Length - 1) / 2;
        return MathF.Max(0f, 1f - (float)distance / pairs / 30f);
    }

    private float CalculateSettlementAverageEnergy(SettlementState settlement)
    {
        var agents = _agents.Where(a => a.Alive && a.SettlementId == settlement.Id).ToList();
        if (agents.Count == 0)
            return 0f;
        return agents.Average(a => a.Energy);
    }

    private int CalculateSettlementGeneration(SettlementState settlement)
    {
        var agents = _agents.Where(a => a.Alive && a.SettlementId == settlement.Id).ToList();
        if (agents.Count == 0)
            return 1;
        return agents.Max(a => a.Generation);
    }

    private void CheckSettlementMilestones(SettlementState settlement)
    {
        // Milestones at population: 10, 25, 50, 100, 200
        int[] milestones = { 10, 25, 50, 100, 200 };
        foreach (var milestone in milestones)
        {
            if (settlement.Population >= milestone)
            {
                var key = settlement.Id * 1000 + milestone;
                if (_recordedSettlementMilestones.Add(key))
                {
                    RecordEvent(WorldEventType.SettlementMilestone,
                        $"Поселение #{settlement.Id} фракции {settlement.FactionId} достигло {milestone} жителей (поколение {settlement.Generation}, старейшин: {settlement.ElderCount})",
                        WorldEventImportance.Major, settlement.FactionId, settlement.CenterCell);
                }
            }
        }
    }

    #endregion

    // ========== First Cycle: Player Intervention Methods ==========

    /// <summary>
    /// Queues an intervention command from the player. Command is applied at the next tick boundary
    /// for determinism. Returns (success, reason).
    /// </summary>
    public (bool Success, string Reason) QueueIntervention(InterventionType type, Point cell)
    {
        if (type is InterventionType.None || !Enum.IsDefined(type))
            return (false, "Unknown intervention");

        int cost = GetInterventionCost(type);

        // Validate cell bounds
        if (cell.X < 0 || cell.X >= Width || cell.Y < 0 || cell.Y >= Height)
        {
            return (false, "Cell out of bounds");
        }

        // Check Resonance
        var reservedResonance = _pendingInterventions.Sum(command => command.Cost);
        if (_resonance - reservedResonance < cost)
        {
            return (false, $"Not enough Resonance (need {cost}, available {_resonance - reservedResonance})");
        }

        // A Bloom targets an existing nearby source when one is present. Use
        // that source cell as the effect identity so adjacent clicks cannot
        // stack incompatible rollback state on a single node.
        if (type == InterventionType.Bloom)
        {
            var bloomTarget = FindBloomTarget(cell);
            var effectCell = bloomTarget?.Cell ?? cell;
            if (_activeBlooms.ContainsKey(effectCell))
                return (false, "Bloom already active at this food source");
            if (_pendingInterventions.Any(command =>
                    command.Type == InterventionType.Bloom &&
                    (FindBloomTarget(command.Cell)?.Cell ?? command.Cell) == effectCell))
                return (false, "Bloom already queued for this food source");
            if (bloomTarget is null && !_map.IsWalkable(cell))
                return (false, "Invalid cell for food source");
        }
        if (type == InterventionType.Beacon)
        {
            if (!_map.IsWalkable(cell))
                return (false, "Beacon must be placed on a walkable cell");
            if (_activeBeacons.ContainsKey(cell))
                return (false, "Beacon already active at this cell");
            if (_pendingInterventions.Any(command => command.Type == InterventionType.Beacon && command.Cell == cell))
                return (false, "Beacon already queued at this cell");
        }

        if (type == InterventionType.Passage)
        {
            if (_map[cell] != CellType.Wall) return (false, "Choose a wall cell");
            if (_pendingInterventions.Any(command => command.Type == type && command.Cell == cell))
                return (false, "Passage already queued here");
        }

        var command = new InterventionCommand
        {
            Type = type,
            Cell = cell,
            Cost = cost,
            RequestedTick = Tick + 1 // Apply at next tick boundary
        };

        _pendingInterventions.Enqueue(command);
        return (true, "Queued");
    }

    private int GetInterventionCost(InterventionType type)
    {
        return type switch
        {
            InterventionType.Bloom => 1,
            InterventionType.Beacon => 1,
            InterventionType.InsightPulse => 1,
            InterventionType.Passage => 2,
            _ => 0
        };
    }

    private ResourceNode? FindBloomTarget(Point requestedCell)
    {
        if (_foodByCell.TryGetValue(requestedCell, out var exact))
            return exact;

        ResourceNode? bestNode = null;
        var bestDistance = int.MaxValue;
        foreach (var node in _food)
        {
            var distance = Math.Abs(node.Cell.X - requestedCell.X) + Math.Abs(node.Cell.Y - requestedCell.Y);
            if (distance > 2)
                continue;

            var better = distance < bestDistance ||
                distance == bestDistance &&
                (bestNode is null || node.Cell.Y < bestNode.Cell.Y ||
                 node.Cell.Y == bestNode.Cell.Y && node.Cell.X < bestNode.Cell.X);
            if (!better)
                continue;

            bestNode = node;
            bestDistance = distance;
        }

        return bestNode;
    }

    private void ProcessPendingInterventions()
    {
        while (_pendingInterventions.Count > 0 && _pendingInterventions.Peek().RequestedTick <= Tick)
        {
            var command = _pendingInterventions.Dequeue();
            ApplyIntervention(command);
        }
    }

    private void ApplyIntervention(InterventionCommand command)
    {
        bool success = false;
        string reason = string.Empty;

        // Double-check resonance at application time (in case it changed)
        if (_resonance < command.Cost)
        {
            success = false;
            reason = "Insufficient Resonance at application time";
        }
        else
        {
            switch (command.Type)
            {
                case InterventionType.Bloom:
                    success = ApplyBloom(command.Cell, out reason);
                    break;
                case InterventionType.Beacon:
                    success = ApplyBeacon(command.Cell, out reason);
                    break;
                case InterventionType.InsightPulse:
                    success = ApplyInsightPulse(command.Cell, out reason);
                    break;
                case InterventionType.Passage:
                    success = ApplyPassage(command.Cell, out reason);
                    break;
            }

            if (success)
            {
                _resonance -= command.Cost;
                _totalResonanceSpent += command.Cost;
                _successfulInterventions++;
            }
        }

        if (!success)
            _failedInterventions++;

        // Log the intervention
        _interventionLog.Add(new InterventionLogEntry
        {
            Tick = Tick,
            Type = command.Type,
            Cell = command.Cell,
            Cost = success ? command.Cost : 0,
            Success = success,
            Reason = reason
        });

        // Record Chronicle event
        if (success)
        {
            RecordInterventionEvent(command.Type, command.Cell, true);
        }
    }

    private bool ApplyPassage(Point cell, out string reason)
    {
        Span<Point> neighbours = stackalloc Point[4];
        var neighbourCount = 0;
        var offsets = new[] { new Point(0, -1), new Point(1, 0), new Point(0, 1), new Point(-1, 0) };
        foreach (var offset in offsets)
        {
            var neighbour = new Point(cell.X + offset.X, cell.Y + offset.Y);
            if (_map.IsWalkable(neighbour))
                neighbours[neighbourCount++] = neighbour;
        }

        var connectsPreviouslySeparateAreas = false;
        for (var i = 0; i < neighbourCount && !connectsPreviouslySeparateAreas; i++)
        {
            for (var j = i + 1; j < neighbourCount; j++)
            {
                if (_pathFinder.FindPath(neighbours[i], neighbours[j]).Count == 0)
                {
                    connectsPreviouslySeparateAreas = true;
                    break;
                }
            }
        }

        if (!_map.RemoveWallCell(cell)) { reason = "Wall no longer exists"; return false; }
        _map[cell] = CellType.Door; // Walkable and excluded from future wall construction.
        if (connectsPreviouslySeparateAreas)
            _playerPassageCells.Add(cell);
        _chunks.UnregisterWall(cell);
        _wallIndex.Rebuild(_map.WallCells);
        _pathFinder.InvalidatePathCache();
        WallBlocksRemoved++;
        reason = connectsPreviouslySeparateAreas
            ? "Passage opened between previously disconnected areas"
            : "Passage opened; surrounding areas were already connected";
        return true;
    }

    private bool ApplyBloom(Point cell, out string reason)
    {
        var foodNode = FindBloomTarget(cell);
        var createdSource = false;
        var boostAmount = 30;

        if (foodNode is null)
        {
            if (!_map.IsWalkable(cell))
            {
                reason = "Invalid cell for food source";
                return false;
            }

            // A Bloom-created source is temporary too; it must not become a
            // permanent free resource after the effect ends.
            boostAmount = 20;
            foodNode = new ResourceNode { Cell = cell, Amount = boostAmount };
            _food.Add(foodNode);
            _foodByCell[cell] = foodNode;
            _resourceIndex.Add(foodNode);
            _chunks.RegisterFood(cell);
            createdSource = true;
        }
        else
        {
            if (_activeBlooms.ContainsKey(foodNode.Cell))
            {
                reason = "Bloom already active at this food source";
                return false;
            }

            var wasDepleted = foodNode.Amount <= 0;
            foodNode.Amount += boostAmount;
            if (wasDepleted)
                _chunks.RegisterFood(foodNode.Cell);
        }

        _activeBlooms[foodNode.Cell] = new BloomEffect
        {
            Cell = foodNode.Cell,
            RemainingTicks = 500,
            BoostAmount = boostAmount,
            OriginalAmount = createdSource ? 0 : foodNode.Amount - boostAmount,
            RemainingBoost = boostAmount,
            CreatedSource = createdSource,
            ImpactAnnounced = false,
        };

        reason = createdSource
            ? $"Temporary food source at {foodNode.Cell} created with {boostAmount} units for 500 ticks"
            : $"Food source at {foodNode.Cell} boosted by +{boostAmount} for 500 ticks";
        return true;
    }

    private void UpdateActiveInterventions()
    {
        // Update Blooms
        var expiredBlooms = new List<Point>();
        foreach (var kvp in _activeBlooms)
        {
            kvp.Value.RemainingTicks--;
            if (kvp.Value.RemainingTicks <= 0)
            {
                // Remove only the Bloom food that still remains. Restoring an
                // old absolute amount would recreate food agents already ate
                // and could erase legitimate later resource changes.
                if (_foodByCell.TryGetValue(kvp.Value.Cell, out var node))
                {
                    var previousAmount = node.Amount;
                    node.Amount = Math.Max(0, node.Amount - kvp.Value.RemainingBoost);
                    if (previousAmount > 0 && node.Amount == 0)
                        _chunks.UnregisterFood(node.Cell);
                }
                expiredBlooms.Add(kvp.Key);
            }
        }
        foreach (var cell in expiredBlooms)
        {
            _activeBlooms.Remove(cell);
        }

        // Update Beacons
        var expiredBeacons = new List<Point>();
        foreach (var kvp in _activeBeacons)
        {
            kvp.Value.RemainingTicks--;
            if (kvp.Value.RemainingTicks <= 0)
            {
                expiredBeacons.Add(kvp.Key);
            }
        }
        foreach (var cell in expiredBeacons)
        {
            _activeBeacons.Remove(cell);
        }
    }

    private bool ApplyBeacon(Point cell, out string reason)
    {
        if (!_map.IsWalkable(cell))
        {
            reason = "Beacon must be placed on a walkable cell";
            return false;
        }

        _activeBeacons[cell] = new BeaconEffect
        {
            Cell = cell,
            RemainingTicks = 200, // ~20 seconds at normal speed
            Strength = 1.0f,
            ImpactAnnounced = false,
        };

        reason = $"Beacon placed at {cell} for 200 ticks";
        return true;
    }

    private bool ApplyInsightPulse(Point cell, out string reason)
    {
        // Insight transfers a usable clue only when a nearby, eligible agent
        // has a walkable route to a live source. Two multi-source distance
        // grids bound this validation to O(map cells), rather than one path
        // search per agent/source pair.
        const int radius = 5;
        const int sourceRadius = 12;
        var recipients = _agents
            .Where(agent => agent.Alive && agent.Energy >= 20f &&
                Math.Abs(agent.Cell.X - cell.X) + Math.Abs(agent.Cell.Y - cell.Y) <= radius &&
                _map.IsWalkable(agent.Cell))
            .OrderBy(agent => Math.Abs(agent.Cell.X - cell.X) + Math.Abs(agent.Cell.Y - cell.Y))
            .ThenBy(agent => agent.Id)
            .ToList();

        if (recipients.Count == 0)
        {
            reason = "No eligible nearby agents can receive the pulse";
            return false;
        }

        var recipientCells = recipients
            .Select(agent => agent.Cell)
            .Distinct()
            .OrderBy(point => point.Y)
            .ThenBy(point => point.X)
            .ToArray();
        var distanceFromRecipients = _pathFinder.ComputeDistanceToNearestTarget(recipientCells);
        var source = _food
            .Where(node => node.Amount > 0 &&
                Math.Abs(node.Cell.X - cell.X) + Math.Abs(node.Cell.Y - cell.Y) <= sourceRadius)
            .OrderBy(node => Math.Abs(node.Cell.X - cell.X) + Math.Abs(node.Cell.Y - cell.Y))
            .ThenByDescending(node => node.Amount)
            .ThenBy(node => node.Cell.Y)
            .ThenBy(node => node.Cell.X)
            .FirstOrDefault(node => _map.InBounds(node.Cell) &&
                distanceFromRecipients[node.Cell.Y * Width + node.Cell.X] >= 0);

        if (source is null)
        {
            reason = "No live food source within 12 cells is reachable by nearby agents";
            return false;
        }

        var distanceFromSource = _pathFinder.ComputeDistanceToNearestTarget(new[] { source.Cell });
        var taught = 0;
        foreach (var agent in recipients)
        {
            var alreadyKnowsUsefulSource = agent.HasKnownFood && agent.KnownFoodCell == source.Cell && agent.FoodKnowledge >= 0.85f;
            if (alreadyKnowsUsefulSource)
                continue;

            var cellIndex = agent.Cell.Y * Width + agent.Cell.X;
            if (distanceFromSource[cellIndex] < 0)
                continue;
            if (taught >= 24)
                break;

            agent.KnownFoodCell = source.Cell;
            agent.HasKnownFood = true;
            agent.InsightFoodCell = source.Cell;
            agent.HasInsightFoodClue = true;
            agent.FoodKnowledge = MathF.Max(agent.FoodKnowledge, 0.85f);
            agent.RouteKnowledge = MathF.Max(agent.RouteKnowledge, 0.65f);
            taught++;
        }

        if (taught == 0)
        {
            reason = "No reachable nearby agent can benefit from this source";
            return false;
        }

        _insightAgentsTaught += taught;
        reason = $"Revealed food at {source.Cell} to {taught} nearby agents; they can now choose that route";
        return true;
    }

    private void RecordInterventionEvent(InterventionType type, Point cell, bool success)
    {
        string desc = type switch
        {
            InterventionType.Bloom => $"Player intervention: Bloom at {cell} — food source boosted",
            InterventionType.Beacon => $"Player intervention: Beacon at {cell} — attraction signal",
            InterventionType.InsightPulse => $"Player intervention: InsightPulse at {cell} — knowledge shared",
            InterventionType.Passage => $"Проход открыт ({cell.X},{cell.Y}) — новый путь для жителей",
            _ => $"Player intervention: {type} at {cell}"
        };

        RecordEvent(WorldEventType.PlayerIntervention,
            desc,
            WorldEventImportance.Major, -1, cell);
    }

    private void RecalculateScore()
    {
        // Population component: alive ticks, births vs deaths ratio
        float popRatio = Deaths > 0 ? (float)Births / Math.Max(1, Deaths) : Math.Min(10f, Births * 0.1f);
        _scorePopulationComponent = MathF.Min(100f, AlivePopulation * 0.5f + popRatio * 10f);

        // Food component: stockpile, starvation deaths, food variance
        int stockpile = _foodStorage.Values.Sum();
        float starvationRatio = Births > 0 ? (float)StarvationDeaths / Births : 0f;
        _scoreFoodComponent = MathF.Min(100f, stockpile * 0.2f + MathF.Max(0f, 50f - starvationRatio * 100f));

        // Crisis component: survived FoodCrisis events
        _scoreCrisisComponent = MathF.Min(100f,
            _foodCrisisRecoveredCount * 25f + MathF.Max(0f, 20f - _foodCrisisCount * 5f));

        // Settlement component: count, age, population stability
                int stableSettlements = _settlements.Values.Count(s => s.Population >= 5 && s.LastActiveTick > Tick - 1000);
                var settlementsWithPop = _settlements.Values.Where(s => s.Population > 0).ToList();
                int avgSettlementAge = settlementsWithPop.Count > 0
                    ? (int)settlementsWithPop.Average(s => Tick - s.FoundedTick)
                    : 0;
                _scoreSettlementComponent = MathF.Min(100f, stableSettlements * 15f + MathF.Min(50f, avgSettlementAge * 0.001f));

        // Provisional First Cycle score, not leaderboard-ready. Count only
        // observed outcomes: launches/route starts do not score; passage
        // impact is unique per opened cell so repeated crossings cannot farm it.
        var interventionOutcomeValue = _bloomFoodHarvested * 0.1f +
                                       _beaconArrivals * 2f +
                                       _insightFoodArrivals * 0.75f +
                                       _insightFoodHarvested * 1.25f +
                                       _announcedPassageImpactCells.Count * 3f;
        var impactPerResonance = _totalResonanceSpent > 0
            ? interventionOutcomeValue / _totalResonanceSpent
            : 0f;
        _scoreEfficiencyComponent = MathF.Min(100f, 10f * MathF.Log2(1f + impactPerResonance));

        _scoreTotal = (_scorePopulationComponent + _scoreFoodComponent + _scoreCrisisComponent +
                       _scoreSettlementComponent + _scoreEfficiencyComponent) / 5f;
    }
}
