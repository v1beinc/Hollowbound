using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Hollowbound.Simulation;

namespace Hollowbound;

/// <summary>
/// Simple benchmark runner that can be invoked from command line.
/// Run with: dotnet run -- --benchmark [seed] [ticks] [speed]
/// </summary>
public static class Benchmark
{
    public static void Run(
        int seed = 0,
        int targetTicks = 100000,
        float timeScale = 500f,
        bool headless = true,
        int initialPopulation = 2,
        bool lodEnabled = true)
    {
        if (seed == 0)
            seed = Environment.TickCount;

        Console.WriteLine($"=== BENCHMARK START ===");
        Console.WriteLine($"Seed: {seed}");
        Console.WriteLine($"Target ticks: {targetTicks}");
        Console.WriteLine($"Time scale: x{timeScale}");
        Console.WriteLine($"Headless: {headless}");
        Console.WriteLine($"Initial population: {initialPopulation}");
        Console.WriteLine($"LOD: {(lodEnabled ? "on" : "off")}");
        Console.WriteLine();

        var world = new EmergentSimulationWorld(seed, initialPopulation, lodEnabled);
        // Throughput mode: benchmarks measure raw simulation speed, so lift the
        // interactive frame budget that GUI hosts keep at 6 ms.
        world.FrameSimulationBudgetMilliseconds = 200;
        var logger = new AnalyticsLogger();
        var stopwatch = Stopwatch.StartNew();

        logger.LogEvent("benchmark_started", world, timeScale, false, $"seed={seed}, target_ticks={targetTicks}");

        const float realDt = 0.1f;
        int lastReportTick = 0;
        int lastPopulation = 0;
        bool caughtUpReported = false;

        while (world.Tick < targetTicks)
        {
            world.Advance(realDt, timeScale, checked((int)Math.Min(int.MaxValue, targetTicks - world.Tick)));
            logger.MaybeWriteSnapshot(world, timeScale, false);
            logger.LogWorldEvents(world);

            // Progress reporting every 10000 ticks
            if (world.Tick - lastReportTick >= 10000)
            {
                double ticksPerSec = world.Tick / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
                double popGrowth = world.AlivePopulation - lastPopulation;
                Console.WriteLine($"Tick {world.Tick:N0} | Pop {world.AlivePopulation:N0} ({popGrowth:+0;-0;0}/10k) | " +
                                 $"Food {world.FoodStockpile:N0} | Walls {world.Map.WallCells.Count:N0} | " +
                                 $"TPS {ticksPerSec:N0} | CatchingUp={world.IsCatchingUp} | " +
                                 $"ActiveChunks={world.ActiveChunkCount}");

                if (world.IsCatchingUp && !caughtUpReported)
                {
                    Console.WriteLine($"  >>> CATCHING UP DETECTED at tick {world.Tick}");
                    caughtUpReported = true;
                }
                else if (!world.IsCatchingUp)
                {
                    caughtUpReported = false;
                }

                lastReportTick = (int)world.Tick;
                lastPopulation = world.AlivePopulation;
            }

            // Safety checks
            if (world.AlivePopulation == 0)
            {
                Console.WriteLine($"EXTINCTION at tick {world.Tick}");
                logger.LogEvent("extinction", world, timeScale, false, $"tick={world.Tick}");
                break;
            }

            if (world.AlivePopulation >= EmergentSimulationWorld.PopulationSafetyLimit)
            {
                Console.WriteLine($"SAFETY LIMIT HIT at tick {world.Tick}, pop={world.AlivePopulation}");
                logger.LogEvent("safety_limit_hit", world, timeScale, false, $"population={world.AlivePopulation}");
                break;
            }
        }

        stopwatch.Stop();
        logger.LogEvent("benchmark_finished", world, timeScale, false, $"elapsed_ms={stopwatch.ElapsedMilliseconds}");
        logger.Complete(world, timeScale, false, world.AlivePopulation == 0 ? "extinction" : "completed");
        logger.Dispose();

        // Final report
        double totalTicksPerSec = world.Tick / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
        double effectiveSpeed = totalTicksPerSec / 10.0; // 10 ticks/sec = 1x realtime

        Console.WriteLine();
        Console.WriteLine("=== BENCHMARK RESULTS ===");
        Console.WriteLine($"Seed: {seed}");
        Console.WriteLine($"Ticks completed: {world.Tick:N0} / {targetTicks:N0}");
        Console.WriteLine($"Wall time: {stopwatch.Elapsed}");
        Console.WriteLine($"Ticks/second: {totalTicksPerSec:N1}");
        Console.WriteLine($"Effective speed: {effectiveSpeed:F2}x (target x{timeScale})");
        Console.WriteLine($"Efficiency: {(effectiveSpeed / timeScale * 100):F1}%");
        var stepProfile = world.StepPerformance;
        Console.WriteLine($"Recent tick time p50/p95/max: {stepProfile.Total.P50Milliseconds:0.###}/{stepProfile.Total.P95Milliseconds:0.###}/{stepProfile.Total.MaxMilliseconds:0.###} ms (n={stepProfile.Total.Samples})");
        Console.WriteLine($"Recent p95 phases: agent loop (incl. per-agent pathfinding)={stepProfile.AgentLoop.P95Milliseconds:0.###} ms, distance-grid prep={stepProfile.DistanceGridPreparation.P95Milliseconds:0.###} ms, other (incl. LOD)={stepProfile.OtherWork.P95Milliseconds:0.###} ms");
        Console.WriteLine($"Final population: {world.AlivePopulation:N0}");
        Console.WriteLine($"Births: {world.Births:N0}");
        Console.WriteLine($"Deaths: {world.Deaths:N0}");
        Console.WriteLine($"Starvation deaths: {world.StarvationDeaths:N0}");
        Console.WriteLine($"Food consumed: {world.FoodConsumed:N0}");
        Console.WriteLine($"Food gathered: {world.FoodGathered:N0}");
        Console.WriteLine($"Walls built: {world.WallBlocksBuilt:N0}");
        Console.WriteLine($"Walls removed: {world.WallBlocksRemoved:N0}");
        Console.WriteLine($"Resource surges: {world.ResourceSurges:N0}");
        Console.WriteLine($"Scarcity events: {world.ScarcityEvents:N0}");
        Console.WriteLine($"Migration waves: {world.MigrationWaves:N0}");
        Console.WriteLine($"Knowledge shared: {world.KnowledgeShared:N0}");
        Console.WriteLine($"Decisions: {world.DecisionsMade:N0}");
        Console.WriteLine($"Learning updates: {world.LearningUpdates:N0} (+{world.PositiveOutcomes:N0}/-{world.NegativeOutcomes:N0})");
        Console.WriteLine($"Policy bias food/build/explore: {world.AverageFoodPolicyBias:0.000}/{world.AverageBuildPolicyBias:0.000}/{world.AverageExplorePolicyBias:0.000}");
        foreach (var faction in world.Factions)
            Console.WriteLine($"Faction {faction.Id}: goal={faction.Goal}, pop={faction.Population}, cohesion={faction.Cohesion:0.000}, focus={faction.FoodFocus:0.00}/{faction.BuildFocus:0.00}/{faction.ExploreFocus:0.00}");
        Console.WriteLine($"Log file: {logger.LogPath}");

        // Active chunk stats
        Console.WriteLine($"Active chunks: {world.ActiveChunkCount}");
        Console.WriteLine($"LOD agents: active={world.ActiveAgentCount}, dormant={world.DormantAgentCount}, aggregated={world.AggregatedAgentCount}");
        Console.WriteLine($"Queries: agents={world.SpatialQueryCount}, food={world.FoodQueryCount}, walls={world.WallQueryCount}, paths={world.PathfindingRequests}, cache_hits={world.PathfindingCacheHits}");
    }

