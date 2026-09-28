using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Hollowbound.Simulation;

namespace Hollowbound.Headless
{
    /// <summary>
    /// Headless simulation runner for overnight farming, balance testing, and regression validation.
    /// Runs without graphics, outputs JSONL logs, and can save periodic snapshots.
    /// </summary>
    public sealed class HeadlessRunner
    {
        private readonly EmergentSimulationWorld _world;
        private readonly AnalyticsLogger _logger;
        private readonly Stopwatch _stopwatch = new();
        private readonly int _seed;
        private readonly int _targetTicks;
        private readonly float _timeScale;
        private readonly int _saveInterval;
        private readonly string _savePath;
        private long _lastSaveTick = -1;

        public HeadlessRunner(int seed, int targetTicks = 100000, float timeScale = 500f, int saveInterval = 1000, string savePath = "", int initialPopulation = 2, bool enableLod = true)
        {
            _seed = seed;
            _targetTicks = targetTicks;
            _timeScale = timeScale;
            _saveInterval = saveInterval;
            _savePath = savePath;

            _world = new EmergentSimulationWorld(seed, initialPopulation, enableLod);
            // Headless runs have no frame to protect: opt into a large wall-clock
            // budget for throughput. The interactive 6 ms default stays the
            // domain of the GUI. Tick order and content are unaffected.
            _world.FrameSimulationBudgetMilliseconds = 200;
            _logger = new AnalyticsLogger();

            // Log initial state
            _logger.LogEvent("headless_started", _world, timeScale, false, $"seed={seed}, target_ticks={targetTicks}");
        }

        public void Run()
        {
            Console.WriteLine($"[Headless] Starting seed={_seed}, target_ticks={_targetTicks}, speed=x{_timeScale}");
            _stopwatch.Start();

            const float realDt = 0.1f;

            while (_world.Tick < _targetTicks)
            {
                _world.Advance(realDt, _timeScale, checked((int)Math.Min(int.MaxValue, _targetTicks - _world.Tick)));

                // Log snapshot periodically
                _logger.MaybeWriteSnapshot(_world, _timeScale, false);
                _logger.LogWorldEvents(_world);

                // Auto-save
                if (_saveInterval > 0 && _world.Tick - _lastSaveTick >= _saveInterval && !string.IsNullOrEmpty(_savePath))
                {
                    SaveWorld();
                    _lastSaveTick = _world.Tick;
                }

                // Progress reporting
                if (_world.Tick % 10000 == 0)
                {
                    ReportProgress();
                }

                // Safety check
                if (_world.AlivePopulation == 0)
                {
                    Console.WriteLine($"[Headless] Extinction at tick {_world.Tick}");
                    _logger.LogEvent("extinction", _world, _timeScale, false, $"tick={_world.Tick}");
                    break;
                }

                // Safety limit check
                if (_world.AlivePopulation >= EmergentSimulationWorld.PopulationSafetyLimit)
                {
                    Console.WriteLine($"[Headless] Safety limit hit at tick {_world.Tick}, pop={_world.AlivePopulation}");
                    _logger.LogEvent("safety_limit_hit", _world, _timeScale, false, $"population={_world.AlivePopulation}");
                    break;
                }
            }

            _stopwatch.Stop();
            _logger.LogEvent("headless_finished", _world, _timeScale, false, $"elapsed_ms={_stopwatch.ElapsedMilliseconds}");

            if (!string.IsNullOrEmpty(_savePath))
                SaveWorld();

            _logger.Complete(_world, _timeScale, false, _world.AlivePopulation == 0 ? "extinction" : "completed");
            _logger.Dispose();

            ReportFinalStats();
        }

        private void ReportProgress()
        {
            double ticksPerSec = _world.Tick / Math.Max(0.001, _stopwatch.Elapsed.TotalSeconds);
            Console.WriteLine($"[Headless] Tick={_world.Tick}, Pop={_world.AlivePopulation}, Food={_world.FoodStockpile}, Walls={_world.Map.WallCells.Count}, " +
                             $"Speed={ticksPerSec:F0} t/s, CatchingUp={_world.IsCatchingUp}");
        }

