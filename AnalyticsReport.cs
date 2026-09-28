using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Hollowbound;

/// <summary>
/// Small, dependency-free reader for Hollowbound JSONL telemetry.
/// It is deliberately a CLI/reporting concern and never runs inside the game
/// loop. Malformed lines are reported and skipped instead of hiding the rest
/// of a usable log.
/// </summary>
public static class AnalyticsReport
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: dotnet run -- --analyze-log <path> [--json]");
            return 1;
        }

        var path = args[0];
        var json = args.Skip(1).Any(argument => string.Equals(argument, "--json", StringComparison.OrdinalIgnoreCase));
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Analytics log not found: {path}");
            return 1;
        }

        var report = new Accumulator(path);
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                report.Accept(document.RootElement);
            }
            catch (JsonException)
            {
                report.MalformedLines++;
            }
        }

        if (report.ValidRecords == 0)
        {
            Console.Error.WriteLine("No valid JSONL records found.");
            return 1;
        }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(report.ToOutput(), new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
        }
        else
        {
            report.Print();
        }

        return 0;
    }

    private sealed class Accumulator
    {
        private readonly HashSet<int> _schemaVersions = new();
        private readonly HashSet<string> _runIds = new(StringComparer.Ordinal);
        private readonly SortedDictionary<string, int> _recordKinds = new(StringComparer.Ordinal);
        private readonly SortedDictionary<string, int> _worldEventTypes = new(StringComparer.Ordinal);
        private readonly List<WorldSegment> _segments = new();
        private readonly Dictionary<string, SavedIdentity?> _savedIdentitiesByState = new(StringComparer.Ordinal);
        private WorldSegment? _current;
        private int _legacyWorldSequence;

        public Accumulator(string path) => Path = path;

        public string Path { get; }
        public int ValidRecords { get; private set; }
        public int MalformedLines { get; set; }

        public void Accept(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException("record is not an object");

            ValidRecords++;
            var kind = GetString(root, "kind") ?? "unknown";
            Count(_recordKinds, kind);
            if (GetInt(root, "schema_version") is { } schema)
                _schemaVersions.Add(schema);

            var runId = GetString(root, "session_id") ?? GetString(root, "run_id");
            if (runId is { Length: > 0 })
                _runIds.Add(runId);

            var worldId = GetString(root, "world_id");
            var worldSegmentId = GetString(root, "world_segment_id");
            var seed = GetInt(root, "seed");
            var segment = ResolveSegment(kind, root, runId, worldId, worldSegmentId, seed);
            segment.RecordCount++;
            if (worldId is null || worldSegmentId is null)
                segment.RecordsMissingIdentity++;
            if (segment.Seed is null && seed is { } firstSeed)
                segment.Seed = firstSeed;

            if (kind == "world_event" && GetString(root, "event_type") is { Length: > 0 } eventType)
                Count(_worldEventTypes, eventType);

            segment.Metrics.Accept(root);

            if (kind == "saved" && GetSavedStateKey(root) is { } stateKey)
                RememberSavedIdentity(stateKey, segment);
        }

        public void Print()
        {
            Console.WriteLine("=== HOLLOWBOUND ANALYTICS REPORT ===");
            Console.WriteLine($"File: {Path}");
            Console.WriteLine($"Records: {ValidRecords} (malformed skipped: {MalformedLines})");
            Console.WriteLine($"Runs/sessions: {_runIds.Count}");
            Console.WriteLine($"Schema versions: {Format(_schemaVersions.OrderBy(value => value))}");
            Console.WriteLine($"World identities: {GetWorldCount():N0}; world segments: {_segments.Count:N0}");
            if (_segments.Count > 0)
                Console.WriteLine($"Legacy scalar summary scope: latest world segment #{_segments.Count} (see all segments below)");

            for (var i = 0; i < _segments.Count; i++)
            {
                var segment = _segments[i];
                Console.WriteLine();
                Console.WriteLine($"--- WORLD SEGMENT {i + 1} ---");
                Console.WriteLine($"World identity: {segment.IdentityLabel}; status={segment.IdentityStatus}; source={segment.IdentitySource}");
                Console.WriteLine($"Boundary: {segment.StartReason}; seed={(segment.Seed?.ToString(CultureInfo.InvariantCulture) ?? "unknown")}; records={segment.RecordCount:N0}; records missing identity={segment.RecordsMissingIdentity:N0}");
                segment.Metrics.PrintSummary();
            }

            PrintCounts("Record kinds (whole file)", _recordKinds);
            PrintCounts("World event types (whole file; compatibility aggregate)", _worldEventTypes);
        }

        public object ToOutput()
        {
            var output = _segments.Count > 0
                ? ToDictionary(_segments[^1].Metrics.ToOutput())
                : new Dictionary<string, object?>(StringComparer.Ordinal);

            // Preserve the original flat JSON contract. Scalar simulation fields
            // now describe the latest segment; the complete, correctly scoped
            // reports are available in world_segments.
            output["path"] = Path;
            output["valid_records"] = ValidRecords;
            output["malformed_lines"] = MalformedLines;
            output["runs"] = _runIds.Count;
            output["schema_versions"] = _schemaVersions.OrderBy(value => value).ToArray();
            output["record_kinds"] = _recordKinds;
            output["world_event_types"] = _worldEventTypes;
            output["sessions"] = _runIds.Count;
            output["world_count"] = GetWorldCount();
            output["world_segment_count"] = _segments.Count;
            output["summary_scope"] = _segments.Count == 0 ? "unavailable" : "latest_world_segment";
            output["latest_seed"] = _segments.Count == 0 ? null : _segments[^1].Seed;
            output["latest_world_id"] = _segments.Count == 0 ? null : _segments[^1].WorldId;
            output["latest_world_segment_id"] = _segments.Count == 0 ? null : _segments[^1].WorldSegmentId;
            output["latest_identity_status"] = _segments.Count == 0 ? "unavailable" : _segments[^1].IdentityStatus;
            output["latest_identity_source"] = _segments.Count == 0 ? "unavailable" : _segments[^1].IdentitySource;
            output["world_segments"] = _segments.Select((segment, index) => ToSegmentOutput(segment, index + 1)).ToArray();
            return output;
        }

        private WorldSegment ResolveSegment(string kind, JsonElement root, string? runId, string? worldId,
            string? worldSegmentId, int? seed)
        {
            var isTransition = kind is "run_started" or "new_world" or "loaded";
            string? boundary = _current is null ? (isTransition ? kind : "first_record") : null;

            if (_current is not null)
            {
                if (isTransition)
                    boundary = kind;
                else if (runId is { Length: > 0 } && _current.RunId is { Length: > 0 } && runId != _current.RunId)
                    boundary = "run_id_changed";
                else if ((worldId is not null && worldId != _current.WorldId) ||
                         (worldSegmentId is not null && worldSegmentId != _current.WorldSegmentId))
                    boundary = "world_identity_changed";
                else if (seed is { } currentSeed && _current.Seed is { } previousSeed && currentSeed != previousSeed)
                    boundary = "seed_changed";
            }

            if (_current is null || boundary is not null)
            {
                var identitySource = GetString(root, "identity_source");
                var explicitIdentity = !string.IsNullOrWhiteSpace(worldId) && !string.IsNullOrWhiteSpace(worldSegmentId);
                SavedIdentity? savedIdentity = null;
                var matchedLegacySave = kind == "loaded" && TryFindSavedIdentity(root, out savedIdentity);
                var legacyGroup = explicitIdentity
                    ? null
                    : matchedLegacySave
                        ? savedIdentity!.InferredWorldGroupId
                        : NewLegacyWorldGroup();
                var basis = explicitIdentity
                    ? "explicit world_id and world_segment_id fields"
                    : matchedLegacySave
                        ? "inferred match to a preceding saved-state signature; not a persisted identity"
                        : boundary == "seed_changed"
                            ? "legacy log; observed seed change"
                            : isTransition
                                ? $"legacy log; explicit transition marker: {kind}"
                                : "legacy log without world identity; segment boundary is inferred from record order";

                _current = new WorldSegment(
                    worldId,
                    worldSegmentId,
                    legacyGroup,
                    explicitIdentity ? "session_scoped" : "inferred",
                    identitySource ?? (matchedLegacySave ? "legacy_saved_state_match" : explicitIdentity ? "explicit_fields" : "legacy_inferred"),
                    basis,
                    boundary ?? kind,
                    runId,
                    seed,
                    new SegmentAccumulator(Path));
                _segments.Add(_current);
            }
            else
            {
                if (_current.RunId is null && runId is not null)
                    _current.RunId = runId;
                if (_current.Seed is null && seed is not null)
                    _current.Seed = seed;
                if (_current.WorldId is null && worldId is not null)
                    _current.WorldId = worldId;
                if (_current.WorldSegmentId is null && worldSegmentId is not null)
                    _current.WorldSegmentId = worldSegmentId;
                if (worldId is not null && worldSegmentId is not null)
                {
                    _current.IdentityStatus = "session_scoped";
                    _current.IdentitySource = GetString(root, "identity_source") ?? "explicit_fields";
                    _current.IdentityBasis = "explicit world_id and world_segment_id fields";
                }
            }

            return _current;
        }

        private void RememberSavedIdentity(string stateKey, WorldSegment segment)
        {
            var identity = new SavedIdentity(segment.WorldId, segment.InferredWorldGroupId);
            if (_savedIdentitiesByState.TryGetValue(stateKey, out var existing) && existing != identity)
                _savedIdentitiesByState[stateKey] = null;
            else
                _savedIdentitiesByState[stateKey] = identity;
        }

        private bool TryFindSavedIdentity(JsonElement root, out SavedIdentity? identity)
        {
            identity = null;
            var key = GetSavedStateKey(root);
            if (key is null || !_savedIdentitiesByState.TryGetValue(key, out var saved) || saved is null)
                return false;
            identity = saved;
            return true;
        }

        private string NewLegacyWorldGroup() => $"legacy-inferred-{++_legacyWorldSequence:D4}";

        private int GetWorldCount()
        {
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var segment in _segments)
                identities.Add(segment.WorldId is { Length: > 0 } id ? $"id:{id}" : $"inferred:{segment.InferredWorldGroupId}");
            return identities.Count;
        }

        private static string? GetSavedStateKey(JsonElement root)
        {
            var seed = GetInt(root, "seed");
            var tick = GetLong(root, "tick");
            var population = GetInt(root, "population");
            var births = GetLong(root, "births");
            var deaths = GetLong(root, "deaths");
            var food = GetInt(root, "food_stockpile");
            var walls = GetInt(root, "wall_blocks");
            var storedFood = GetLong(root, "stored_food_units");
            if (seed is null || tick is null || population is null || births is null || deaths is null ||
                food is null || walls is null || storedFood is null)
                return null;

            return string.Join("|", seed.Value.ToString(CultureInfo.InvariantCulture),
                tick.Value.ToString(CultureInfo.InvariantCulture), population.Value.ToString(CultureInfo.InvariantCulture),
                births.Value.ToString(CultureInfo.InvariantCulture), deaths.Value.ToString(CultureInfo.InvariantCulture),
                food.Value.ToString(CultureInfo.InvariantCulture), walls.Value.ToString(CultureInfo.InvariantCulture),
                storedFood.Value.ToString(CultureInfo.InvariantCulture));
        }

        private static Dictionary<string, object?> ToDictionary(object value)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
            return document.RootElement.EnumerateObject().ToDictionary(
                property => property.Name,
                property => (object?)property.Value.Clone(),
                StringComparer.Ordinal);
        }

        private static object ToSegmentOutput(WorldSegment segment, int index)
        {
            var output = ToDictionary(segment.Metrics.ToOutput());
            output.Remove("path");
            output.Remove("valid_records");
            output.Remove("malformed_lines");
            output.Remove("runs");
            output.Remove("schema_versions");
            output.Remove("record_kinds");
            output.Remove("world_event_types");
            output["segment_index"] = index;
            output["world_id"] = segment.WorldId;
            output["world_segment_id"] = segment.WorldSegmentId;
            output["inferred_world_group_id"] = segment.InferredWorldGroupId;
            output["identity_status"] = segment.IdentityStatus;
            output["identity_source"] = segment.IdentitySource;
            output["identity_basis"] = segment.IdentityBasis;
            output["segment_start"] = segment.StartReason;
            output["run_id"] = segment.RunId;
            output["seed"] = segment.Seed;
            output["record_count"] = segment.RecordCount;
            output["records_missing_identity"] = segment.RecordsMissingIdentity;
            output["record_kinds"] = segment.Metrics.RecordKinds;
            output["world_event_types"] = segment.Metrics.WorldEventTypes;
            return output;
        }
    }

    private sealed class WorldSegment
    {
        public WorldSegment(string? worldId, string? worldSegmentId, string? inferredWorldGroupId,
            string identityStatus, string identitySource, string identityBasis, string startReason,
            string? runId, int? seed, SegmentAccumulator metrics)
        {
            WorldId = worldId;
            WorldSegmentId = worldSegmentId;
            InferredWorldGroupId = inferredWorldGroupId;
            IdentityStatus = identityStatus;
            IdentitySource = identitySource;
            IdentityBasis = identityBasis;
            StartReason = startReason;
            RunId = runId;
            Seed = seed;
            Metrics = metrics;
        }

        public string? WorldId { get; set; }
        public string? WorldSegmentId { get; set; }
        public string? InferredWorldGroupId { get; }
        public string IdentityStatus { get; set; }
        public string IdentitySource { get; set; }
        public string IdentityBasis { get; set; }
        public string StartReason { get; }
        public string? RunId { get; set; }
        public int? Seed { get; set; }
        public int RecordCount { get; set; }
        public int RecordsMissingIdentity { get; set; }
        public SegmentAccumulator Metrics { get; }
        public string IdentityLabel => WorldId is { Length: > 0 } id ? id : InferredWorldGroupId ?? "unknown";
    }

    private sealed record SavedIdentity(string? WorldId, string? InferredWorldGroupId);

    private static void PrintCounts(string title, IReadOnlyDictionary<string, int> counts)
    {
        if (counts.Count == 0)
            return;

        Console.WriteLine($"{title}:");
        foreach (var pair in counts)
            Console.WriteLine($"  {pair.Key}: {pair.Value:N0}");
    }

    private static string Format(IEnumerable<int> values) => string.Join(", ", values);

    private static void Count(IDictionary<string, int> counts, string key)
    {
        counts.TryGetValue(key, out var current);
        counts[key] = current + 1;
    }

    private sealed class SegmentAccumulator
    {
        private readonly HashSet<int> _schemaVersions = new();
        private readonly HashSet<string> _runIds = new(StringComparer.Ordinal);
        private readonly SortedDictionary<string, int> _recordKinds = new(StringComparer.Ordinal);
        private readonly SortedDictionary<string, int> _worldEventTypes = new(StringComparer.Ordinal);
        private long _firstTick = long.MaxValue;
        private long _lastTick;
        private int _peakPopulation;
        private int _peakFood;
        private int _peakWalls;
        private int _lastPopulation;
        private int _lastFood;
        private long? _foodConsumed;
        private JsonElement? _colonyEcology;
        private long _lastBirths;
        private long _lastDeaths;
        private long _lastLearningUpdates;
        private long _lastDecisions;
        private long _lastKnowledgeShared;
        private long _lastShoutsMade;
        private long _lastShoutsHeard;
        private long _lastFoodShouts;
        private long _lastDangerShouts;
        private long _lastRallyShouts;
        private long _lastShoutLearningEvents;
        private long _lastSuccessfulShoutLessons;
        private long _lastFailedShoutLessons;
        private long _lastShoutReputationRecords;
        private long _lastShoutMemoryRecords;
        private long _lastFactionShoutLearningEvents;
        private long _lastPositiveOutcomes;
        private long _lastNegativeOutcomes;
        private int _lastSettlements;
        private double _maxActualSpeed;
        private double _maxBacklogSeconds;
        private double _minFps = double.MaxValue;
        private double _maxWorldDrawMilliseconds;
        private double _maxUiDrawMilliseconds;
        private double _maxSimulationMilliseconds;
        private double _maxStepTotalP50Milliseconds;
        private double _maxStepTotalP95Milliseconds;
        private double _maxStepTotalMilliseconds;
        private double _maxStepAgentP95Milliseconds;
        private double _maxStepDistanceGridPrepP95Milliseconds;
        private double _maxStepOtherP95Milliseconds;
        private long _lastBloomFoodHarvested;
        private long _lastBeaconExplorationStarts;
        private long _lastBeaconArrivals;
        private long _lastInsightAgentsTaught;
        private long _lastInsightFoodRoutesStarted;
        private long _lastInsightFoodArrivals;
        private long _lastInsightFoodHarvested;
        private long _lastPlayerPassageTraversals;
        private long _lastTotalResonanceSpent;
        private long _lastSuccessfulInterventions;
        private long _lastFailedInterventions;
        private bool _hasStepProfile;
        private bool _hasInterventionOutcomeCounters;
        private bool _hasCausalInterventionOutcomeCounters;

        public SegmentAccumulator(string path) => Path = path;

        public string Path { get; }
        public int ValidRecords { get; private set; }
        public IReadOnlyDictionary<string, int> RecordKinds => _recordKinds;
        public IReadOnlyDictionary<string, int> WorldEventTypes => _worldEventTypes;

        public void Accept(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException("record is not an object");

            ValidRecords++;
            var kind = GetString(root, "kind") ?? "unknown";
            Count(_recordKinds, kind);

            if (GetInt(root, "schema_version") is { } schema)
                _schemaVersions.Add(schema);
            if (GetString(root, "run_id") is { Length: > 0 } runId)
                _runIds.Add(runId);

            if (GetLong(root, "tick") is { } tick)
            {
                _firstTick = Math.Min(_firstTick, tick);
                _lastTick = Math.Max(_lastTick, tick);
            }

            var population = GetInt(root, "population") ?? GetInt(root, "final_population");
            if (population is { } pop)
            {
                _lastPopulation = pop;
                _peakPopulation = Math.Max(_peakPopulation, GetInt(root, "peak_population") ?? pop);
            }

            var food = GetInt(root, "food_stockpile");
            if (food is { } foodValue)
            {
                _lastFood = foodValue;
                _peakFood = Math.Max(_peakFood, GetInt(root, "peak_food_stockpile") ?? foodValue);
            }

            var walls = GetInt(root, "wall_blocks");
            if (walls is { } wallValue)
                _peakWalls = Math.Max(_peakWalls, GetInt(root, "peak_wall_blocks") ?? wallValue);

            _lastBirths = GetLong(root, "births") ?? _lastBirths;
            _lastDeaths = GetLong(root, "deaths") ?? _lastDeaths;
            _lastLearningUpdates = GetLong(root, "learning_updates") ?? _lastLearningUpdates;
            _lastDecisions = GetLong(root, "decisions_made") ?? _lastDecisions;
            _lastKnowledgeShared = GetLong(root, "knowledge_shared") ?? _lastKnowledgeShared;
            _lastShoutsMade = GetLong(root, "shouts_made") ?? _lastShoutsMade;
            _lastShoutsHeard = GetLong(root, "shouts_heard") ?? _lastShoutsHeard;
            _lastFoodShouts = GetLong(root, "food_shouts") ?? _lastFoodShouts;
            _foodConsumed = GetLong(root, "food_consumed") ?? _foodConsumed;
            if (root.TryGetProperty("colony_ecology", out var ecology) && ecology.ValueKind == JsonValueKind.Object)
                _colonyEcology = ecology.Clone();
            _lastDangerShouts = GetLong(root, "danger_shouts") ?? _lastDangerShouts;
            _lastRallyShouts = GetLong(root, "rally_shouts") ?? _lastRallyShouts;
            _lastShoutLearningEvents = GetLong(root, "shout_learning_events") ?? _lastShoutLearningEvents;
            _lastSuccessfulShoutLessons = GetLong(root, "successful_shout_lessons") ?? _lastSuccessfulShoutLessons;
            _lastFailedShoutLessons = GetLong(root, "failed_shout_lessons") ?? _lastFailedShoutLessons;
            _lastShoutReputationRecords = GetLong(root, "shout_reputation_records") ?? _lastShoutReputationRecords;
            _lastShoutMemoryRecords = GetLong(root, "shout_memory_records") ?? _lastShoutMemoryRecords;
            _lastFactionShoutLearningEvents = GetLong(root, "faction_shout_learning_events") ?? _lastFactionShoutLearningEvents;
            _lastPositiveOutcomes = GetLong(root, "positive_outcomes") ?? _lastPositiveOutcomes;
            _lastNegativeOutcomes = GetLong(root, "negative_outcomes") ?? _lastNegativeOutcomes;
            _lastSettlements = GetInt(root, "settlements") ?? _lastSettlements;
            _lastBloomFoodHarvested = GetLong(root, "bloom_food_harvested") ?? _lastBloomFoodHarvested;
            _lastBeaconExplorationStarts = GetLong(root, "beacon_exploration_starts") ?? _lastBeaconExplorationStarts;
            _lastBeaconArrivals = GetLong(root, "beacon_arrivals") ?? _lastBeaconArrivals;
            _lastInsightAgentsTaught = GetLong(root, "insight_agents_taught") ?? _lastInsightAgentsTaught;
            _lastInsightFoodRoutesStarted = GetLong(root, "insight_food_routes_started") ?? _lastInsightFoodRoutesStarted;
            _lastInsightFoodArrivals = GetLong(root, "insight_food_arrivals") ?? _lastInsightFoodArrivals;
            _lastInsightFoodHarvested = GetLong(root, "insight_food_harvested") ?? _lastInsightFoodHarvested;
            _lastPlayerPassageTraversals = GetLong(root, "player_passage_traversals") ?? _lastPlayerPassageTraversals;
            _lastTotalResonanceSpent = GetLong(root, "total_resonance_spent") ?? _lastTotalResonanceSpent;
            _lastSuccessfulInterventions = GetLong(root, "successful_interventions") ?? _lastSuccessfulInterventions;
            _lastFailedInterventions = GetLong(root, "failed_interventions") ?? _lastFailedInterventions;
            _hasInterventionOutcomeCounters |= root.TryGetProperty("bloom_food_harvested", out _) ||
                                               root.TryGetProperty("beacon_exploration_starts", out _) ||
                                               root.TryGetProperty("insight_agents_taught", out _) ||
                                               root.TryGetProperty("insight_food_routes_started", out _) ||
                                               root.TryGetProperty("player_passage_traversals", out _);
            _hasCausalInterventionOutcomeCounters |= root.TryGetProperty("beacon_arrivals", out _) ||
                                                      root.TryGetProperty("insight_food_arrivals", out _) ||
                                                      root.TryGetProperty("insight_food_harvested", out _);

            _hasStepProfile |= root.TryGetProperty("step_total_p95_ms", out _);
            _maxStepTotalP50Milliseconds = Math.Max(_maxStepTotalP50Milliseconds, GetDouble(root, "step_total_p50_ms") ?? 0d);
            _maxStepTotalP95Milliseconds = Math.Max(_maxStepTotalP95Milliseconds, GetDouble(root, "step_total_p95_ms") ?? 0d);
            _maxStepTotalMilliseconds = Math.Max(_maxStepTotalMilliseconds, GetDouble(root, "step_total_max_ms") ?? 0d);
            _maxStepAgentP95Milliseconds = Math.Max(_maxStepAgentP95Milliseconds, GetDouble(root, "step_agents_p95_ms") ?? 0d);
            // Accept the early prototype key while preferring the more precise
            // name introduced before this schema is shipped.
            _maxStepDistanceGridPrepP95Milliseconds = Math.Max(
                _maxStepDistanceGridPrepP95Milliseconds,
                GetDouble(root, "step_distance_grid_prep_p95_ms") ?? GetDouble(root, "step_navigation_p95_ms") ?? 0d);
            _maxStepOtherP95Milliseconds = Math.Max(_maxStepOtherP95Milliseconds, GetDouble(root, "step_other_p95_ms") ?? 0d);
            _maxSimulationMilliseconds = Math.Max(_maxSimulationMilliseconds, GetDouble(root, "last_simulation_ms") ?? 0d);

            if (GetDouble(root, "smoothed_actual_speed") is { } actualSpeed)
                _maxActualSpeed = Math.Max(_maxActualSpeed, actualSpeed);
            if (GetDouble(root, "backlog_seconds") is { } backlog)
                _maxBacklogSeconds = Math.Max(_maxBacklogSeconds, backlog);
            if (kind == "render_metrics")
            {
                if (GetDouble(root, "fps") is { } fps && fps > 0d)
                    _minFps = Math.Min(_minFps, fps);
                if (GetDouble(root, "world_draw_ms") is { } worldDraw)
                    _maxWorldDrawMilliseconds = Math.Max(_maxWorldDrawMilliseconds, worldDraw);
                if (GetDouble(root, "ui_draw_ms") is { } uiDraw)
                    _maxUiDrawMilliseconds = Math.Max(_maxUiDrawMilliseconds, uiDraw);
            }

            if (kind == "world_event" && GetString(root, "event_type") is { Length: > 0 } eventType)
                Count(_worldEventTypes, eventType);
        }

        public void PrintSummary()
        {
            var firstTick = _firstTick == long.MaxValue ? 0 : _firstTick;
            Console.WriteLine($"Ticks observed: {firstTick:N0} -> {_lastTick:N0}");
            Console.WriteLine($"Population: final={_lastPopulation:N0}, peak={_peakPopulation:N0}");
            Console.WriteLine($"Food stockpile: final={_lastFood:N0}, peak={_peakFood:N0}");
            Console.WriteLine($"Food consumed: {(_foodConsumed.HasValue ? _foodConsumed.Value.ToString("N0") : "unavailable")}");
            Console.WriteLine($"Colony ecology: {(_colonyEcology.HasValue ? _colonyEcology.Value.GetRawText() : "unavailable (older log)")}");
            Console.WriteLine($"Wall blocks peak: {_peakWalls:N0}");
            Console.WriteLine($"Births/deaths: {_lastBirths:N0}/{_lastDeaths:N0}");
            Console.WriteLine($"Decisions/learning updates: {_lastDecisions:N0}/{_lastLearningUpdates:N0}");
            Console.WriteLine($"Positive/negative outcomes: {_lastPositiveOutcomes:N0}/{_lastNegativeOutcomes:N0}");
            Console.WriteLine($"Knowledge shared: {_lastKnowledgeShared:N0}");
            Console.WriteLine($"Shouts made/heard: {_lastShoutsMade:N0}/{_lastShoutsHeard:N0}");
            Console.WriteLine($"Shout types: food={_lastFoodShouts:N0}, danger={_lastDangerShouts:N0}, rally={_lastRallyShouts:N0}");
            Console.WriteLine($"Shout learning: {_lastShoutLearningEvents:N0} (success={_lastSuccessfulShoutLessons:N0}, failed={_lastFailedShoutLessons:N0})");
            Console.WriteLine($"Shout reputation records: {_lastShoutReputationRecords:N0}");
            Console.WriteLine($"Shout memories: {_lastShoutMemoryRecords:N0} (faction lessons={_lastFactionShoutLearningEvents:N0})");
            Console.WriteLine($"Settlements: {_lastSettlements:N0}");
            Console.WriteLine($"Max smoothed actual speed: x{_maxActualSpeed:0.##}");
            Console.WriteLine($"Max backlog: {_maxBacklogSeconds:0.###}s");
            var minFps = _minFps == double.MaxValue ? "n/a" : $"{_minFps:0.##}";
            Console.WriteLine($"Render: min FPS={minFps}, max world draw={_maxWorldDrawMilliseconds:0.###}ms, max UI draw={_maxUiDrawMilliseconds:0.###}ms; max simulation frame={_maxSimulationMilliseconds:0.###}ms");
            Console.WriteLine(_hasStepProfile
                ? $"Simulation sampled tick: max observed p50/p95/max={_maxStepTotalP50Milliseconds:0.###}/{_maxStepTotalP95Milliseconds:0.###}/{_maxStepTotalMilliseconds:0.###}ms"
                : "Simulation sampled tick profile: unavailable (legacy log schema)");
            if (_hasStepProfile)
                Console.WriteLine($"Simulation phase p95 maxima: agent loop (includes per-agent pathfinding)={_maxStepAgentP95Milliseconds:0.###}ms, distance-grid prep={_maxStepDistanceGridPrepP95Milliseconds:0.###}ms, other (includes LOD)={_maxStepOtherP95Milliseconds:0.###}ms");
            Console.WriteLine(_hasInterventionOutcomeCounters
                ? $"Player intervention effects: bloom food harvested={_lastBloomFoodHarvested:N0}, beacon routes started={_lastBeaconExplorationStarts:N0}, Insight clues transferred={_lastInsightAgentsTaught:N0}, Insight routes started={_lastInsightFoodRoutesStarted:N0}, meaningful passage crossings={_lastPlayerPassageTraversals:N0}"
                : "Player intervention outcomes: unavailable (legacy log schema)");
            Console.WriteLine(_hasCausalInterventionOutcomeCounters
                ? $"Verified outcomes: Beacon arrivals={_lastBeaconArrivals:N0}, Insight food arrivals={_lastInsightFoodArrivals:N0}, Insight food harvested={_lastInsightFoodHarvested:N0}; Resonance spent={_lastTotalResonanceSpent:N0}, applied/rejected={_lastSuccessfulInterventions:N0}/{_lastFailedInterventions:N0}"
                : "Verified intervention arrivals/harvest: unavailable (requires causal outcome fields)");
            PrintCounts("World event types", _worldEventTypes);
        }

        public object ToOutput() => new
        {
            path = Path,
            valid_records = ValidRecords,
            runs = _runIds.Count,
            schema_versions = _schemaVersions.OrderBy(value => value).ToArray(),
            first_tick = _firstTick == long.MaxValue ? 0 : _firstTick,
            last_tick = _lastTick,
            final_population = _lastPopulation,
            peak_population = _peakPopulation,
            final_food_stockpile = _lastFood,
            food_consumed = _foodConsumed,
            colony_ecology = _colonyEcology,
            peak_food_stockpile = _peakFood,
            peak_wall_blocks = _peakWalls,
            births = _lastBirths,
            deaths = _lastDeaths,
            decisions_made = _lastDecisions,
            learning_updates = _lastLearningUpdates,
            positive_outcomes = _lastPositiveOutcomes,
            negative_outcomes = _lastNegativeOutcomes,
            knowledge_shared = _lastKnowledgeShared,
            shouts_made = _lastShoutsMade,
            shouts_heard = _lastShoutsHeard,
            food_shouts = _lastFoodShouts,
            danger_shouts = _lastDangerShouts,
            rally_shouts = _lastRallyShouts,
            shout_learning_events = _lastShoutLearningEvents,
            successful_shout_lessons = _lastSuccessfulShoutLessons,
            failed_shout_lessons = _lastFailedShoutLessons,
            shout_reputation_records = _lastShoutReputationRecords,
            shout_memory_records = _lastShoutMemoryRecords,
            faction_shout_learning_events = _lastFactionShoutLearningEvents,
            settlements = _lastSettlements,
            max_smoothed_actual_speed = Math.Round(_maxActualSpeed, 3),
            max_backlog_seconds = Math.Round(_maxBacklogSeconds, 3),
            min_fps = _minFps == double.MaxValue ? (double?)null : Math.Round(_minFps, 3),
            max_world_draw_ms = Math.Round(_maxWorldDrawMilliseconds, 3),
            max_ui_draw_ms = Math.Round(_maxUiDrawMilliseconds, 3),
            max_simulation_frame_ms = Math.Round(_maxSimulationMilliseconds, 3),
            has_step_profile = _hasStepProfile,
            max_observed_step_total_p50_ms = _hasStepProfile ? Math.Round(_maxStepTotalP50Milliseconds, 3) : (double?)null,
            max_observed_step_total_p95_ms = _hasStepProfile ? Math.Round(_maxStepTotalP95Milliseconds, 3) : (double?)null,
            max_observed_step_total_ms = _hasStepProfile ? Math.Round(_maxStepTotalMilliseconds, 3) : (double?)null,
            max_observed_step_agents_p95_ms = _hasStepProfile ? Math.Round(_maxStepAgentP95Milliseconds, 3) : (double?)null,
            max_observed_step_distance_grid_prep_p95_ms = _hasStepProfile ? Math.Round(_maxStepDistanceGridPrepP95Milliseconds, 3) : (double?)null,
            max_observed_step_other_p95_ms = _hasStepProfile ? Math.Round(_maxStepOtherP95Milliseconds, 3) : (double?)null,
            has_intervention_outcome_counters = _hasInterventionOutcomeCounters,
            has_causal_intervention_outcome_counters = _hasCausalInterventionOutcomeCounters,
            total_resonance_spent = _hasInterventionOutcomeCounters ? _lastTotalResonanceSpent : (long?)null,
            successful_interventions = _hasInterventionOutcomeCounters ? _lastSuccessfulInterventions : (long?)null,
            failed_interventions = _hasInterventionOutcomeCounters ? _lastFailedInterventions : (long?)null,
            bloom_food_harvested = _hasInterventionOutcomeCounters ? _lastBloomFoodHarvested : (long?)null,
            beacon_exploration_starts = _hasInterventionOutcomeCounters ? _lastBeaconExplorationStarts : (long?)null,
            beacon_arrivals = _hasCausalInterventionOutcomeCounters ? _lastBeaconArrivals : (long?)null,
            insight_agents_taught = _hasInterventionOutcomeCounters ? _lastInsightAgentsTaught : (long?)null,
            insight_food_routes_started = _hasInterventionOutcomeCounters ? _lastInsightFoodRoutesStarted : (long?)null,
            insight_food_arrivals = _hasCausalInterventionOutcomeCounters ? _lastInsightFoodArrivals : (long?)null,
            insight_food_harvested = _hasCausalInterventionOutcomeCounters ? _lastInsightFoodHarvested : (long?)null,
            player_passage_traversals = _hasInterventionOutcomeCounters ? _lastPlayerPassageTraversals : (long?)null,
            record_kinds = _recordKinds,
            world_event_types = _worldEventTypes,
        };

        private static void PrintCounts(string title, IReadOnlyDictionary<string, int> counts)
        {
            if (counts.Count == 0)
                return;

            Console.WriteLine($"{title}:");
            foreach (var pair in counts)
                Console.WriteLine($"  {pair.Key}: {pair.Value:N0}");
        }

        private static string Format(IEnumerable<int> values) => string.Join(", ", values);

        private static void Count(IDictionary<string, int> counts, string key)
        {
            counts.TryGetValue(key, out var current);
            counts[key] = current + 1;
        }
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;

    private static long? GetLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : null;

    private static double? GetDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : null;
}