    public static void CompareLod(int seed, int targetTicks, float timeScale, int initialPopulation)
    {
        Console.WriteLine("=== LOD COMPARISON ===");
        Console.WriteLine($"Seed: {seed} | Ticks: {targetTicks:N0} | Speed: x{timeScale} | Initial population: {initialPopulation:N0}");
        Console.WriteLine();

        var lodOn = Measure(seed, targetTicks, timeScale, initialPopulation, enableLod: true);
        var lodOff = Measure(seed, targetTicks, timeScale, initialPopulation, enableLod: false);

        Console.WriteLine("Mode       Ticks       Pop       TPS       Wall ms    Paths      Cache hits");
        Console.WriteLine($"LOD on     {lodOn.Tick,8:N0} {lodOn.Population,9:N0} {lodOn.TicksPerSecond,9:N1} {lodOn.ElapsedMs,11:N0} {lodOn.PathRequests,10:N0} {lodOn.PathCacheHits,12:N0}");
        Console.WriteLine($"LOD off    {lodOff.Tick,8:N0} {lodOff.Population,9:N0} {lodOff.TicksPerSecond,9:N1} {lodOff.ElapsedMs,11:N0} {lodOff.PathRequests,10:N0} {lodOff.PathCacheHits,12:N0}");

        if (lodOn.TicksPerSecond > 0 && lodOff.TicksPerSecond > 0)
            Console.WriteLine($"Speed ratio (on/off): {lodOn.TicksPerSecond / lodOff.TicksPerSecond:F2}x");
    }

    private static (long Tick, int Population, double TicksPerSecond, double ElapsedMs, long PathRequests, long PathCacheHits)
        Measure(int seed, int targetTicks, float timeScale, int initialPopulation, bool enableLod)
    {
        var world = new EmergentSimulationWorld(seed, initialPopulation, enableLod)
        {
            FrameSimulationBudgetMilliseconds = 200, // throughput mode
        };
        var stopwatch = Stopwatch.StartNew();
        const float realDt = 0.1f;

        while (world.Tick < targetTicks && world.AlivePopulation > 0 &&
               world.AlivePopulation < EmergentSimulationWorld.PopulationSafetyLimit)
        {
            world.Advance(realDt, timeScale, checked((int)Math.Min(int.MaxValue, targetTicks - world.Tick)));
        }

        stopwatch.Stop();
        var seconds = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
        return (world.Tick, world.AlivePopulation, world.Tick / seconds, stopwatch.Elapsed.TotalMilliseconds,
            world.PathfindingRequests, world.PathfindingCacheHits);
    }
}