        private void ReportFinalStats()
        {
            double ticksPerSec = _world.Tick / Math.Max(0.001, _stopwatch.Elapsed.TotalSeconds);
            double effectiveSpeed = _world.Tick / Math.Max(0.001, _stopwatch.Elapsed.TotalSeconds);

            Console.WriteLine("=== Headless Run Complete ===");
            Console.WriteLine($"Seed: {_seed}");
            Console.WriteLine($"Ticks completed: {_world.Tick} / {_targetTicks}");
            Console.WriteLine($"Elapsed: {_stopwatch.Elapsed}");
            Console.WriteLine($"Ticks/second: {ticksPerSec:F1}");
            Console.WriteLine($"Effective speed multiplier: {effectiveSpeed / 10.0:F2}x");
            Console.WriteLine($"Final population: {_world.AlivePopulation}");
            Console.WriteLine($"Births: {_world.Births}");
            Console.WriteLine($"Deaths: {_world.Deaths}");
            Console.WriteLine($"Starvation deaths: {_world.StarvationDeaths}");
            Console.WriteLine($"Food consumed: {_world.FoodConsumed}");
            Console.WriteLine($"Food gathered: {_world.FoodGathered}");
            Console.WriteLine($"Walls built: {_world.WallBlocksBuilt}");
            Console.WriteLine($"Walls removed: {_world.WallBlocksRemoved}");
            Console.WriteLine($"Resource surges: {_world.ResourceSurges}");
            Console.WriteLine($"Scarcity events: {_world.ScarcityEvents}");
            Console.WriteLine($"Migration waves: {_world.MigrationWaves}");
            Console.WriteLine($"Knowledge shared: {_world.KnowledgeShared}");
            Console.WriteLine($"Decisions: {_world.DecisionsMade}");
            Console.WriteLine($"Learning updates: {_world.LearningUpdates} (+{_world.PositiveOutcomes}/-{_world.NegativeOutcomes})");
            Console.WriteLine($"Policy bias food/build/explore: {_world.AverageFoodPolicyBias:0.000}/{_world.AverageBuildPolicyBias:0.000}/{_world.AverageExplorePolicyBias:0.000}");
            foreach (var faction in _world.Factions)
                Console.WriteLine($"Faction {faction.Id}: goal={faction.Goal}, pop={faction.Population}, cohesion={faction.Cohesion:0.000}, focus={faction.FoodFocus:0.00}/{faction.BuildFocus:0.00}/{faction.ExploreFocus:0.00}");
            Console.WriteLine($"Log file: {_logger.LogPath}");
        }

        private void SaveWorld()
        {
            WorldSaveService.Save(_world, _savePath);
            Console.WriteLine($"[Headless] Saved to {_savePath} at tick {_world.Tick}");
        }

        public static void LoadAndAdvance(string path, int additionalTicks, float timeScale)
        {
            var world = WorldSaveService.Load(path);
            world.FrameSimulationBudgetMilliseconds = 200; // headless throughput mode
            var startTick = world.Tick;
            while (world.Tick < startTick + additionalTicks && world.AlivePopulation > 0)
                world.Advance(0.1f, timeScale, checked((int)Math.Min(int.MaxValue, startTick + additionalTicks - world.Tick)));

            var directAlive = world.Agents.Count(agent => agent.Alive);
            Console.WriteLine($"[LoadSmoke] Loaded seed={world.Seed}, " +
                              $"tick={startTick} -> {world.Tick}, population={world.AlivePopulation} (direct={directAlive}, total={world.Agents.Count}), " +
                              $"lod={(world.LodEnabled ? "on" : "off")}");
        }

