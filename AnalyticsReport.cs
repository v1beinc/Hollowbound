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

        public void Print()
        {
            var firstTick = _firstTick == long.MaxValue ? 0 : _firstTick;
            Console.WriteLine("=== HOLLOWBOUND ANALYTICS REPORT ===");
            Console.WriteLine($"File: {Path}");
            Console.WriteLine($"Records: {ValidRecords} (malformed skipped: {MalformedLines})");
            Console.WriteLine($"Runs: {_runIds.Count}");
            Console.WriteLine($"Schema versions: {Format(_schemaVersions.OrderBy(value => value))}");
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
            Console.WriteLine($"Render: min FPS={minFps}, max world draw={_maxWorldDrawMilliseconds:0.###}ms, max UI draw={_maxUiDrawMilliseconds:0.###}ms");
            PrintCounts("Record kinds", _recordKinds);
            PrintCounts("World event types", _worldEventTypes);
        }

        public object ToOutput() => new
        {
            path = Path,
            valid_records = ValidRecords,
            malformed_lines = MalformedLines,
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
