using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Hollowbound.Simulation;

public sealed class AnalyticsLogger : IDisposable
{
    public const int SchemaVersion = 2;

    private readonly JsonSerializerOptions _jsonOptions = new();
    private readonly Stopwatch _sessionStopwatch = Stopwatch.StartNew();
    private readonly Dictionary<string, int> _kindCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _worldEventCounts = new(StringComparer.Ordinal);
    private StreamWriter? _writer;
    private long _lastSnapshotTick = -1;
    private int _recordsSinceFlush;
    private bool _wasCatchingUp;
    private bool _completed;
    private int _peakPopulation;
    private int _peakFoodStockpile;
    private int _peakWallBlocks;

    public string LogPath { get; }
    public string RunId { get; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

    public AnalyticsLogger()
    {
        try
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root))
                root = AppContext.BaseDirectory;

            var directory = Path.Combine(root, "Hollowbound", "logs");
            Directory.CreateDirectory(directory);
            LogPath = Path.Combine(directory, $"simulation-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.jsonl");
            _writer = new StreamWriter(LogPath, false, new UTF8Encoding(false), 16 * 1024);
        }
        catch
        {
            try
            {
                var fallbackDirectory = Path.Combine(Path.GetTempPath(), "Hollowbound", "logs");
                Directory.CreateDirectory(fallbackDirectory);
                LogPath = Path.Combine(fallbackDirectory, $"simulation-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.jsonl");
                _writer = new StreamWriter(LogPath, false, new UTF8Encoding(false), 16 * 1024);
            }
            catch
            {
                LogPath = string.Empty;
            }
        }
    }

    public void LogEvent(string kind, EmergentSimulationWorld world, float timeScale, bool paused, string? detail = null)
    {
        if (kind is "run_started" or "new_world")
            _lastSnapshotTick = -1;
        WriteRecord(kind, world, timeScale, paused, detail);
    }

    public void LogWorldEvents(EmergentSimulationWorld world)
    {
        if (_writer is null)
            return;

        foreach (var evt in world.DrainNewChronicleEvents())
        {
            try
            {
                var record = new
                {
                    timestamp_utc = DateTimeOffset.UtcNow,
                    schema_version = SchemaVersion,
                    run_id = RunId,
                    kind = "world_event",
                    event_type = evt.Type.ToString(),
                    tick = evt.Tick,
                    importance = evt.Importance.ToString(),
                    faction_id = evt.FactionId,
                    cell_x = evt.HasCell ? evt.Cell.X : (int?)null,
                    cell_y = evt.HasCell ? evt.Cell.Y : (int?)null,
                    description = evt.Description,
                };
                Count(_kindCounts, "world_event");
                Count(_worldEventCounts, evt.Type.ToString());
                _writer.WriteLine(JsonSerializer.Serialize(record, _jsonOptions));
                _recordsSinceFlush++;
            }
            catch
            {
                _writer.Dispose();
                _writer = null;
                return;
            }
        }

        if (_recordsSinceFlush >= 10)
        {
            _writer.Flush();
            _recordsSinceFlush = 0;
        }
    }

    public void MaybeWriteSnapshot(EmergentSimulationWorld world, float timeScale, bool paused)
    {
        // Log catch-up mode transitions as they happen (not gated by the
        // snapshot tick interval) so sustained slowdowns are attributable.
        if (_wasCatchingUp != world.IsCatchingUp)
        {
            _wasCatchingUp = world.IsCatchingUp;
            WriteRecord(
                world.IsCatchingUp ? "catching_up_started" : "catching_up_ended",
                world, timeScale, paused,
                $"backlog_s={world.BacklogSeconds:0.###}; requested_x={timeScale:0.#}");
        }

        if (world.Tick == _lastSnapshotTick || world.Tick - _lastSnapshotTick < 100)
            return;

        _lastSnapshotTick = world.Tick;
        WriteRecord("snapshot", world, timeScale, paused, null);
    }

    /// <summary>
    /// Records low-frequency client/render telemetry separately from the
    /// simulation snapshot. Rendering is owned by Game1, so keeping these
    /// fields in their own record avoids coupling the simulation model to the
    /// MonoGame thread while making manual performance investigations possible.
    /// </summary>
    public void LogRenderMetrics(
        EmergentSimulationWorld world,
        float timeScale,
        bool paused,
        float fps,
        double worldDrawMilliseconds,
        double uiDrawMilliseconds,
        int windowWidth,
        int windowHeight,
        float uiScale,
        int viewportWidth,
        int viewportHeight)
    {
        if (_writer is null)
            return;

        try
        {
            Count(_kindCounts, "render_metrics");
            var record = new
            {
                timestamp_utc = DateTimeOffset.UtcNow,
                schema_version = SchemaVersion,
                run_id = RunId,
                kind = "render_metrics",
                seed = world.Seed,
                tick = world.Tick,
                population = world.AlivePopulation,
                speed = timeScale,
                paused,
                fps = Math.Round(Math.Max(0f, fps), 2),
                world_draw_ms = Math.Round(Math.Max(0d, worldDrawMilliseconds), 3),
                ui_draw_ms = Math.Round(Math.Max(0d, uiDrawMilliseconds), 3),
                window_width = Math.Max(0, windowWidth),
                window_height = Math.Max(0, windowHeight),
                ui_scale = Math.Round(Math.Max(0f, uiScale), 2),
                viewport_width = Math.Max(0, viewportWidth),
                viewport_height = Math.Max(0, viewportHeight),
            };
            _writer.WriteLine(JsonSerializer.Serialize(record, _jsonOptions));
            _recordsSinceFlush++;
            if (_recordsSinceFlush >= 10)
            {
                _writer.Flush();
                _recordsSinceFlush = 0;
            }
        }
        catch
        {
            _writer.Dispose();
            _writer = null;
        }
    }

    private void WriteRecord(string kind, EmergentSimulationWorld world, float timeScale, bool paused, string? detail)
    {
        if (_writer is null)
            return;

        try
        {
            Observe(world);
            Count(_kindCounts, kind);
            var record = new
            {
                timestamp_utc = DateTimeOffset.UtcNow,
                schema_version = SchemaVersion,
                run_id = RunId,
                kind,
                detail,
                seed = world.Seed,
                tick = world.Tick,
                population = world.AlivePopulation,
                food_stockpile = world.FoodStockpile,
                wall_blocks = world.Map.WallCells.Count,
                passages = world.WallBlocksRemoved,
                births = world.Births,
                deaths = world.Deaths,
                starvation_deaths = world.StarvationDeaths,
                food_gathered = world.FoodGathered,
                food_consumed = world.FoodConsumed,
                colony_ecology = world.Ecology.Copy(),
                food_shared = world.FoodShared,
                knowledge_shared = world.KnowledgeShared,
                stored_piles = world.FoodStorage.Count,
                stored_food_units = world.StoredFoodUnits,
                average_energy = MathF.Round(world.AverageEnergy, 2),
                low_energy_agents = world.LowEnergyAgents,
                active_builders = world.ActiveBuilders,
                active_explorers = world.ActiveExplorers,
                average_intelligence = MathF.Round(world.AverageIntelligence, 3),
                average_learning = MathF.Round(world.AverageLearning, 3),
                decisions_made = world.DecisionsMade,
                learning_updates = world.LearningUpdates,
                positive_outcomes = world.PositiveOutcomes,
                negative_outcomes = world.NegativeOutcomes,
                average_food_policy_bias = MathF.Round(world.AverageFoodPolicyBias, 3),
                average_build_policy_bias = MathF.Round(world.AverageBuildPolicyBias, 3),
                average_explore_policy_bias = MathF.Round(world.AverageExplorePolicyBias, 3),
                factions = world.Factions.Select(faction => new
                {
                    id = faction.Id,
                    goal = faction.Goal.ToString(),
                    population = faction.Population,
                    average_energy = MathF.Round(faction.AverageEnergy, 2),
                    food_focus = MathF.Round(faction.FoodFocus, 3),
                    build_focus = MathF.Round(faction.BuildFocus, 3),
                    explore_focus = MathF.Round(faction.ExploreFocus, 3),
                    cohesion = MathF.Round(faction.Cohesion, 3),
                    territory_pressure = MathF.Round(faction.TerritoryPressure, 3),
                    food_stored = faction.FoodStored,
                    food_consumed = faction.FoodConsumed,
                    knowledge_shared = faction.KnowledgeShared,
                    births = faction.Births,
                    deaths = faction.Deaths,
                }).ToArray(),
                foragers = world.Foragers,
                builders = world.Builders,
                scouts = world.Scouts,
                keepers = world.Keepers,
                pathfinders = world.Pathfinders,
                current_event = world.CurrentEvent,
                event_ticks_remaining = world.EventTicksRemaining,
                resource_surges = world.ResourceSurges,
                scarcity_events = world.ScarcityEvents,
                migration_waves = world.MigrationWaves,
                speed = timeScale,
                paused,
                catching_up = world.IsCatchingUp,
                active_population = world.AlivePopulation,
                dead_agents_removed = world.DeadAgentsRemovedThisTick,
                lod_enabled = world.LodEnabled,
                active_agents = world.ActiveAgentCount,
                dormant_agents = world.DormantAgentCount,
                aggregated_agents = world.AggregatedAgentCount,
                active_agent_budget = world.ActiveAgentBudget,
                spatial_query_count = world.SpatialQueryCount,
                food_query_count = world.FoodQueryCount,
                wall_query_count = world.WallQueryCount,
                pathfinding_requests = world.PathfindingRequests,
                pathfinding_cache_hits = world.PathfindingCacheHits,
                distance_grid_requests = world.DistanceGridRequests,
                last_steps_processed = world.LastStepsProcessed,
                last_simulation_ms = Math.Round(world.LastSimulationMilliseconds, 3),
                catching_up_frames = world.CatchingUpFrames,
                catching_up_duration_seconds = Math.Round(world.CatchingUpSeconds, 3),
                effective_simulation_speed = Math.Round(world.LastEffectiveSimulationSpeed, 3),
                smoothed_actual_speed = Math.Round(world.SmoothedActualSpeed, 3),
                backlog_seconds = Math.Round(world.BacklogSeconds, 3),
                frame_budget_ms = world.FrameSimulationBudgetMilliseconds,
                catching_up_episodes = world.CatchingUpEpisodes,
                current_catching_up_seconds = Math.Round(world.CurrentCatchingUpForSeconds, 3),
                longest_catching_up_seconds = Math.Round(world.LongestCatchingUpEpisodeSeconds, 3),
                active_chunks = world.ActiveChunkCount,
                total_chunks = world.Chunks.TotalChunks,
                // First Cycle: Resonance and score
                resonance = world.Resonance,
                total_resonance_spent = world.TotalResonanceSpent,
                successful_interventions = world.SuccessfulInterventions,
                 failed_interventions = world.FailedInterventions,
                 shouts_made = world.ShoutsMade,
                 shouts_heard = world.ShoutsHeard,
                 food_shouts = world.FoodShouts,
                 danger_shouts = world.DangerShouts,
                 rally_shouts = world.RallyShouts,
                 shout_learning_events = world.ShoutLearningEvents,
                 successful_shout_lessons = world.SuccessfulShoutLessons,
                 failed_shout_lessons = world.FailedShoutLessons,
                 shout_reputation_records = world.ShoutReputationRecords,
                 shout_memory_records = world.ShoutMemoryRecords,
                 faction_shout_learning_events = world.FactionShoutLearningEvents,
                 active_shouts = world.RecentShouts.Count,
                 score_population = MathF.Round(world.ScorePopulationComponent, 2),
                score_food = MathF.Round(world.ScoreFoodComponent, 2),
                score_crisis = MathF.Round(world.ScoreCrisisComponent, 2),
                score_settlement = MathF.Round(world.ScoreSettlementComponent, 2),
                score_efficiency = MathF.Round(world.ScoreEfficiencyComponent, 2),
                score_total = MathF.Round(world.ScoreTotal, 2),
            };
            _writer.WriteLine(JsonSerializer.Serialize(record, _jsonOptions));
            _recordsSinceFlush++;
            if (kind != "snapshot" || _recordsSinceFlush >= 10)
            {
                _writer.Flush();
                _recordsSinceFlush = 0;
            }
        }
        catch
        {
            _writer.Dispose();
            _writer = null;
        }
    }

    /// <summary>
    /// Writes one bounded end-of-session record. It is intentionally separate
    /// from periodic snapshots so log consumers can distinguish the final
    /// state from the last sampled state.
    /// </summary>
    public void Complete(EmergentSimulationWorld world, float timeScale, bool paused, string reason = "completed")
    {
        if (_completed)
            return;

        _completed = true;
        _sessionStopwatch.Stop();
        if (_writer is null)
            return;

        try
        {
            Observe(world);
            var kindCounts = new Dictionary<string, int>(_kindCounts, StringComparer.Ordinal);
            Count(kindCounts, "session_summary");
            var record = new
            {
                timestamp_utc = DateTimeOffset.UtcNow,
                schema_version = SchemaVersion,
                run_id = RunId,
                kind = "session_summary",
                reason,
                duration_seconds = Math.Round(_sessionStopwatch.Elapsed.TotalSeconds, 3),
                seed = world.Seed,
                tick = world.Tick,
                population = world.AlivePopulation,
                peak_population = _peakPopulation,
                food_stockpile = world.FoodStockpile,
                peak_food_stockpile = _peakFoodStockpile,
                wall_blocks = world.Map.WallCells.Count,
                peak_wall_blocks = _peakWallBlocks,
                births = world.Births,
                deaths = world.Deaths,
                starvation_deaths = world.StarvationDeaths,
                food_gathered = world.FoodGathered,
                food_consumed = world.FoodConsumed,
                stored_food_units = world.StoredFoodUnits,
                decisions_made = world.DecisionsMade,
                colony_ecology = world.Ecology.Copy(),
                learning_updates = world.LearningUpdates,
                positive_outcomes = world.PositiveOutcomes,
                negative_outcomes = world.NegativeOutcomes,
                knowledge_shared = world.KnowledgeShared,
                successful_interventions = world.SuccessfulInterventions,
                failed_interventions = world.FailedInterventions,
                shouts_made = world.ShoutsMade,
                shouts_heard = world.ShoutsHeard,
                food_shouts = world.FoodShouts,
                danger_shouts = world.DangerShouts,
                rally_shouts = world.RallyShouts,
                shout_learning_events = world.ShoutLearningEvents,
                successful_shout_lessons = world.SuccessfulShoutLessons,
                failed_shout_lessons = world.FailedShoutLessons,
                shout_reputation_records = world.ShoutReputationRecords,
                shout_memory_records = world.ShoutMemoryRecords,
                faction_shout_learning_events = world.FactionShoutLearningEvents,
                score_total = MathF.Round(world.ScoreTotal, 2),
                settlements = world.SettlementCount,
                factions = world.Factions.Count,
                active_agents = world.ActiveAgentCount,
                dormant_agents = world.DormantAgentCount,
                aggregated_agents = world.AggregatedAgentCount,
                catching_up_episodes = world.CatchingUpEpisodes,
                longest_catching_up_seconds = Math.Round(world.LongestCatchingUpEpisodeSeconds, 3),
                event_counts = kindCounts,
                world_event_counts = new Dictionary<string, int>(_worldEventCounts, StringComparer.Ordinal),
            };
            _writer.WriteLine(JsonSerializer.Serialize(record, _jsonOptions));
            _writer.Flush();
        }
        catch
        {
            _writer.Dispose();
            _writer = null;
        }
    }

    private void Observe(EmergentSimulationWorld world)
    {
        _peakPopulation = Math.Max(_peakPopulation, world.AlivePopulation);
        _peakFoodStockpile = Math.Max(_peakFoodStockpile, world.FoodStockpile);
        _peakWallBlocks = Math.Max(_peakWallBlocks, world.Map.WallCells.Count);
    }

    private static void Count(Dictionary<string, int> counts, string key)
    {
        counts.TryGetValue(key, out var current);
        counts[key] = current + 1;
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _writer = null;
    }
}