        /// <summary>
        /// Deterministic save/load regression test.
        /// Returns true if state matches within tolerance.
        /// </summary>
        [Obsolete("Superseded by the strict bit-exact 'continuation' scenario of --self-test.")]
        public static bool RunDeterministicTest(int seed = 12345, int initialTicks = 10000, int additionalTicks = 5000, float timeScale = 100f)
        {
            Console.WriteLine($"[DeterministicTest] Seed={seed}, initialTicks={initialTicks}, additionalTicks={additionalTicks}, speed=x{timeScale}");

            // 1. Create world with fixed seed
            var world1 = new EmergentSimulationWorld(seed, 2, true);
            var logger1 = new AnalyticsLogger();

            // 2. Run initial ticks
            for (int i = 0; i < initialTicks / 1000; i++)
            {
                world1.Advance(100f, timeScale);
                logger1.MaybeWriteSnapshot(world1, timeScale, false);
                logger1.LogWorldEvents(world1);
            }

            var stateBeforeSave = CaptureState(world1);
            Console.WriteLine($"[DeterministicTest] Before save: {stateBeforeSave}");

            // 3. Save snapshot
            var tempPath = Path.Combine(Path.GetTempPath(), $"hollowbound_test_{seed}.json");
            WorldSaveService.Save(world1, tempPath);

            // 4. Load snapshot
            var world2 = WorldSaveService.Load(tempPath);
            var stateAfterLoad = CaptureState(world2);
            Console.WriteLine($"[DeterministicTest] After load: {stateAfterLoad}");

            // 5. Continue both worlds for additionalTicks
            var startTick1 = world1.Tick;
            var startTick2 = world2.Tick;

            for (int i = 0; i < additionalTicks / 1000; i++)
            {
                world1.Advance(100f, timeScale);
                world2.Advance(100f, timeScale);
            }

            var stateAfterRun1 = CaptureState(world1);
            var stateAfterRun2 = CaptureState(world2);
            Console.WriteLine($"[DeterministicTest] World1 after run: {stateAfterRun1}");
            Console.WriteLine($"[DeterministicTest] World2 after run: {stateAfterRun2}");

            // 6. Compare
            bool matches = CompareStates(stateBeforeSave, stateAfterLoad, stateAfterRun1, stateAfterRun2, out string diff);

            if (matches)
            {
                Console.WriteLine($"[DeterministicTest] PASS: States match");
            }
            else
            {
                Console.WriteLine($"[DeterministicTest] FAIL: {diff}");
            }

            // Cleanup
            try { File.Delete(tempPath); } catch { }

            return matches;
        }

        private static StateSnapshot CaptureState(EmergentSimulationWorld world)
        {
            int aggregatedPop = 0;
            int aggregatedChunks = 0;
            if (world.LodEnabled)
            {
                aggregatedPop = world.AggregatedAgentCount;
            }

            return new StateSnapshot
            {
                Tick = world.Tick,
                Seed = world.Seed,
                Population = world.AlivePopulation,
                Food = world.FoodStockpile,
                StoredFood = world.StoredFoodUnits,
                Walls = world.Map.WallCells.Count,
                Births = world.Births,
                Deaths = world.Deaths,
                FactionCount = world.Factions.Count,
                SettlementCount = world.SettlementCount,
                ChronicleCount = world.ChronicleCount,
                AggregatedPopulation = aggregatedPop,
                AggregatedChunks = aggregatedChunks,
            };
        }

        private static bool CompareStates(StateSnapshot beforeSave, StateSnapshot afterLoad, StateSnapshot afterRun1, StateSnapshot afterRun2, out string diff)
        {
            var diffs = new List<string>();

            // Compare before save vs after load (should be nearly identical)
            if (beforeSave.Tick != afterLoad.Tick) diffs.Add($"Tick: {beforeSave.Tick} vs {afterLoad.Tick}");
            if (beforeSave.Seed != afterLoad.Seed) diffs.Add($"Seed: {beforeSave.Seed} vs {afterLoad.Seed}");
            if (beforeSave.Population != afterLoad.Population) diffs.Add($"Population: {beforeSave.Population} vs {afterLoad.Population}");
            if (beforeSave.Food != afterLoad.Food) diffs.Add($"Food: {beforeSave.Food} vs {afterLoad.Food}");
            if (beforeSave.StoredFood != afterLoad.StoredFood) diffs.Add($"StoredFood: {beforeSave.StoredFood} vs {afterLoad.StoredFood}");
            if (beforeSave.Walls != afterLoad.Walls) diffs.Add($"Walls: {beforeSave.Walls} vs {afterLoad.Walls}");
            if (beforeSave.Births != afterLoad.Births) diffs.Add($"Births: {beforeSave.Births} vs {afterLoad.Births}");
            if (beforeSave.Deaths != afterLoad.Deaths) diffs.Add($"Deaths: {beforeSave.Deaths} vs {afterLoad.Deaths}");
            if (beforeSave.FactionCount != afterLoad.FactionCount) diffs.Add($"FactionCount: {beforeSave.FactionCount} vs {afterLoad.FactionCount}");
            if (beforeSave.SettlementCount != afterLoad.SettlementCount) diffs.Add($"SettlementCount: {beforeSave.SettlementCount} vs {afterLoad.SettlementCount}");
            if (beforeSave.ChronicleCount != afterLoad.ChronicleCount) diffs.Add($"ChronicleCount: {beforeSave.ChronicleCount} vs {afterLoad.ChronicleCount}");
            if (beforeSave.AggregatedPopulation != afterLoad.AggregatedPopulation) diffs.Add($"AggregatedPopulation: {beforeSave.AggregatedPopulation} vs {afterLoad.AggregatedPopulation}");

            // Compare after run1 vs after run2 (should diverge minimally due to floating point)
            if (afterRun1.Tick != afterRun2.Tick) diffs.Add($"PostRun Tick: {afterRun1.Tick} vs {afterRun2.Tick}");
            if (afterRun1.Seed != afterRun2.Seed) diffs.Add($"PostRun Seed: {afterRun1.Seed} vs {afterRun2.Seed}");
            if (Math.Abs(afterRun1.Population - afterRun2.Population) > 2) diffs.Add($"PostRun Population: {afterRun1.Population} vs {afterRun2.Population} (diff > 2)");

            diff = string.Join("; ", diffs);
            return diffs.Count == 0;
        }

        private sealed class StateSnapshot
        {
            public long Tick { get; set; }
            public int Seed { get; set; }
            public int Population { get; set; }
            public int Food { get; set; }
            public int StoredFood { get; set; }
            public int Walls { get; set; }
            public int Births { get; set; }
            public int Deaths { get; set; }
            public int FactionCount { get; set; }
            public int SettlementCount { get; set; }
            public int ChronicleCount { get; set; }
            public int AggregatedPopulation { get; set; }
            public int AggregatedChunks { get; set; }

            public override string ToString()
            {
                return $"Tick={Tick}, Pop={Population}, Food={Food}, Stored={StoredFood}, Walls={Walls}, Births={Births}, Deaths={Deaths}, Factions={FactionCount}, Settlements={SettlementCount}, Chronicle={ChronicleCount}, AggPop={AggregatedPopulation}";
            }
        }

        public static void RunFromArgs(string[] args)
        {
            int seed = args.Length > 0 ? int.Parse(args[0]) : new Random().Next();
            int ticks = args.Length > 1 ? int.Parse(args[1]) : 100000;
            float speed = args.Length > 2 ? float.Parse(args[2]) : 500f;
            int saveInterval = args.Length > 3 ? int.Parse(args[3]) : 1000;
            string savePath = args.Length > 4 ? args[4] : "";
            int initialPopulation = args.Length > 5 ? int.Parse(args[5]) : 2;
            bool enableLod = args.Length <= 6 || !string.Equals(args[6], "lod=off", StringComparison.OrdinalIgnoreCase);

            var runner = new HeadlessRunner(seed, ticks, speed, saveInterval, savePath, initialPopulation, enableLod);
            runner.Run();
        }
    }
}
