using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Xna.Framework;

namespace Hollowbound.Simulation;

/// <summary>
/// Phase 0 self-test suite ("dotnet run -- --self-test"). Headless, GUI-free,
/// writes nothing outside the temp folder, exits 0 on success and 1 on failure.
///
/// Scenarios:
///  1. roundtrip      - v11 snapshot round trip with explicit state comparison,
///                       periodic invariant sweeps and Chronicle load safety.
///  2. continuation   - deterministic continuation: original world kept in memory,
///                       twin restored from snapshot, both advanced identically,
///                       full-state fingerprints must stay equal.
///  3. v10            - legacy snapshot compatibility (no chunks/settlements/goals),
///                       chunk rebuild, LOD recalculation, 600-tick continuation.
///  4. soak           - fixed-seed smoke run with periodic invariant sweeps.
/// </summary>
public static class SimulationRegressionRunner
{
    private const int MaxReportedDiffs = 12;
    private const int ChunkSize = WorldChunks.ChunkSize;

    private static readonly string[] SectionNames =
    {
        "meta", "map", "walls", "resources", "agents", "factions", "settlements", "chunks", "chronicle", "interventions",
    };

    public static int Run(string[] args)
    {
        var scenario = args.Length > 0 ? args[0].ToLowerInvariant() : "all";
        var started = Stopwatch.StartNew();
        Console.WriteLine("=== Hollowbound simulation self-test ===");
        Console.WriteLine($"snapshot version: {WorldSnapshot.CurrentVersion}, map: {EmergentSimulationWorld.Width}x{EmergentSimulationWorld.Height}");
        if (scenario == "all")
            Console.WriteLine("'all' runs the fast scenarios; use 'multi-seed' for the 100-seed sweep");

        var failures = 0;
        var executed = 0;

        void Execute(string name, Func<string> body)
        {
            if (scenario != "all" && scenario != name)
                return;
            executed++;
            try
            {
                var summary = body();
                Console.WriteLine($"[PASS] {name,-12} {summary}");
            }
            catch (SelfTestFailure failure)
            {
                failures++;
                Console.WriteLine($"[FAIL] {name,-12} {failure.Message}");
                if (failure.Details.Count > 0)
                {
                    foreach (var line in failure.Details.Take(MaxReportedDiffs))
                        Console.WriteLine($"         {line}");
                    if (failure.Details.Count > MaxReportedDiffs)
                        Console.WriteLine($"         ... and {failure.Details.Count - MaxReportedDiffs} more");
                }
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"[FAIL] {name,-12} unhandled {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"         {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
            }
        }

        if (scenario is "all" or "roundtrip")
            Execute("roundtrip", RunRoundTrip);
        if (scenario is "all" or "continuation")
            Execute("continuation", RunDeterministicContinuation);
        if (scenario is "all" or "v10" or "legacy")
            Execute("v10", RunLegacyCompatibility);
        if (scenario is "all" or "chronicle")
            Execute("chronicle", RunChronicleOverflow);
        if (scenario is "all" or "soak")
            Execute("soak", RunSoak);
        if (scenario is "all" or "responsiveness")
            Execute("responsiveness", RunResponsiveness);
        if (scenario is "all" or "interventions")
            Execute("interventions", RunInterventions);
        if (scenario is "all" or "shouts")
            Execute("shouts", RunShouts);
        if (scenario is "all" or "settlements")
            Execute("settlements", RunSettlementStability);
        if (scenario is "all" or "passage")
            Execute("passage", RunPassage);
        if (scenario is "all" or "ecology")
            Execute("ecology", RunEcology);
        if (scenario is "multi-seed" or "multiseed")
            Execute("multi-seed", RunMultiSeed);

        started.Stop();
        Console.WriteLine(executed > 0
            ? $"Result: {(failures == 0 ? "PASS" : "FAIL")} ({executed - failures}/{executed} scenarios, {started.Elapsed.TotalSeconds:F1}s)"
            : "Unknown scenario. Expected: all | roundtrip | continuation | v10 | chronicle | soak | responsiveness | interventions | shouts | settlements | passage | ecology | multi-seed");
        return executed == 0 || failures > 0 ? 1 : 0;
    }

    private static string RunEcology()
    {
        var feed = typeof(EmergentSimulationWorld).GetMethod("TryConsumeStoredFood", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var resolve = typeof(EmergentSimulationWorld).GetMethod("ResolveAction", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var reproduce = typeof(EmergentSimulationWorld).GetMethod("TryColonyBirth", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var fixture = new EmergentSimulationWorld(817, 4).CreateSnapshot();
        fixture.Food.Clear(); fixture.FoodStorage.Clear(); fixture.FoodStockpile = 100;
        for (var i = 0; i < fixture.Agents.Count; i++)
        {
            fixture.Agents[i].Cell = new PointSnapshot { X = i < 2 ? 10 + i : 100 + i % 2, Y = i < 2 ? 10 : 60 };
            fixture.Agents[i].Age = 200; fixture.Agents[i].Energy = 100;
        }
        fixture.FoodStorage.Add(new StorageSnapshot { Cell = new PointSnapshot { X = 10, Y = 11 }, Amount = 50 });
        fixture.FoodStorage.Add(new StorageSnapshot { Cell = new PointSnapshot { X = 100, Y = 61 }, Amount = 50 });
        var unit = EmergentSimulationWorld.FromSnapshot(fixture);
        unit.Map.BuildWallCell(new Point(10, 8));
        unit.Map.BuildWallCell(new Point(100, 58));
        var members = unit.Agents.ToArray();
        reproduce.Invoke(unit, new object[] { members.Take(2).ToArray() });
        reproduce.Invoke(unit, new object[] { members.Skip(2).Take(2).ToArray() });
        if (unit.Births != 2 || unit.Ecology.FoodSpentOnBirths != 12 || unit.StoredFoodUnits != 88)
            throw new SelfTestFailure($"Independent local births: births={unit.Births}, spent={unit.Ecology.FoodSpentOnBirths}, stock={unit.StoredFoodUnits}, blocked={unit.Ecology.BirthBlockedParents}/{unit.Ecology.BirthBlockedFood}/{unit.Ecology.BirthBlockedSpace}");
        var a = members[0]; a.Energy = 40; a.CarriedFood = 1; a.FeedingCooldown = 0;
        feed.Invoke(unit, new object[] { a });
        if (a.CarriedFood != 0 || a.FoodEaten != 1 || a.Energy <= 40 || unit.StoredFoodUnits != 88)
            throw new SelfTestFailure("Carried food nutrition is not conserved");
        a.Energy = 40; a.CarriedFood = 2; a.Action = AgentAction.StoringFood;
        resolve.Invoke(unit, new object[] { a });
        if (a.Energy != 40 || a.CarriedFood != 0) throw new SelfTestFailure("Storing still grants free energy");
        a.Cell = new Point(60, 40); a.Energy = 10; a.FeedingCooldown = 0;
        if ((bool)feed.Invoke(unit, new object[] { a })!) throw new SelfTestFailure("Remote reserve teleported food");
        a.Cell = new Point(10, 10); a.FeedingCooldown = 0;
        foreach (var p in new[] { new Point(10, 9), new Point(9, 10), new Point(11, 10), new Point(10, 11) }) unit.Map.BuildWallCell(p);
        // Remove the pile under the agent through a fixture; test an enclosed adjacent reserve.
        var blocked = unit.CreateSnapshot(); blocked.FoodStorage.RemoveAll(s => s.Cell.X == 10 && s.Cell.Y == 10);
        var enclosed = EmergentSimulationWorld.FromSnapshot(blocked);
        var isolated = enclosed.Agents.First(x => x.Id == a.Id); isolated.FeedingCooldown = 0;
        if ((bool)feed.Invoke(enclosed, new object[] { isolated })!) throw new SelfTestFailure("Feeding crossed a closed wall");

        var world = new EmergentSimulationWorld(54321, 80);
        AdvanceExact(world, 3000);
        if (world.Ecology.Phase != 1) throw new SelfTestFailure("Missing drought warning");
        var path = TempSavePath();
        try
        {
            WorldSaveService.Save(world, path);
            var restored = WorldSaveService.Load(path);
            AdvanceExact(world, 900); AdvanceExact(restored, 900);
            if (world.Ecology.Phase != 2) throw new SelfTestFailure("Warning did not become a drought");
            var diffs = CompareWorlds(world, restored);
            if (diffs.Count > 0) throw new SelfTestFailure("Drought warning save/load diverged", diffs);
            WorldSaveService.Save(world, path); restored = WorldSaveService.Load(path);
            AdvanceExact(world, 1800); AdvanceExact(restored, 1800);
            diffs = CompareWorlds(world, restored);
            if (diffs.Count > 0) throw new SelfTestFailure("Active drought save/load diverged", diffs);
            if (world.Ecology.DroughtsCompleted != 1 || world.Ecology.Phase != 0 || world.FoodConsumed == 0)
                throw new SelfTestFailure("Drought cycle or real consumption missing");
            var errors = CheckInvariants(world);
            if (errors.Count > 0) throw new SelfTestFailure("Ecology invariants", errors);
            return $"nutrition=conserved remote/blocked_food=rejected independent_births=2 drought_roundtrip=exact eaten={world.FoodConsumed} pop={world.AlivePopulation}";
        }
        finally { TryDelete(path); }
    }

    private static string RunSettlementStability()
    {
        var world = new EmergentSimulationWorld(12345, 120);
        AdvanceExact(world, 2000);
        var update = typeof(EmergentSimulationWorld).GetMethod("UpdateSettlements", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new SelfTestFailure("UpdateSettlements test hook missing");
        update.Invoke(world, null);
        var ids = world.Settlements.Keys.Order().ToArray();
        if (ids.Length == 0) throw new SelfTestFailure("Fixture did not form a settlement");
        var next = world.NextSettlementId;
        // No simulation movement between passes: the same colony must retain
        // its ID even though population counters are zeroed during each pass.
        for (var pass = 0; pass < 20; pass++) update.Invoke(world, null);
        if (world.NextSettlementId != next || !ids.SequenceEqual(world.Settlements.Keys.Order()))
            throw new SelfTestFailure($"Settlement churn without movement: nextId {next} -> {world.NextSettlementId}");
        var assigned = world.Agents.Count(a => a.Alive && a.SettlementId >= 0);
        if (world.Settlements.Values.Sum(s => s.Population) != assigned)
            throw new SelfTestFailure("Settlement population does not match memberships");
        return $"stable_ids={ids.Length} repeated_updates=20 population_assigned={assigned}";
    }

    private static string RunPassage()
    {
        var original = new EmergentSimulationWorld(12345, 80);
        AdvanceExact(original, 1200);
        var target = original.Map.WallCells.OrderBy(c => c.Y).ThenBy(c => c.X).First();
        var resonance = original.Resonance;
        if (!original.QueueIntervention(EmergentSimulationWorld.InterventionType.Passage, target).Success)
            throw new SelfTestFailure("Passage was rejected");
        if (original.QueueIntervention(EmergentSimulationWorld.InterventionType.Passage, target).Success)
            throw new SelfTestFailure("Duplicate passage accepted");
        if (original.AvailableResonance != resonance - 2)
            throw new SelfTestFailure("Queued cost was not reserved");
        var path = TempSavePath();
        try
        {
            WorldSaveService.Save(original, path);
            var restored = WorldSaveService.Load(path);
            AdvanceExact(original, 1);
            AdvanceExact(restored, 1);
            if (!original.Map.IsWalkable(target) || original.Map.CanBuildWallCell(target) || original.Resonance != resonance - 2)
                throw new SelfTestFailure("Passage did not open or cost is wrong");
            AdvanceExact(original, 100);
            AdvanceExact(restored, 100);
            var diffs = CompareWorlds(original, restored);
            if (diffs.Count > 0) throw new SelfTestFailure("Passage save/load divergence", diffs);
            var errors = CheckInvariants(original);
            if (errors.Count > 0) throw new SelfTestFailure("Passage broke world invariants", errors);
            return "queued_save_load=pass reserved_cost=2 duplicate_rejected=yes continuation=101 ticks";
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private sealed class SelfTestFailure : Exception
    {
        public List<string> Details { get; }

        public SelfTestFailure(string message, List<string>? details = null)
            : base(message)
        {
            Details = details ?? new List<string>();
        }
    }

    // ------------------------------------------------------------------
    // Scenario 1: v11 round trip + Chronicle load safety
    // ------------------------------------------------------------------

    private static string RunRoundTrip()
    {
        const int seed = 20260826;
        const int initialPopulation = 260;
        const int runTicks = 3200;
        const int postLoadTicks = 600;
        var details = new List<string>();

        var original = new EmergentSimulationWorld(seed, initialPopulation, enableLod: true);
        for (var done = 0; done < runTicks; done += 800)
        {
            AdvanceExact(original, Math.Min(800, runTicks - done));
            var errors = CheckInvariants(original);
            if (errors.Count > 0)
                throw new SelfTestFailure($"invariants failed at tick {original.Tick}", errors);
        }

        if (original.ChronicleCount < 8)
            details.Add($"note: thin chronicle ({original.ChronicleCount} events)");
        if (original.SettlementCount == 0)
            details.Add("note: no settlements formed in the window");

        var snapshot = original.CreateSnapshot();
        if (snapshot.Version != WorldSnapshot.CurrentVersion)
            throw new SelfTestFailure($"snapshot version {snapshot.Version} != {WorldSnapshot.CurrentVersion}");
        if (snapshot.Chunks.Count != original.Chunks.TotalChunks)
            throw new SelfTestFailure($"snapshot chunks {snapshot.Chunks.Count} != {original.Chunks.TotalChunks}");

        var path = TempSavePath();
        try
        {
            WorldSaveService.Save(original, path);
            var loaded = WorldSaveService.Load(path);

            var diffs = CompareWorlds(original, loaded);
            if (diffs.Count > 0)
                throw new SelfTestFailure($"{diffs.Count} state differences after v11 round trip", diffs);

            // Chronicle load safety: nothing pending right after load, history intact.
            var drainedAfterLoad = loaded.DrainNewChronicleEvents();
            if (drainedAfterLoad.Count != 0)
                throw new SelfTestFailure(
                    $"DrainNewChronicleEvents returned {drainedAfterLoad.Count} events right after load",
                    drainedAfterLoad.Take(MaxReportedDiffs)
                        .Select(e => $"replayed event: tick={e.Tick} type={e.Type}")
                        .ToList());

            var historyBefore = loaded.ChronicleCount;
            if (historyBefore != original.ChronicleCount)
                throw new SelfTestFailure($"chronicle history {historyBefore} != original {original.ChronicleCount}");

            AdvanceExact(loaded, postLoadTicks);
            var lateErrors = CheckInvariants(loaded);
            if (lateErrors.Count > 0)
                throw new SelfTestFailure($"invariants failed after {postLoadTicks} post-load ticks", lateErrors);

            // New events must appear only for real changes and never duplicate
            // one-shot history (first wall/birth/death/..., milestones).
            var fresh = loaded.DrainNewChronicleEvents();
            var duplicates = new List<string>();
            // UpdateWorldEvents increments MigrationWaves and records one
            // MigrationStarted per wave. Unlike FirstBirth it is repeatable.
            // Verify the actual counter delta and event ticks, not a one-shot
            // assumption. A load-induced replay still fails these checks.
            var freshMigrations = fresh.Where(e => e.Type == WorldEventType.MigrationStarted).ToArray();
            if (freshMigrations.Length != loaded.MigrationWaves - original.MigrationWaves)
                duplicates.Add("Migration event count does not match new wave count after load");
            if (freshMigrations.Any(e => e.Tick <= snapshot.Tick || e.Tick > loaded.Tick))
                duplicates.Add("Migration replay has a tick outside the continuation interval");
            foreach (var group in loaded.Chronicle.GroupBy(e => e.Type).Where(g => IsOnceOnly(g.Key)))
            {
                if (group.Key == WorldEventType.MigrationStarted)
                {
                    if (group.Select(e => e.Tick).Distinct().Count() != group.Count() || group.Count() > loaded.MigrationWaves)
                        duplicates.Add("Migration history has duplicate ticks or more events than actual waves");
                }
                else if (group.Count() > 1)
                    duplicates.Add($"once-event {group.Key} appears {group.Count()}x after load");
            }
            foreach (var e in fresh)
            {
                if (e.Type == WorldEventType.FirstBirth && original.Births > 0)
                    duplicates.Add($"replayed first-birth at tick {e.Tick}");
                if (e.Type == WorldEventType.FirstWallBuilt && original.WallBlocksBuilt > 0)
                    duplicates.Add($"replayed first-wall at tick {e.Tick}");
            }
            if (duplicates.Count > 0)
                throw new SelfTestFailure($"{duplicates.Count} chronicle duplication problems", duplicates);

            return Formatted($"seed={seed} ticks={runTicks}+{postLoadTicks} pop={loaded.AlivePopulation} " +
                             $"chronicle={loaded.ChronicleCount} (+{fresh.Count} new) settlements={loaded.SettlementCount} " +
                             $"aggregated={loaded.AggregatedAgentCount} diffs=0", details);
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static bool IsOnceOnly(WorldEventType type) =>
        type is WorldEventType.FirstFoodGathered or WorldEventType.FirstWallBuilt or WorldEventType.FirstBirth
            or WorldEventType.FirstDeath or WorldEventType.MigrationStarted or WorldEventType.FirstElder;

    // ------------------------------------------------------------------
    // Scenario 2: deterministic continuation
    // ------------------------------------------------------------------

    private static string RunDeterministicContinuation()
    {
        const int seed = 987654;
        const int initialPopulation = 260;
        const int preTicks = 2400;
        const int postTicks = 1200;

        var original = new EmergentSimulationWorld(seed, initialPopulation, enableLod: true);
        AdvanceExact(original, preTicks);

        var beforeSave = ComputeSectionHashes(original);
        var path = TempSavePath();
        try
        {
            WorldSaveService.Save(original, path);
            var twin = WorldSaveService.Load(path);

            if (twin.DrainNewChronicleEvents().Count != 0)
                throw new SelfTestFailure("twin world emitted chronicle events right after load");

            var afterLoad = ComputeSectionHashes(twin);
            var loadDiff = DiffHashes(beforeSave, afterLoad, original, twin);
            if (loadDiff.Count > 0)
                throw new SelfTestFailure("restored world differs from original before continuation", loadDiff);

            AdvanceExact(original, postTicks);
            AdvanceExact(twin, postTicks);

            var afterRunOriginal = ComputeSectionHashes(original);
            var afterRunTwin = ComputeSectionHashes(twin);
            var runDiff = DiffHashes(afterRunOriginal, afterRunTwin, original, twin);
            if (runDiff.Count > 0)
                throw new SelfTestFailure(
                    $"worlds diverged during {postTicks}-tick continuation",
                    BisectDivergence(seed, initialPopulation, preTicks, postTicks));

            return Formatted($"seed={seed} pre={preTicks} post={postTicks} " +
                             $"pop={twin.AlivePopulation} fingerprints_match=true rng={twin.RandomState}", null);
        }
        finally
        {
            TryDelete(path);
        }
    }

    /// <summary>
    /// Failure-path diagnostic: replays the continuation comparing full-state
    /// hashes every few ticks and reports the exact tick where the worlds first
    /// diverge, together with the differing sections and first line diff.
    /// </summary>
    private static List<string> BisectDivergence(int seed, int population, int preTicks, int postTicks)
    {
        var details = new List<string> { "divergence bisect:" };
        var a = new EmergentSimulationWorld(seed, population, enableLod: true);
        AdvanceExact(a, preTicks);

        var tempPath = TempSavePath();
        try
        {
            WorldSaveService.Save(a, tempPath);
            var b = WorldSaveService.Load(tempPath);

            const int stride = 20;
            for (var done = 0; done < postTicks; done += stride)
            {
                AdvanceExact(a, Math.Min(stride, postTicks - done));
                AdvanceExact(b, Math.Min(stride, postTicks - done));
                var diff = DiffHashes(ComputeSectionHashes(a), ComputeSectionHashes(b), a, b);
                if (diff.Count == 0)
                    continue;

                details.Add($"first divergence after tick {a.Tick}");
                details.AddRange(diff.Select(l => "  " + l));
                return details;
            }

            details.Add("no divergence reproduced during bisect run");
            return details;
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    // ------------------------------------------------------------------
    // Scenario 2b: Chronicle ring-buffer overflow must not resurrect once-events
    // ------------------------------------------------------------------

    /// <summary>
    /// Proves that anti-replay state survives eviction from the 48-slot
    /// Chronicle ring buffer. Fully deterministic: elder transitions are forced
    /// by setting ages directly, and the buffer is overflowed with synthetic
    /// events through the engine's own recording path - no RNG luck involved.
    /// </summary>
    private static string RunChronicleOverflow()
    {
        const int seed = 4451;
        const int initialPopulation = 30;

        var world = new EmergentSimulationWorld(seed, initialPopulation, enableLod: true);

        // 1. Force a real FirstElder event without waiting ~24k ticks: push half
        //    of the population past the 75% MaxAge threshold; the next Step
        //    performs the transition exactly once.
        var agents = world.Agents.ToList();
        foreach (var agent in agents.Take(agents.Count / 2))
            agent.Age = agent.MaxAge * 0.8f;
        AdvanceExact(world, 2);

        if (world.Chronicle.Count(e => e.Type == WorldEventType.FirstElder) != 1)
            throw new SelfTestFailure(
                $"expected exactly one FirstElder after forced aging, found " +
                $"{world.Chronicle.Count(e => e.Type == WorldEventType.FirstElder)}");
        if (!GetOnceEvents(world).Contains(WorldEventType.FirstElder))
            throw new SelfTestFailure("FirstElder fired but was not registered in the once-event set");

        // Generation milestones are RNG-gated in natural play; inject known
        // values to prove their persistence plumbing deterministically.
        GetRecordedGenerations(world).UnionWith(new[] { 5, 10, 15 });

        // 2. Overflow the ring buffer so the FirstElder entry is evicted.
        for (var i = 0; i < 60; i++)
            RecordSyntheticEvent(world, $"synthetic overflow {i}");
        if (world.Chronicle.Any(e => e.Type == WorldEventType.FirstElder))
            throw new SelfTestFailure("eviction failed: FirstElder still present in the ring buffer");

        // 3. Save + load.
        var path = TempSavePath();
        try
        {
            WorldSaveService.Save(world, path);
            var loaded = WorldSaveService.Load(path);

            if (!loaded.FiredOnceEvents.Contains(WorldEventType.FirstElder))
                throw new SelfTestFailure(
                    "once-event set was not restored across save/load; " +
                    "evicted once-events would re-fire (the exact bug this scenario guards)");
            var generations = GetRecordedGenerations(loaded);
            if (!generations.SetEquals(new[] { 5, 10, 15 }))
                throw selfTestGenerationsNotRestored(generations);
            if (loaded.DrainNewChronicleEvents().Count != 0)
                throw new SelfTestFailure("loaded world replayed chronicle events");

            // 4. Behavioural suppression proof: the remaining (previously young)
            //    agents now cross the elder threshold. With restored state the
            //    engine must stay silent; with empty state it would announce
            //    another "first" elder.
            foreach (var agent in loaded.Agents.Skip(loaded.Agents.Count / 2).ToList())
                agent.Age = agent.MaxAge * 0.8f;
            AdvanceExact(loaded, 2);

            var refires = loaded.Chronicle.Count(e => e.Type == WorldEventType.FirstElder);
            if (refires != 0)
                throw new SelfTestFailure(
                    $"FirstElder re-fired after load ({refires} entries); anti-replay restore is broken");
            if (loaded.DrainNewChronicleEvents().Any(e => e.Type == WorldEventType.FirstElder))
                throw new SelfTestFailure("FirstElder appeared among pending chronicle events after load");
            if (!GetRecordedGenerations(loaded).SetEquals(new[] { 5, 10, 15 }))
                throw selfTestGenerationsNotRestored(GetRecordedGenerations(loaded));

            var errors = CheckInvariants(loaded);
            if (errors.Count > 0)
                throw new SelfTestFailure("invariants failed", errors);

            return Formatted($"seed={seed} evicted=60 once_restored=yes generations=[5,10,15] " +
                             $"refires=0 chronicle={loaded.ChronicleCount}", null);
        }
        finally
        {
            TryDelete(path);
        }

        SelfTestFailure selfTestGenerationsNotRestored(HashSet<int> actual) =>
            new($"generation milestones not restored: [{string.Join(",", actual.OrderBy(g => g))}]");
    }

    // ------------------------------------------------------------------
    // Scenario 3: legacy save compatibility from embedded minimal fixtures
    // ------------------------------------------------------------------

    /// <summary>
    /// Loads hand-written minimal JSON fixtures that genuinely lack the newer
    /// fields - no chunks, no settlements, no goal baseline, no determinism
    /// block, no anti-replay block. This proves the deserializer and the
    /// version guards tolerate real old files, not just downgraded v12 ones.
    /// </summary>
    private static string RunLegacyCompatibility()
    {
        var summaries = new List<string>
        {
            LoadLegacyFixtureAndContinue(BuildLegacyFixtureJson(version: 10), "v10", continueTicks: 700),
            LoadLegacyFixtureAndContinue(BuildLegacyFixtureJson(version: 11), "v11", continueTicks: 700),
        };
        return Formatted(string.Join(" | ", summaries), null);
    }

    private static string LoadLegacyFixtureAndContinue(string json, string label, int continueTicks)
    {
        var path = TempSavePath();
        try
        {
            File.WriteAllText(path, json);

            EmergentSimulationWorld loaded;
            try
            {
                loaded = WorldSaveService.Load(path);
            }
            catch (Exception ex)
            {
                throw new SelfTestFailure($"{label} fixture load threw {ex.GetType().Name}: {ex.Message}");
            }

            if (loaded.AlivePopulation != 2)
                throw new SelfTestFailure($"{label}: population {loaded.AlivePopulation} != fixture's 2");
            if (loaded.Seed != 55051 || loaded.Tick != 500)
                throw new SelfTestFailure($"{label}: seed/tick mismatch ({loaded.Seed}/{loaded.Tick})");
            if (loaded.FoodStockpile != 80)
                throw new SelfTestFailure($"{label}: FoodStockpile {loaded.FoodStockpile} != fixture's 80");

            if (loaded.DrainNewChronicleEvents().Count != 0)
                throw new SelfTestFailure($"{label}: load replayed chronicle events");
            if (loaded.ChronicleCount != 1 || loaded.Chronicle[0].Type != WorldEventType.FirstFoodGathered)
                throw new SelfTestFailure($"{label}: chronicle history not preserved as written");

            // Counter-derived once-event fallback must have seeded FirstFoodGathered
            // (FoodGathered=9 in the fixture proves it fired historically).
            if (!loaded.FiredOnceEvents.Contains(WorldEventType.FirstFoodGathered))
                throw new SelfTestFailure($"{label}: FirstFoodGathered not derived from FoodGathered counter");

            if (loaded.ActiveAgentCount <= 0)
                throw new SelfTestFailure($"{label}: LOD was not recalculated after load");

            var errors = CheckInvariants(loaded);
            if (errors.Count > 0)
                throw new SelfTestFailure($"{label}: state reconstruction failed", errors);

            AdvanceExact(loaded, continueTicks);
            var lateErrors = CheckInvariants(loaded);
            if (lateErrors.Count > 0)
                throw new SelfTestFailure($"{label}: invariants failed during {continueTicks}-tick continuation", lateErrors);
            if (loaded.AlivePopulation == 0)
                throw new SelfTestFailure($"{label}: extinction during continuation");

            return Formatted($"{label}: pop={loaded.AlivePopulation} lod_active={loaded.ActiveAgentCount} " +
                             $"continued={continueTicks}", null);
        }
        finally
        {
            TryDelete(path);
        }
    }

    /// <summary>
    /// Minimal legacy JSON: only fields every historical writer emitted.
    /// Everything version-gated is deliberately absent.
    /// </summary>
    private static string BuildLegacyFixtureJson(int version)
    {
        var mapCells = Convert.ToBase64String(new byte[EmergentSimulationWorld.Width * EmergentSimulationWorld.Height]);
        var sb = new StringBuilder(4096);
        sb.Append('{');
        sb.Append("\"Version\": ").Append(version).Append(',');
        sb.Append("\"Seed\": 55051,");
        sb.Append("\"RandomState\": 123456789,");
        sb.Append("\"Tick\": 500,");
        sb.Append("\"Accumulator\": 0,");
        sb.Append("\"BirthCooldown\": 0,");
        sb.Append("\"NextAgentId\": 3,");
        sb.Append("\"FoodStockpile\": 80,");
        sb.Append("\"Births\": 0,\"Deaths\": 0,\"StarvationDeaths\": 0,");
        sb.Append("\"FoodConsumed\": 4,\"FoodGathered\": 9,");
        sb.Append("\"FoodShared\": 0,\"KnowledgeShared\": 0,");
        sb.Append("\"ResourceSurges\": 0,\"ScarcityEvents\": 0,\"MigrationWaves\": 0,");
        sb.Append("\"CurrentEvent\": \"quiet\",\"EventTicksRemaining\": 0,");
        sb.Append("\"WallBlocksBuilt\": 0,\"WallBlocksRemoved\": 0,");
        sb.Append("\"LodEnabled\": true,");
        sb.Append("\"MapCells\": \"").Append(mapCells).Append("\",");
        sb.Append("\"Agents\": [");
        sb.Append(AgentFixtureJson(id: 0, faction: 0, x: 12, y: 12)).Append(',');
        sb.Append(AgentFixtureJson(id: 2, faction: 1, x: 40, y: 30));
        sb.Append("],");
        sb.Append("\"Factions\": [ { \"Id\": 0, \"Goal\": 0 }, { \"Id\": 1, \"Goal\": 2 } ],");
        sb.Append("\"Food\": [],\"FoodStorage\": [],");
        sb.Append("\"Chronicle\": [ { \"Tick\": 120, \"Type\": 0, \"FactionId\": 0, " +
                  "\"Cell\": {\"X\":10,\"Y\":10}, \"HasCell\": true, " +
                  "\"Description\": \"legacy first food\", \"Importance\": 1 } ]");
        // Deliberately absent: Chunks, Settlements, NextSettlementId,
        // RecordedSettlementMilestones, LastFactionGoal, EventCooldown,
        // scheduler offsets, grid data, LOD counters, EventCooldownTicks,
        // KnownClusterChunks, OnceEventTypes, RecordedGenerations.
        sb.Append('}');
        return sb.ToString();
    }

    private static string AgentFixtureJson(int id, int faction, int x, int y) =>
        "{ \"Id\": " + id + ", \"FactionId\": " + faction +
        ", \"Cell\": {\"X\":" + x + ",\"Y\":" + y + "}, \"Facing\": {\"X\":1,\"Y\":0}" +
        ", \"TargetCell\": {\"X\":" + x + ",\"Y\":" + y + "}, \"FoodTargetCell\": {\"X\":0,\"Y\":0}" +
        ", \"HasFoodTarget\": false, \"Path\": [], \"PathIndex\": 0" +
        ", \"Energy\": 90.0, \"Age\": 100.0, \"CarriedFood\": 0" +
        ", \"MoveCooldown\": 0, \"RestTimer\": 0, \"BuildCooldown\": 0, \"FeedingCooldown\": 0, \"ExplorationCooldown\": 0" +
        ", \"Role\": 1, \"RoleExperience\": 0.1" +
        ", \"HomeWallCell\": {\"X\":0,\"Y\":0}, \"HasHomeWall\": false" +
        ", \"KnownFoodCell\": {\"X\":0,\"Y\":0}, \"HasKnownFood\": false, \"FoodKnowledge\": 0" +
        ", \"SuccessfulFoodTrips\": 1, \"FailedFoodTrips\": 0, \"FoodEaten\": 1, \"ExplorationTrips\": 0, \"SharedMemories\": 0" +
        ", \"KnownDangerCell\": {\"X\":0,\"Y\":0}, \"HasDangerMemory\": false, \"DangerKnowledge\": 0, \"RouteKnowledge\": 0" +
        ", \"BuildDrive\": 0.5, \"ExplorationDrive\": 0.5, \"RiskTolerance\": 0.5, \"LearningRate\": 0.5" +
        ", \"Intelligence\": 0.5, \"PlanningSkill\": 0.5, \"SocialAwareness\": 0.5, \"PreferredBuildDirection\": 3" +
        ", \"FoodUtilityBias\": 0, \"BuildUtilityBias\": 0, \"ExploreUtilityBias\": 0, \"RestUtilityBias\": 0" +
        ", \"LastDecisionAction\": 0, \"LastDecisionScore\": 0, \"DecisionsMade\": 3, \"LearningUpdates\": 1" +
        ", \"PositiveOutcomes\": 1, \"NegativeOutcomes\": 0, \"Action\": 0, \"Alive\": true" +
        ", \"Generation\": 1, \"ParentId1\": -1, \"ParentId2\": -1, \"MaxAge\": 36000.0, \"IsElder\": false" +
        ", \"ElderWisdomBonus\": 0, \"SettlementId\": -1 }";

    // ------------------------------------------------------------------
    // Scenario 4: soak smoke test
    // ------------------------------------------------------------------

    private static string RunSoak()
    {
        const int seed = 424242;
        const int initialPopulation = 280;
        const int totalTicks = 12000;
        const int chunkTicks = 200;
        const int invariantInterval = 1000;

        var world = new EmergentSimulationWorld(seed, initialPopulation, enableLod: true);
        var stopwatch = Stopwatch.StartNew();
        var invariantErrors = new List<string>();
        var nextInvariantTick = invariantInterval;

        for (var done = 0; done < totalTicks; done += chunkTicks)
        {
            AdvanceExact(world, Math.Min(chunkTicks, totalTicks - done));

            if (world.Tick >= nextInvariantTick)
            {
                nextInvariantTick += invariantInterval;
                var errors = CheckInvariants(world);
                if (errors.Count > 0 && invariantErrors.Count == 0)
                    invariantErrors.AddRange(errors.Select(e => $"tick {world.Tick}: {e}"));
            }

            if (done > 0 && done % 2000 == 0)
            {
                Console.WriteLine($"         soak tick={world.Tick} pop={world.AlivePopulation} " +
                                  $"tps={world.Tick / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds):F0}");
            }

            if (world.AlivePopulation == 0)
            {
                Console.WriteLine($"         soak extinction at tick {world.Tick}");
                break;
            }
        }

        stopwatch.Stop();
        if (invariantErrors.Count > 0)
            throw new SelfTestFailure($"{invariantErrors.Count} invariant violations during soak", invariantErrors);

        return Formatted($"ticks={world.Tick} tps={world.Tick / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds):F0} " +
                         $"pop={world.AlivePopulation} lod={world.ActiveAgentCount}/{world.DormantAgentCount}/{world.AggregatedAgentCount} " +
                         $"settlements={world.SettlementCount} walls={world.Map.WallCells.Count} chronicle={world.ChronicleCount} inv_errors=0", null);
    }

    // ------------------------------------------------------------------
    // Scenario 5: 100-seed regression (ROADMAP Phase-0 requirement)
    // ------------------------------------------------------------------

    /// <summary>
    /// Runs a fixed, reproducible set of 100 worlds and verifies invariants in
    /// each. Not bit-exact continuation per seed - that is what the dedicated
    /// deep 'continuation' scenario is for; this mode is the breadth sweep.
    /// </summary>
    private static string RunMultiSeed()
    {
        const int seedCount = 100;
        const int initialPopulation = 90;
        const int ticksPerSeed = 800;
        const int midpoint = ticksPerSeed / 2;
        const int seedBase = 1_000_003;
        const int seedStride = 7_777;

        var stopwatch = Stopwatch.StartNew();
        var passed = 0;
        var extinctions = 0;
        List<string>? firstFailure = null;
        var firstFailureSeed = 0;
        long firstFailureTick = 0;

        for (var i = 0; i < seedCount; i++)
        {
            var seed = seedBase + i * seedStride;
            EmergentSimulationWorld? world = null;
            try
            {
                world = new EmergentSimulationWorld(seed, initialPopulation, enableLod: true);
                AdvanceExact(world, midpoint);
                var errors = CheckInvariants(world);
                if (errors.Count == 0)
                {
                    AdvanceExact(world, ticksPerSeed - midpoint);
                    errors = CheckInvariants(world);
                }
                if (errors.Count > 0)
                    throw new SelfTestFailure(errors[0], errors);

                if (world.AlivePopulation == 0)
                    extinctions++; // extinction is not an invariant violation
                passed++;
            }
            catch (Exception ex)
            {
                if (firstFailure == null)
                {
                    firstFailureSeed = seed;
                    firstFailureTick = world?.Tick ?? -1;
                    firstFailure = ex is SelfTestFailure failure
                        ? failure.Details.Take(5).ToList()
                        : new List<string> { $"{ex.GetType().Name}: {ex.Message}" };
                }
                Console.WriteLine($"         [FAIL] seed={seed} tick={world?.Tick ?? -1}: {ex.Message}");
            }

            if ((i + 1) % 10 == 0)
            {
                Console.WriteLine($"         multi-seed progress {i + 1}/{seedCount} " +
                                  $"({stopwatch.Elapsed.TotalSeconds:F0}s)");
            }
        }

        stopwatch.Stop();
        if (passed < seedCount)
            throw new SelfTestFailure(
                $"{seedCount - passed}/{seedCount} seeds failed; first failure: " +
                $"seed={firstFailureSeed} tick={firstFailureTick}",
                firstFailure);

        return Formatted($"seeds=100/100 ticks_per_seed={ticksPerSeed} pop_start={initialPopulation} " +
                         $"extinctions={extinctions} elapsed={stopwatch.Elapsed.TotalSeconds:F0}s", null);
    }

    // ------------------------------------------------------------------
    // Scenario 6: frame-budget responsiveness (no GUI, hardware-tolerant)
    // ------------------------------------------------------------------

    /// <summary>
    /// Simulates display-frame calls into Advance at representative population
    /// levels and speeds, measuring per-call duration, processed ticks, actual
    /// speed, catching-up share and backlog bounds.
    ///
    /// Hardware-independent by design: the only hard timing assertion is an
    /// explicit-stall threshold (100 ms), not a claim about absolute machine
    /// speed. Calls above the interactive budget but below the stall threshold
    /// are reported as performance warnings, never hidden.
    /// </summary>
    private static string RunResponsiveness()
    {
        const int warmupTicks = 300;
        const int frames = 480;                 // 8 simulated wall-seconds at 60 FPS
        const float frameSeconds = 1f / 60f;
        const double stallThresholdMs = 100.0;  // hard fail: interactive stall
        const double warnThresholdMs = 12.0;    // soft report: over budget, under stall
        const float backlogBound = 6.5f;        // engine caps backlog at 6.4 s (x500)

        int[] populations = { 60, 150, 300 };
        float[] speeds = { 1f, 25f, 100f, 250f, 500f };

        Console.WriteLine("         responsiveness (Advance per simulated 60 FPS frame):");
        var warnings = new List<string>();

        foreach (var population in populations)
        {
            foreach (var speed in speeds)
            {
                var world = new EmergentSimulationWorld(
                    90210 + population * 7 + (int)speed, population, enableLod: true);
                AdvanceExact(world, warmupTicks);

                var durations = new List<double>(frames);
                long ticksProcessed = 0;
                var catchingFrames = 0;
                float peakBacklog = 0f;

                for (var frame = 0; frame < frames; frame++)
                {
                    var callStopwatch = Stopwatch.StartNew();
                    world.Advance(frameSeconds, speed);
                    callStopwatch.Stop();

                    var ms = callStopwatch.Elapsed.TotalMilliseconds;
                    durations.Add(ms);
                    ticksProcessed += world.LastStepsProcessed;
                    if (world.IsCatchingUp)
                        catchingFrames++;
                    if (world.BacklogSeconds > peakBacklog)
                        peakBacklog = world.BacklogSeconds;

                    if (ms > stallThresholdMs)
                        throw new SelfTestFailure(
                            $"interactive stall: Advance took {ms:F1} ms (limit {stallThresholdMs:F0} ms) " +
                            $"at pop={world.AlivePopulation} x{speed:0} frame={frame}");

                    if (world.BacklogSeconds > backlogBound)
                        throw new SelfTestFailure(
                            $"backlog escaped its bound: {world.BacklogSeconds:0.###} s > {backlogBound} s " +
                            $"at pop={world.AlivePopulation} x{speed:0}");
                }

                // ClearBacklog must empty the queue and drop the flag.
                world.Advance(frameSeconds, 500f);
                world.ClearBacklog();
                if (world.BacklogSeconds != 0f || world.IsCatchingUp)
                    throw new SelfTestFailure(
                        $"ClearBacklog left backlog={world.BacklogSeconds:0.###}s catching_up={world.IsCatchingUp}");

                // Invalid speeds must be inert: no ticks, no accumulator damage.
                var tickBefore = world.Tick;
                var backlogBefore = world.BacklogSeconds;
                foreach (var bad in new[] { 0f, -25f, float.NaN, float.PositiveInfinity })
                    world.Advance(frameSeconds, bad);
                if (world.Tick != tickBefore || world.BacklogSeconds != backlogBefore ||
                    !float.IsFinite(world.BacklogSeconds))
                    throw new SelfTestFailure(
                        $"invalid timeScale mutated state: tick {tickBefore}->{world.Tick}, " +
                        $"backlog {backlogBefore:0.###}->{world.BacklogSeconds:0.###}");

                var errors = CheckInvariants(world);
                if (errors.Count > 0)
                    throw new SelfTestFailure("invariants failed", errors);

                durations.Sort();
                double Percentile(double fraction) =>
                    durations[(int)Math.Min(durations.Count - 1, Math.Floor(durations.Count * fraction))];

                var actualTicksPerSecond = ticksProcessed / (frames * (double)frameSeconds);
                var actualMultiplier = actualTicksPerSecond / 10.0; // realtime is 10 ticks/s
                var worst = durations[^1];
                if (worst > warnThresholdMs)
                    warnings.Add($"pop~{population} x{speed:0}: worst {worst:0.#} ms");

                Console.WriteLine(
                    $"           pop~{world.AlivePopulation,3} x{speed,3:0} | ticks {ticksProcessed,5} | " +
                    $"actual x{actualMultiplier,6:0.#} | p50 {Percentile(0.50),6:0.###} ms | " +
                    $"p95 {Percentile(0.95),6:0.###} ms | max {worst,7:0.###} ms | catch {100.0 * catchingFrames / frames,5:0.#}%");
            }
        }

        var notes = warnings.Count > 0
            ? new List<string>
            {
                $"{warnings.Count} perf warning(s) over {warnThresholdMs:0} ms budget (below {stallThresholdMs:F0} ms stall limit): " +
                string.Join("; ", warnings.Take(4)) + (warnings.Count > 4 ? "; ..." : ""),
            }
            : null;

        return Formatted($"frames_per_combo={frames} combos={populations.Length * speeds.Length} " +
                         $"stalls=0 backlog_bounded=yes invalid_speeds=inert clear_backlog=ok", notes);
    }

    // ------------------------------------------------------------------
    // Scenario: Interventions deterministic replay
    // ------------------------------------------------------------------
    private static string RunInterventions()
    {
        const int seed = 12345;
        const int initialPopulation = 50;
        const int runTicks = 2000;

        var original = new EmergentSimulationWorld(seed, initialPopulation, enableLod: true);
        AdvanceExact(original, runTicks);

        var bloomCell = original.Food.First(node => node.Amount > 0).Cell;
        var insightCell = original.Agents.First(agent => agent.Alive).Cell;
        var beaconCell = FindWalkableCell(original, new Point(EmergentSimulationWorld.Width / 2, EmergentSimulationWorld.Height / 2));
        var commands = new[]
        {
            (EmergentSimulationWorld.InterventionType.Bloom, bloomCell),
            (EmergentSimulationWorld.InterventionType.Beacon, beaconCell),
            (EmergentSimulationWorld.InterventionType.InsightPulse, insightCell),
        };

        foreach (var (type, cell) in commands)
        {
            var result = original.QueueIntervention(type, cell);
            if (!result.Success)
                throw new SelfTestFailure($"Could not queue {type}: {result.Reason}");
        }

        // The three queued commands reserve the starting Resonance. A fourth
        // request must fail immediately rather than pretending it is queued.
        if (original.QueueIntervention(EmergentSimulationWorld.InterventionType.Bloom, bloomCell).Success)
            throw new SelfTestFailure("Queued intervention exceeded available Resonance");

        var pendingPath = TempSavePath();
        var activePath = TempSavePath();
        try
        {
            // Save/load while commands are pending verifies the queue itself.
            WorldSaveService.Save(original, pendingPath);
            var pendingLoaded = WorldSaveService.Load(pendingPath);
            var pendingDiffs = CompareWorlds(original, pendingLoaded);
            if (pendingDiffs.Count > 0)
                throw new SelfTestFailure("pending intervention queue changed after load", pendingDiffs);

            AdvanceExact(original, 1);
            AdvanceExact(pendingLoaded, 1);
            var appliedDiffs = CompareWorlds(original, pendingLoaded);
            if (appliedDiffs.Count > 0)
                throw new SelfTestFailure("worlds diverged when queued interventions were applied", appliedDiffs);

            if (original.ActiveBlooms.Count != 1 || original.ActiveBeacons.Count != 1 ||
                original.SuccessfulInterventions != commands.Length)
                throw new SelfTestFailure("expected all three interventions to apply exactly once");

            // Save/load during active temporary effects, then continue through
            // beacon expiry and Bloom expiry.
            WorldSaveService.Save(original, activePath);
            var activeLoaded = WorldSaveService.Load(activePath);
            var activeDiffs = CompareWorlds(original, activeLoaded);
            if (activeDiffs.Count > 0)
                throw new SelfTestFailure("active intervention state changed after load", activeDiffs);

            for (var step = 1; step <= 250; step++)
            {
                AdvanceExact(original, 1);
                AdvanceExact(activeLoaded, 1);
                var midDiffs = CompareWorlds(original, activeLoaded);
                if (midDiffs.Count > 0)
                    throw new SelfTestFailure($"worlds diverged during active intervention continuation at +{step} tick(s)", midDiffs);
            }

            AdvanceExact(original, 260);
            AdvanceExact(activeLoaded, 260);
            var expiryDiffs = CompareWorlds(original, activeLoaded);
            if (expiryDiffs.Count > 0)
                throw new SelfTestFailure("worlds diverged when intervention effects expired", expiryDiffs);

            if (original.ActiveBlooms.Count != 0 || original.ActiveBeacons.Count != 0)
                throw new SelfTestFailure("temporary intervention effects did not expire");

            if (original.Resonance < 0 || original.Resonance > 10 || original.TotalResonanceSpent != commands.Length)
                throw new SelfTestFailure("Resonance accounting is inconsistent");
            if (original.InterventionLog.Count != commands.Length)
                throw new SelfTestFailure("intervention log does not contain every applied command");
            if (!original.Chronicle.Any(e => e.Type == WorldEventType.PlayerIntervention))
                throw new SelfTestFailure("Chronicle has no PlayerIntervention event");

            if (original.QueueIntervention(EmergentSimulationWorld.InterventionType.Bloom, new Point(-1, -1)).Success ||
                original.QueueIntervention(EmergentSimulationWorld.InterventionType.None, bloomCell).Success)
                throw new SelfTestFailure("invalid intervention command was accepted");

            return $"commands={commands.Length} pending_save_load=pass active_save_load=pass " +
                   $"log_entries={original.InterventionLog.Count} score={original.ScoreTotal:0.0}";
        }
        finally
        {
            TryDelete(pendingPath);
            TryDelete(activePath);
        }

        static Point FindWalkableCell(EmergentSimulationWorld world, Point center)
        {
            for (var radius = 0; radius < 20; radius++)
            {
                for (var y = center.Y - radius; y <= center.Y + radius; y++)
                {
                    for (var x = center.X - radius; x <= center.X + radius; x++)
                    {
                        if (world.Map.InBounds(x, y) && world.Map.IsWalkable(x, y))
                            return new Point(x, y);
                    }
                }
            }

            throw new SelfTestFailure("could not find a walkable beacon cell");
        }
    }

    // ------------------------------------------------------------------
    // Scenario: Shout v3 propagation, memory and deterministic save/load
    // ------------------------------------------------------------------
    private static string RunShouts()
    {
        const int seed = 24681357;
        const int initialPopulation = 80;
        const int runTicks = 1800;
        var original = new EmergentSimulationWorld(seed, initialPopulation, enableLod: true);
        AdvanceExact(original, runTicks);

        if (original.ShoutsMade == 0)
            throw new SelfTestFailure("Shout scenario produced no signals in the deterministic window");
        if (original.ShoutsHeard == 0)
            throw new SelfTestFailure("Shout scenario produced signals but no recipients heard them");
        if (original.ShoutLearningEvents == 0)
            throw new SelfTestFailure("Shout scenario produced no outcome-based learning events");
        if (original.SuccessfulShoutLessons + original.FailedShoutLessons != original.ShoutLearningEvents)
            throw new SelfTestFailure("Shout learning counters do not add up");
        if (original.FactionShoutLearningEvents != original.ShoutLearningEvents)
            throw new SelfTestFailure("Faction shout learning counters do not match agent outcomes");
        if (original.ShoutReputationRecords == 0)
            throw new SelfTestFailure("Shout scenario produced no source reputation records");
        if (original.ShoutMemoryRecords == 0)
            throw new SelfTestFailure("Shout scenario produced no multi-signal memories");
        if (original.Agents.Any(agent => (agent.ShoutReputations?.Count ?? 0) > 8))
            throw new SelfTestFailure("Shout reputation memory exceeded its bound");
        if (original.Agents.Any(agent => (agent.ShoutMemories?.Count ?? 0) > 6))
            throw new SelfTestFailure("Shout signal memory exceeded its bound");
        if (!original.Agents.SelectMany(agent => agent.ShoutReputations ?? Enumerable.Empty<ShoutReputation>())
                .Any(reputation => reputation.FoodTrust != 0.5f || reputation.DangerTrust != 0.5f || reputation.RallyTrust != 0.5f))
            throw new SelfTestFailure("Source reputation never learned from an outcome");
        if (!original.Factions.Any(faction =>
                MathF.Abs(faction.FoodSignalReliability - 0.5f) > 0.0001f ||
                MathF.Abs(faction.DangerSignalReliability - 0.5f) > 0.0001f ||
                MathF.Abs(faction.RallySignalReliability - 0.5f) > 0.0001f))
            throw new SelfTestFailure("Faction signal reliability never learned from an outcome");

        var path = TempSavePath();
        try
        {
            WorldSaveService.Save(original, path);
            var loaded = WorldSaveService.Load(path);
            var diffs = CompareWorlds(original, loaded);
            if (diffs.Count > 0)
                throw new SelfTestFailure("Shout state changed after save/load", diffs);

            AdvanceExact(original, 300);
            AdvanceExact(loaded, 300);
            diffs = CompareWorlds(original, loaded);
            if (diffs.Count > 0)
                throw new SelfTestFailure("Shout worlds diverged after continuation", diffs);

            if (original.FoodShouts + original.DangerShouts + original.RallyShouts != original.ShoutsMade)
                throw new SelfTestFailure("Shout type counters do not add up to total signals");

            return $"made={original.ShoutsMade} heard={original.ShoutsHeard} " +
                   $"types=food:{original.FoodShouts},danger:{original.DangerShouts},rally:{original.RallyShouts} " +
                   $"learning={original.ShoutLearningEvents} ({original.SuccessfulShoutLessons}+/{original.FailedShoutLessons}-) " +
                   $"reputation_records={original.ShoutReputationRecords} " +
                   $"memory_records={original.ShoutMemoryRecords} " +
                   $"visible={original.RecentShouts.Count} save_load=pass";
        }
        finally
        {
            TryDelete(path);
        }
    }

    // ------------------------------------------------------------------
    // Exact-tick advancement
    // ------------------------------------------------------------------

    /// <summary>
    /// Advances exactly <paramref name="ticks"/> simulation steps regardless of
    /// frame budgets: each call queues exactly one tick length and allows one step.
    /// </summary>
    private static void AdvanceExact(EmergentSimulationWorld world, int ticks)
    {
        for (var i = 0; i < ticks; i++)
            world.Advance(EmergentSimulationWorld.TickLength, 1f, maxTicks: 1);
    }

    // ------------------------------------------------------------------
    // Simulation invariants
    // ------------------------------------------------------------------

    private static List<string> CheckInvariants(EmergentSimulationWorld world)
    {
        var errors = new List<string>();
        var map = world.Map;

        var liveIds = new HashSet<int>();
        var directAlive = 0;
        foreach (var agent in world.Agents)
        {
            if (!agent.Alive)
                continue;
            directAlive++;

            if (!liveIds.Add(agent.Id))
                errors.Add($"duplicate live agent id {agent.Id}");
            if (!map.InBounds(agent.Cell))
                errors.Add($"agent {agent.Id} out of bounds at {agent.Cell.X},{agent.Cell.Y}");
            else if (map[agent.Cell] == CellType.Wall && !HasAdjacentWalkable(map, agent.Cell))
                errors.Add($"agent {agent.Id} is buried in walls at {agent.Cell.X},{agent.Cell.Y}");

            CheckFinite(agent.Energy, $"agent {agent.Id}.Energy", errors);
            if (agent.Energy < 0f || agent.Energy > 100f)
                errors.Add($"agent {agent.Id}.Energy out of range: {Format(agent.Energy)}");
            CheckFinite(agent.Age, $"agent {agent.Id}.Age", errors);
            CheckFinite(agent.MaxAge, $"agent {agent.Id}.MaxAge", errors);
            CheckFinite(agent.MoveCooldown, $"agent {agent.Id}.MoveCooldown", errors);
            CheckFinite(agent.BuildCooldown, $"agent {agent.Id}.BuildCooldown", errors);
            CheckFinite(agent.FeedingCooldown, $"agent {agent.Id}.FeedingCooldown", errors);
            CheckFinite(agent.RoleExperience, $"agent {agent.Id}.RoleExperience", errors);
            CheckFinite(agent.LearningRate, $"agent {agent.Id}.LearningRate", errors);
            CheckFinite(agent.Intelligence, $"agent {agent.Id}.Intelligence", errors);
            CheckFinite(agent.PlanningSkill, $"agent {agent.Id}.PlanningSkill", errors);
            CheckFinite(agent.SocialAwareness, $"agent {agent.Id}.SocialAwareness", errors);
            CheckFinite(agent.BuildDrive, $"agent {agent.Id}.BuildDrive", errors);
            CheckFinite(agent.ExplorationDrive, $"agent {agent.Id}.ExplorationDrive", errors);
            CheckFinite(agent.RiskTolerance, $"agent {agent.Id}.RiskTolerance", errors);
            CheckFinite(agent.FoodKnowledge, $"agent {agent.Id}.FoodKnowledge", errors);
            CheckFinite(agent.RouteKnowledge, $"agent {agent.Id}.RouteKnowledge", errors);
            CheckFinite(agent.DangerKnowledge, $"agent {agent.Id}.DangerKnowledge", errors);
            CheckFinite(agent.FoodUtilityBias, $"agent {agent.Id}.FoodUtilityBias", errors);
            CheckFinite(agent.BuildUtilityBias, $"agent {agent.Id}.BuildUtilityBias", errors);
            CheckFinite(agent.ExploreUtilityBias, $"agent {agent.Id}.ExploreUtilityBias", errors);
            CheckFinite(agent.RestUtilityBias, $"agent {agent.Id}.RestUtilityBias", errors);
            CheckFinite(agent.LastDecisionScore, $"agent {agent.Id}.LastDecisionScore", errors);
            CheckFinite(agent.ElderWisdomBonus, $"agent {agent.Id}.ElderWisdomBonus", errors);

            if (agent.CarriedFood < 0)
                errors.Add($"agent {agent.Id} carries negative food: {agent.CarriedFood}");
            if (agent.SettlementId != -1 && !world.Settlements.ContainsKey(agent.SettlementId))
                errors.Add($"agent {agent.Id} references missing settlement {agent.SettlementId}");
        }

        if (directAlive != world.AlivePopulation)
            errors.Add($"cached population {world.AlivePopulation} != direct count {directAlive}");

        if (world.FoodStockpile < 0)
            errors.Add($"negative food stockpile: {world.FoodStockpile}");
        if (world.StoredFoodUnits < 0)
            errors.Add($"negative stored food: {world.StoredFoodUnits}");
        if (world.Births < 0 || world.Deaths < 0 || world.StarvationDeaths < 0 ||
            world.FoodConsumed < 0 || world.FoodGathered < 0)
            errors.Add("negative lifetime counter");

        foreach (var faction in world.Factions)
        {
            if (faction.Population < 0 || faction.FoodStored < 0)
                errors.Add($"faction {faction.Id} negative counter");
            CheckFinite(faction.AverageEnergy, $"faction {faction.Id}.AverageEnergy", errors);
            CheckFinite(faction.Cohesion, $"faction {faction.Id}.Cohesion", errors);
        }

        foreach (var settlement in world.Settlements.Values)
        {
            CheckFinite(settlement.AverageEnergy, $"settlement {settlement.Id}.AverageEnergy", errors);
            CheckFinite(settlement.Cohesion, $"settlement {settlement.Id}.Cohesion", errors);
            CheckFinite(settlement.TerritoryPressure, $"settlement {settlement.Id}.TerritoryPressure", errors);
        }

        foreach (var node in world.Food)
        {
            if (node.Amount < 0)
                errors.Add($"negative food amount at {node.Cell.X},{node.Cell.Y}");
            if (!map.InBounds(node.Cell))
                errors.Add($"food node out of bounds at {node.Cell.X},{node.Cell.Y}");
        }

        foreach (var storage in world.FoodStorage)
        {
            if (storage.Value <= 0)
                errors.Add($"non-positive storage pile at {storage.Key.X},{storage.Key.Y}");
            if (!map.InBounds(storage.Key))
                errors.Add($"storage pile out of bounds at {storage.Key.X},{storage.Key.Y}");
        }

        // Chunk bookkeeping must match reality.
        var chunkWidth = world.Chunks.ChunkWidth;
        var expectedAgents = new int[chunkWidth * world.Chunks.ChunkHeight];
        var expectedFood = new int[expectedAgents.Length];
        var expectedWalls = new int[expectedAgents.Length];

        foreach (var agent in world.Agents)
        {
            if (!agent.Alive)
                continue;
            expectedAgents[(agent.Cell.Y / ChunkSize) * chunkWidth + agent.Cell.X / ChunkSize]++;
        }
        foreach (var node in world.Food)
        {
            if (node.Amount > 0)
                expectedFood[(node.Cell.Y / ChunkSize) * chunkWidth + node.Cell.X / ChunkSize]++;
        }
        foreach (var wall in world.Map.WallCells)
            expectedWalls[(wall.Y / ChunkSize) * chunkWidth + wall.X / ChunkSize]++;

        var aggregatedSum = 0;
        for (var cy = 0; cy < world.Chunks.ChunkHeight; cy++)
        {
            for (var cx = 0; cx < chunkWidth; cx++)
            {
                var chunk = world.Chunks.GetChunk(cx, cy);
                var index = cy * chunkWidth + cx;
                if (chunk.AgentCount != expectedAgents[index])
                    errors.Add($"chunk {cx},{cy} AgentCount={chunk.AgentCount} expected {expectedAgents[index]}");
                if (chunk.FoodCount != expectedFood[index])
                    errors.Add($"chunk {cx},{cy} FoodCount={chunk.FoodCount} expected {expectedFood[index]}");
                if (chunk.WallCount != expectedWalls[index])
                    errors.Add($"chunk {cx},{cy} WallCount={chunk.WallCount} expected {expectedWalls[index]}");
                if (chunk.IsAggregated)
                    aggregatedSum += chunk.AggregatedPopulation;
            }
        }

        if (aggregatedSum != world.AggregatedAgentCount)
            errors.Add($"AggregatedAgentCount {world.AggregatedAgentCount} != aggregated chunk sum {aggregatedSum}");

        // Every alive agent must have a LOD classification, with exactly one
        // sanctioned exception: agents born during the most recent completed
        // tick are classified by the next UpdateAgentLOD. Anything beyond
        // that transient is a bookkeeping defect, not tolerated noise.
        var unclassified = world.UnclassifiedAliveAgents;
        if (unclassified > world.BirthsThisTick)
            errors.Add($"{unclassified} alive agents lack LOD classification " +
                       $"(births last tick: {world.BirthsThisTick})");
        if (world.ActiveAgentCount > world.AlivePopulation)
            errors.Add($"ActiveAgentCount {world.ActiveAgentCount} exceeds alive population {world.AlivePopulation}");

        return errors;
    }

    private static void CheckFinite(float value, string name, List<string> errors)
    {
        if (!float.IsFinite(value))
            errors.Add($"{name} is not finite: {value}");
    }

    /// <summary>
    /// Builders place a wall into their own cell by design and step out on the
    /// next move, so standing on a wall is an accepted transient. Being fully
    /// walled in with no adjacent passable cell is not.
    /// </summary>
    private static bool HasAdjacentWalkable(Map map, Point cell)
    {
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0)
                    continue;
                if (map.IsWalkable(cell.X + dx, cell.Y + dy))
                    return true;
            }
        }
        return false;
    }

    // ------------------------------------------------------------------
    // Typed state comparison (precise diffs)
    // ------------------------------------------------------------------

    private static List<string> CompareWorlds(EmergentSimulationWorld a, EmergentSimulationWorld b)
    {
        var diffs = new List<string>();

        void Eq(long av, long bv, string name)
        {
            if (av != bv)
                diffs.Add($"{name}: {av} vs {bv}");
        }

        void EqStr(string av, string bv, string name)
        {
            if (!string.Equals(av, bv, StringComparison.Ordinal))
                diffs.Add($"{name}: '{av}' vs '{bv}'");
        }

        void EqBool(bool av, bool bv, string name)
        {
            if (av != bv)
                diffs.Add($"{name}: {av} vs {bv}");
        }

        void EqF(float av, float bv, string name)
        {
            if (BitConverter.SingleToUInt32Bits(av) != BitConverter.SingleToUInt32Bits(bv))
                diffs.Add($"{name}: {Format(av)} vs {Format(bv)}");
        }

        Eq(a.Seed, b.Seed, "seed");
        Eq((long)a.RandomState, (long)b.RandomState, "random_state");
        Eq(a.Tick, b.Tick, "tick");
        EqBool(a.LodEnabled, b.LodEnabled, "lod_enabled");
        Eq(a.FoodStockpile, b.FoodStockpile, "food_stockpile");
        Eq(a.StoredFoodUnits, b.StoredFoodUnits, "stored_food");
        EqStr(System.Text.Json.JsonSerializer.Serialize(a.Ecology), System.Text.Json.JsonSerializer.Serialize(b.Ecology), "ecology");
        Eq(a.Births, b.Births, "births");
        Eq(a.Deaths, b.Deaths, "deaths");
        Eq(a.StarvationDeaths, b.StarvationDeaths, "starvation_deaths");
        Eq(a.FoodConsumed, b.FoodConsumed, "food_consumed");
        Eq(a.FoodGathered, b.FoodGathered, "food_gathered");
        Eq(a.FoodShared, b.FoodShared, "food_shared");
        Eq(a.KnowledgeShared, b.KnowledgeShared, "knowledge_shared");
        Eq(a.ResourceSurges, b.ResourceSurges, "resource_surges");
        Eq(a.ScarcityEvents, b.ScarcityEvents, "scarcity_events");
        Eq(a.MigrationWaves, b.MigrationWaves, "migration_waves");
        Eq(a.WallBlocksBuilt, b.WallBlocksBuilt, "walls_built");
        Eq(a.WallBlocksRemoved, b.WallBlocksRemoved, "walls_removed");
        Eq(a.EventTicksRemaining, b.EventTicksRemaining, "event_ticks_remaining");
        EqStr(a.CurrentEvent, b.CurrentEvent, "current_event");
        Eq(a.ActiveAgentCount, b.ActiveAgentCount, "active_agents");
        Eq(a.DormantAgentCount, b.DormantAgentCount, "dormant_agents");
        Eq(a.AggregatedAgentCount, b.AggregatedAgentCount, "aggregated_agents");
        Eq(a.NextSettlementId, b.NextSettlementId, "next_settlement_id");

        // Map cells (full grid, covers walls/doors/storage/floor).
        var cellsA = a.Map.ExportCells();
        var cellsB = b.Map.ExportCells();
        if (!cellsA.AsSpan().SequenceEqual(cellsB))
            diffs.Add("map_cells differ");

        // Walls as an unordered set (list order is history-dependent).
        var wallsA = a.Map.WallCells.OrderBy(p => p.Y * EmergentSimulationWorld.Width + p.X).ToList();
        var wallsB = b.Map.WallCells.OrderBy(p => p.Y * EmergentSimulationWorld.Width + p.X).ToList();
        Eq(wallsA.Count, wallsB.Count, "wall_count");
        var wallPairs = wallsA.Zip(wallsB, (pa, pb) => (pa, pb));
        foreach (var (pa, pb) in wallPairs)
        {
            if (pa != pb)
            {
                diffs.Add($"wall cell mismatch: {pa.X},{pa.Y} vs {pb.X},{pb.Y}");
                break;
            }
        }

        // Agents: identical id sequence, then full field comparison.
        var agentsA = a.Agents.OrderBy(x => x.Id).ToList();
        var agentsB = b.Agents.OrderBy(x => x.Id).ToList();
        Eq(agentsA.Count, agentsB.Count, "agent_count");
        var pairs = Math.Min(agentsA.Count, agentsB.Count);
        for (var i = 0; i < pairs; i++)
        {
            var x = agentsA[i];
            var y = agentsB[i];
            var p = $"agent[{x.Id}]";
            Eq(x.Id, y.Id, $"{p}.id");
            Eq(x.FactionId, y.FactionId, $"{p}.faction");
            Eq(x.Cell.X, y.Cell.X, $"{p}.cell_x");
            Eq(x.Cell.Y, y.Cell.Y, $"{p}.cell_y");
            Eq(x.Facing.X, y.Facing.X, $"{p}.facing_x");
            Eq(x.Facing.Y, y.Facing.Y, $"{p}.facing_y");
            Eq(x.TargetCell.X, y.TargetCell.X, $"{p}.target_x");
            Eq(x.TargetCell.Y, y.TargetCell.Y, $"{p}.target_y");
            Eq(x.FoodTargetCell.X, y.FoodTargetCell.X, $"{p}.food_target_x");
            Eq(x.FoodTargetCell.Y, y.FoodTargetCell.Y, $"{p}.food_target_y");
            EqBool(x.HasFoodTarget, y.HasFoodTarget, $"{p}.has_food_target");
            Eq(x.PathIndex, y.PathIndex, $"{p}.path_index");
            Eq(x.Path.Count, y.Path.Count, $"{p}.path_length");
            EqF(x.Energy, y.Energy, $"{p}.energy");
            EqF(x.Age, y.Age, $"{p}.age");
            Eq(x.CarriedFood, y.CarriedFood, $"{p}.carried_food");
            EqF(x.MoveCooldown, y.MoveCooldown, $"{p}.move_cooldown");
            EqF(x.RestTimer, y.RestTimer, $"{p}.rest_timer");
            EqF(x.BuildCooldown, y.BuildCooldown, $"{p}.build_cooldown");
            EqF(x.FeedingCooldown, y.FeedingCooldown, $"{p}.feeding_cooldown");
            EqF(x.ExplorationCooldown, y.ExplorationCooldown, $"{p}.exploration_cooldown");
            Eq((int)x.Role, (int)y.Role, $"{p}.role");
            EqF(x.RoleExperience, y.RoleExperience, $"{p}.role_experience");
            Eq(x.HomeWallCell.X, y.HomeWallCell.X, $"{p}.home_wall_x");
            Eq(x.HomeWallCell.Y, y.HomeWallCell.Y, $"{p}.home_wall_y");
            EqBool(x.HasHomeWall, y.HasHomeWall, $"{p}.has_home_wall");
            Eq(x.KnownFoodCell.X, y.KnownFoodCell.X, $"{p}.known_food_x");
            Eq(x.KnownFoodCell.Y, y.KnownFoodCell.Y, $"{p}.known_food_y");
            EqBool(x.HasKnownFood, y.HasKnownFood, $"{p}.has_known_food");
            EqF(x.FoodKnowledge, y.FoodKnowledge, $"{p}.food_knowledge");
            Eq(x.SuccessfulFoodTrips, y.SuccessfulFoodTrips, $"{p}.successful_trips");
            Eq(x.FailedFoodTrips, y.FailedFoodTrips, $"{p}.failed_trips");
            Eq(x.FoodEaten, y.FoodEaten, $"{p}.food_eaten");
            Eq(x.ExplorationTrips, y.ExplorationTrips, $"{p}.exploration_trips");
            Eq(x.SharedMemories, y.SharedMemories, $"{p}.shared_memories");
            EqBool(x.HasDangerMemory, y.HasDangerMemory, $"{p}.has_danger_memory");
            EqF(x.DangerKnowledge, y.DangerKnowledge, $"{p}.danger_knowledge");
            EqF(x.RouteKnowledge, y.RouteKnowledge, $"{p}.route_knowledge");
            EqF(x.BuildDrive, y.BuildDrive, $"{p}.build_drive");
            EqF(x.ExplorationDrive, y.ExplorationDrive, $"{p}.exploration_drive");
            EqF(x.RiskTolerance, y.RiskTolerance, $"{p}.risk_tolerance");
            EqF(x.LearningRate, y.LearningRate, $"{p}.learning_rate");
            EqF(x.Intelligence, y.Intelligence, $"{p}.intelligence");
            EqF(x.PlanningSkill, y.PlanningSkill, $"{p}.planning_skill");
            EqF(x.SocialAwareness, y.SocialAwareness, $"{p}.social_awareness");
            Eq(x.PreferredBuildDirection, y.PreferredBuildDirection, $"{p}.build_direction");
            EqF(x.FoodUtilityBias, y.FoodUtilityBias, $"{p}.food_bias");
            EqF(x.BuildUtilityBias, y.BuildUtilityBias, $"{p}.build_bias");
            EqF(x.ExploreUtilityBias, y.ExploreUtilityBias, $"{p}.explore_bias");
            EqF(x.RestUtilityBias, y.RestUtilityBias, $"{p}.rest_bias");
            Eq((int)x.LastDecisionAction, (int)y.LastDecisionAction, $"{p}.last_action");
            EqF(x.LastDecisionScore, y.LastDecisionScore, $"{p}.last_score");
            Eq(x.DecisionsMade, y.DecisionsMade, $"{p}.decisions");
            Eq(x.LearningUpdates, y.LearningUpdates, $"{p}.learning_updates");
            Eq(x.PositiveOutcomes, y.PositiveOutcomes, $"{p}.positive_outcomes");
            Eq(x.NegativeOutcomes, y.NegativeOutcomes, $"{p}.negative_outcomes");
            EqF(x.ShoutCooldown, y.ShoutCooldown, "agent shout cooldown");
            Eq(x.ShoutsMade, y.ShoutsMade, "agent shouts made");
            Eq(x.ShoutsHeard, y.ShoutsHeard, "agent shouts heard");
            Eq((int)x.LastShoutType, (int)y.LastShoutType, "agent last shout type");
            Eq(x.LastShoutTick, y.LastShoutTick, "agent last shout tick");
            Eq((int)x.LastHeardShoutType, (int)y.LastHeardShoutType, "agent last heard shout type");
            Eq(x.LastHeardShoutTick, y.LastHeardShoutTick, "agent last heard shout tick");
            Eq(x.LastHeardShoutCell.X, y.LastHeardShoutCell.X, "agent last heard shout x");
            Eq(x.LastHeardShoutCell.Y, y.LastHeardShoutCell.Y, "agent last heard shout y");
            EqF(x.LastHeardShoutStrength, y.LastHeardShoutStrength, "agent last heard shout strength");
            Eq(x.LastHeardShoutSenderId, y.LastHeardShoutSenderId, "agent last heard shout sender");
            EqBool(x.LastHeardShoutEvaluated, y.LastHeardShoutEvaluated, "agent last heard shout evaluated");
            EqF(x.ShoutFoodTrust, y.ShoutFoodTrust, "agent food shout trust");
            EqF(x.ShoutDangerTrust, y.ShoutDangerTrust, "agent danger shout trust");
            EqF(x.ShoutRallyTrust, y.ShoutRallyTrust, "agent rally shout trust");
            Eq(x.ShoutLearningEvents, y.ShoutLearningEvents, "agent shout learning events");
            Eq(x.SuccessfulShoutLessons, y.SuccessfulShoutLessons, "agent successful shout lessons");
            Eq(x.FailedShoutLessons, y.FailedShoutLessons, "agent failed shout lessons");
            var xReputations = x.ShoutReputations ?? new List<ShoutReputation>();
            var yReputations = y.ShoutReputations ?? new List<ShoutReputation>();
            Eq(xReputations.Count, yReputations.Count, "agent shout reputation count");
            for (var reputationIndex = 0; reputationIndex < Math.Min(xReputations.Count, yReputations.Count); reputationIndex++)
            {
                var xr = xReputations.OrderBy(entry => entry.SenderId).ElementAt(reputationIndex);
                var yr = yReputations.OrderBy(entry => entry.SenderId).ElementAt(reputationIndex);
                Eq(xr.SenderId, yr.SenderId, "shout reputation sender");
                EqF(xr.FoodTrust, yr.FoodTrust, "shout reputation food trust");
                EqF(xr.DangerTrust, yr.DangerTrust, "shout reputation danger trust");
                EqF(xr.RallyTrust, yr.RallyTrust, "shout reputation rally trust");
                Eq(xr.HeardCount, yr.HeardCount, "shout reputation heard");
                Eq(xr.LearningEvents, yr.LearningEvents, "shout reputation learning");
                Eq(xr.LastSeenTick, yr.LastSeenTick, "shout reputation last seen");
            }
            var xMemories = x.ShoutMemories ?? new List<ShoutMemory>();
            var yMemories = y.ShoutMemories ?? new List<ShoutMemory>();
            Eq(xMemories.Count, yMemories.Count, "agent shout memory count");
            var orderedXMemories = xMemories.OrderBy(entry => entry.Type).ThenBy(entry => entry.Cell.Y)
                .ThenBy(entry => entry.Cell.X).ThenBy(entry => entry.SenderId).ToList();
            var orderedYMemories = yMemories.OrderBy(entry => entry.Type).ThenBy(entry => entry.Cell.Y)
                .ThenBy(entry => entry.Cell.X).ThenBy(entry => entry.SenderId).ToList();
            for (var memoryIndex = 0; memoryIndex < Math.Min(orderedXMemories.Count, orderedYMemories.Count); memoryIndex++)
            {
                var xm = orderedXMemories[memoryIndex];
                var ym = orderedYMemories[memoryIndex];
                Eq(xm.SenderId, ym.SenderId, "shout memory sender");
                Eq(xm.FactionId, ym.FactionId, "shout memory faction");
                Eq((int)xm.Type, (int)ym.Type, "shout memory type");
                Eq(xm.Cell.X, ym.Cell.X, "shout memory x");
                Eq(xm.Cell.Y, ym.Cell.Y, "shout memory y");
                EqF(xm.Confidence, ym.Confidence, "shout memory confidence");
                EqF(xm.Strength, ym.Strength, "shout memory strength");
                Eq(xm.SuccessfulOutcomes, ym.SuccessfulOutcomes, "shout memory successful");
                Eq(xm.FailedOutcomes, ym.FailedOutcomes, "shout memory failed");
                Eq(xm.HeardTick, ym.HeardTick, "shout memory heard tick");
                Eq(xm.LastOutcomeTick, ym.LastOutcomeTick, "shout memory outcome tick");
            }
            Eq((int)x.Action, (int)y.Action, $"{p}.action");
            EqBool(x.Alive, y.Alive, $"{p}.alive");
            Eq(x.Generation, y.Generation, $"{p}.generation");
            Eq(x.ParentId1, y.ParentId1, $"{p}.parent1");
            Eq(x.ParentId2, y.ParentId2, $"{p}.parent2");
            EqF(x.MaxAge, y.MaxAge, $"{p}.max_age");
            EqBool(x.IsElder, y.IsElder, $"{p}.is_elder");
            EqF(x.ElderWisdomBonus, y.ElderWisdomBonus, $"{p}.wisdom_bonus");
            Eq(x.SettlementId, y.SettlementId, $"{p}.settlement_id");
        }

        // Food resources: same multiset keyed by cell.
        var foodA = a.Food.OrderBy(n => n.Cell.Y * EmergentSimulationWorld.Width + n.Cell.X).ToList();
        var foodB = b.Food.OrderBy(n => n.Cell.Y * EmergentSimulationWorld.Width + n.Cell.X).ToList();
        Eq(foodA.Count, foodB.Count, "food_node_count");
        for (var i = 0; i < Math.Min(foodA.Count, foodB.Count); i++)
        {
            var na = foodA[i];
            var nb = foodB[i];
            if (na.Cell != nb.Cell)
            {
                diffs.Add($"food node order mismatch: {na.Cell.X},{na.Cell.Y} vs {nb.Cell.X},{nb.Cell.Y}");
                break;
            }
            Eq(na.Amount, nb.Amount, $"food[{na.Cell.X},{na.Cell.Y}].amount");
            var resA = na.ReservedBy.OrderBy(id => id).ToArray();
            var resB = nb.ReservedBy.OrderBy(id => id).ToArray();
            if (!resA.SequenceEqual(resB))
                diffs.Add($"food[{na.Cell.X},{na.Cell.Y}].reserved: [{string.Join(",", resA)}] vs [{string.Join(",", resB)}]");
        }

        // Storage piles.
        var storageA = a.FoodStorage.OrderBy(kv => kv.Key.Y * EmergentSimulationWorld.Width + kv.Key.X).ToList();
        var storageB = b.FoodStorage.OrderBy(kv => kv.Key.Y * EmergentSimulationWorld.Width + kv.Key.X).ToList();
        Eq(storageA.Count, storageB.Count, "storage_pile_count");
        for (var i = 0; i < Math.Min(storageA.Count, storageB.Count); i++)
        {
            if (storageA[i].Key != storageB[i].Key || storageA[i].Value != storageB[i].Value)
                diffs.Add($"storage mismatch: {storageA[i].Key.X},{storageA[i].Key.Y}={storageA[i].Value} vs " +
                          $"{storageB[i].Key.X},{storageB[i].Key.Y}={storageB[i].Value}");
        }

        // Factions.
        Eq(a.Factions.Count, b.Factions.Count, "faction_count");
        for (var i = 0; i < Math.Min(a.Factions.Count, b.Factions.Count); i++)
        {
            var fa = a.Factions[i];
            var fb = b.Factions[i];
            var p = $"faction[{fa.Id}]";
            Eq(fa.Id, fb.Id, $"{p}.id");
            Eq((int)fa.Goal, (int)fb.Goal, $"{p}.goal");
            Eq(fa.Population, fb.Population, $"{p}.population");
            EqF(fa.AverageEnergy, fb.AverageEnergy, $"{p}.avg_energy");
            EqF(fa.FoodFocus, fb.FoodFocus, $"{p}.food_focus");
            EqF(fa.BuildFocus, fb.BuildFocus, $"{p}.build_focus");
            EqF(fa.ExploreFocus, fb.ExploreFocus, $"{p}.explore_focus");
            EqF(fa.Cohesion, fb.Cohesion, $"{p}.cohesion");
            EqF(fa.TerritoryPressure, fb.TerritoryPressure, $"{p}.territory_pressure");
            EqF(fa.FoodSignalReliability, fb.FoodSignalReliability, $"{p}.food_signal_reliability");
            EqF(fa.DangerSignalReliability, fb.DangerSignalReliability, $"{p}.danger_signal_reliability");
            EqF(fa.RallySignalReliability, fb.RallySignalReliability, $"{p}.rally_signal_reliability");
            Eq(fa.ShoutLearningEvents, fb.ShoutLearningEvents, $"{p}.shout_learning_events");
            Eq(fa.FoodStored, fb.FoodStored, $"{p}.food_stored");
            Eq(fa.FoodConsumed, fb.FoodConsumed, $"{p}.food_consumed");
            Eq(fa.KnowledgeShared, fb.KnowledgeShared, $"{p}.knowledge_shared");
            Eq(fa.Births, fb.Births, $"{p}.births");
            Eq(fa.Deaths, fb.Deaths, $"{p}.deaths");
        }

        // Chronicle: ordered sequence.
        Eq(a.Chronicle.Count, b.Chronicle.Count, "chronicle_count");
        for (var i = 0; i < Math.Min(a.Chronicle.Count, b.Chronicle.Count); i++)
        {
            var ea = a.Chronicle[i];
            var eb = b.Chronicle[i];
            if (ea.Tick != eb.Tick || ea.Type != eb.Type || ea.Description != eb.Description ||
                ea.FactionId != eb.FactionId || ea.Importance != eb.Importance || ea.HasCell != eb.HasCell ||
                ea.Cell != eb.Cell)
                diffs.Add($"chronicle[{i}]: tick{ea.Tick}/{ea.Type} '{ea.Description}' vs tick{eb.Tick}/{eb.Type} '{eb.Description}'");
        }

        // Settlements: same ids and fields.
        Eq(a.Settlements.Count, b.Settlements.Count, "settlement_count");
        foreach (var idA in a.Settlements.Keys.OrderBy(k => k))
        {
            if (!b.Settlements.TryGetValue(idA, out var sb))
            {
                diffs.Add($"settlement {idA} missing in second world");
                continue;
            }
            var sa = a.Settlements[idA];
            var p = $"settlement[{idA}]";
            Eq(sa.FactionId, sb.FactionId, $"{p}.faction");
            Eq(sa.CenterCell.X, sb.CenterCell.X, $"{p}.center_x");
            Eq(sa.CenterCell.Y, sb.CenterCell.Y, $"{p}.center_y");
            Eq(sa.Population, sb.Population, $"{p}.population");
            Eq(sa.WallCount, sb.WallCount, $"{p}.wall_count");
            Eq(sa.FoodStored, sb.FoodStored, $"{p}.food_stored");
            EqF(sa.AverageEnergy, sb.AverageEnergy, $"{p}.avg_energy");
            Eq(sa.FoundedTick, sb.FoundedTick, $"{p}.founded_tick");
            Eq(sa.LastActiveTick, sb.LastActiveTick, $"{p}.last_active_tick");
            Eq(sa.Generation, sb.Generation, $"{p}.generation");
            Eq(sa.ElderCount, sb.ElderCount, $"{p}.elders");
            EqF(sa.Cohesion, sb.Cohesion, $"{p}.cohesion");
            Eq(sa.Births, sb.Births, $"{p}.births");
            Eq(sa.Deaths, sb.Deaths, $"{p}.deaths");
            EqF(sa.TerritoryPressure, sb.TerritoryPressure, $"{p}.territory_pressure");
            EqBool(sa.IsAbandoned, sb.IsAbandoned, $"{p}.abandoned");
        }

        // Milestones, anti-replay state (v12) and v11 determinism companions.
        CompareIntSets(a.RecordedSettlementMilestones, b.RecordedSettlementMilestones, "recorded_milestones", diffs);
        CompareIntSets(GetRecordedGenerations(a), GetRecordedGenerations(b), "recorded_generations", diffs);
        CompareIntSets(a.KnownClusterChunks, b.KnownClusterChunks, "known_cluster_chunks", diffs);

        var onceA = a.FiredOnceEvents.Select(t => (int)t).OrderBy(t => t).ToArray();
        var onceB = b.FiredOnceEvents.Select(t => (int)t).OrderBy(t => t).ToArray();
        if (!onceA.SequenceEqual(onceB))
            diffs.Add($"once_events: [{string.Join(",", onceA)}] vs [{string.Join(",", onceB)}]");

        var cdA = a.EventCooldowns.OrderBy(kv => (int)kv.Key)
            .Select(kv => $"{(int)kv.Key}:{kv.Value}").ToArray();
        var cdb = b.EventCooldowns.OrderBy(kv => (int)kv.Key)
            .Select(kv => $"{(int)kv.Key}:{kv.Value}").ToArray();
        if (!cdA.SequenceEqual(cdb))
            diffs.Add($"event_cooldowns: [{string.Join(",", cdA)}] vs [{string.Join(",", cdb)}]");

        CompareFirstCycleState(a.CreateSnapshot(), b.CreateSnapshot(), diffs);

        Eq(a.Factions.Count, b.Factions.Count, "last_goal_faction_count");
        CompareLastGoals(a, b, diffs);

        // Chunks: all fields, including aggregated LOD block.
        Eq(a.Chunks.TotalChunks, b.Chunks.TotalChunks, "chunk_total");
        for (var cy = 0; cy < a.Chunks.ChunkHeight; cy++)
        {
            for (var cx = 0; cx < a.Chunks.ChunkWidth; cx++)
            {
                var ca = a.Chunks.GetChunk(cx, cy);
                var cb = b.Chunks.GetChunk(cx, cy);
                var p = $"chunk[{cx},{cy}]";
                Eq(ca.AgentCount, cb.AgentCount, $"{p}.agents");
                Eq(ca.FoodCount, cb.FoodCount, $"{p}.food");
                Eq(ca.WallCount, cb.WallCount, $"{p}.walls");
                EqF(ca.ActivityLevel, cb.ActivityLevel, $"{p}.activity");
                Eq(ca.LastUpdateTick, cb.LastUpdateTick, $"{p}.last_tick");
                Eq(ca.Faction0Count, cb.Faction0Count, $"{p}.f0");
                Eq(ca.Faction1Count, cb.Faction1Count, $"{p}.f1");
                EqF(ca.BirthRate, cb.BirthRate, $"{p}.birth_rate");
                EqF(ca.DeathRate, cb.DeathRate, $"{p}.death_rate");
                EqF(ca.MigrationPressure, cb.MigrationPressure, $"{p}.migration");
                Eq(ca.SettlementId, cb.SettlementId, $"{p}.settlement_id");
                EqF(ca.AvgEnergy, cb.AvgEnergy, $"{p}.avg_energy");
                Eq(ca.TotalFoodConsumed, cb.TotalFoodConsumed, $"{p}.total_consumed");
                Eq(ca.TotalFoodStored, cb.TotalFoodStored, $"{p}.total_stored");
                Eq(ca.AggregatedPopulation, cb.AggregatedPopulation, $"{p}.agg_population");
                EqF(ca.AggregatedAvgEnergy, cb.AggregatedAvgEnergy, $"{p}.agg_energy");
                Eq(ca.AggregatedFoodStockpile, cb.AggregatedFoodStockpile, $"{p}.agg_stockpile");
                Eq(ca.AggregatedFactionGoal, cb.AggregatedFactionGoal, $"{p}.agg_goal");
                EqF(ca.AggregatedCohesion, cb.AggregatedCohesion, $"{p}.agg_cohesion");
                Eq(ca.LastAggregatedUpdateTick, cb.LastAggregatedUpdateTick, $"{p}.agg_tick");
                EqBool(ca.IsAggregated, cb.IsAggregated, $"{p}.is_aggregated");
            }
        }

        return diffs;
    }

    private static void CompareIntSets(
        IReadOnlyCollection<int> left,
        IReadOnlyCollection<int> right,
        string name,
        List<string> diffs)
    {
        var l = left.OrderBy(x => x).ToArray();
        var r = right.OrderBy(x => x).ToArray();
        if (!l.SequenceEqual(r))
            diffs.Add($"{name}: [{string.Join(",", l)}] vs [{string.Join(",", r)}]");
    }

    private static void CompareFirstCycleState(WorldSnapshot left, WorldSnapshot right, List<string> diffs)
    {
        void Eq(long a, long b, string name)
        {
            if (a != b)
                diffs.Add($"interventions.{name}: {a} vs {b}");
        }

        void EqF(float a, float b, string name)
        {
            if (BitConverter.SingleToUInt32Bits(a) != BitConverter.SingleToUInt32Bits(b))
                diffs.Add($"interventions.{name}: {Format(a)} vs {Format(b)}");
        }

        Eq(left.Resonance, right.Resonance, "resonance");
        Eq(left.ResonanceRegenTick, right.ResonanceRegenTick, "regen_tick");
        Eq(left.TotalResonanceSpent, right.TotalResonanceSpent, "spent");
        Eq(left.SuccessfulInterventions, right.SuccessfulInterventions, "successful");
        Eq(left.FailedInterventions, right.FailedInterventions, "failed");
        Eq(left.ShoutsMade, right.ShoutsMade, "shouts_made");
        Eq(left.ShoutsHeard, right.ShoutsHeard, "shouts_heard");
        Eq(left.FoodShouts, right.FoodShouts, "food_shouts");
        Eq(left.DangerShouts, right.DangerShouts, "danger_shouts");
        Eq(left.RallyShouts, right.RallyShouts, "rally_shouts");
        Eq(left.ShoutLearningEvents, right.ShoutLearningEvents, "shout_learning_events");
        Eq(left.SuccessfulShoutLessons, right.SuccessfulShoutLessons, "successful_shout_lessons");
        Eq(left.FailedShoutLessons, right.FailedShoutLessons, "failed_shout_lessons");
        Eq(left.FoodCrisisCount, right.FoodCrisisCount, "crises");
        Eq(left.FoodCrisisRecoveredCount, right.FoodCrisisRecoveredCount, "recoveries");
        Eq(left.ScoreLastTick, right.ScoreLastTick, "score_tick");
        EqF(left.ScorePopulationComponent, right.ScorePopulationComponent, "score_population");
        EqF(left.ScoreFoodComponent, right.ScoreFoodComponent, "score_food");
        EqF(left.ScoreCrisisComponent, right.ScoreCrisisComponent, "score_crisis");
        EqF(left.ScoreSettlementComponent, right.ScoreSettlementComponent, "score_settlement");
        EqF(left.ScoreEfficiencyComponent, right.ScoreEfficiencyComponent, "score_efficiency");
        EqF(left.ScoreTotal, right.ScoreTotal, "score_total");

        var pendingLeft = left.PendingInterventions.OrderBy(command => command.RequestedTick).ThenBy(command => command.Cell.Y).ThenBy(command => command.Cell.X).ToList();
        var pendingRight = right.PendingInterventions.OrderBy(command => command.RequestedTick).ThenBy(command => command.Cell.Y).ThenBy(command => command.Cell.X).ToList();
        Eq(pendingLeft.Count, pendingRight.Count, "pending_count");
        for (var i = 0; i < Math.Min(pendingLeft.Count, pendingRight.Count); i++)
        {
            var a = pendingLeft[i];
            var b = pendingRight[i];
            if (a.Type != b.Type || a.Cost != b.Cost || a.RequestedTick != b.RequestedTick || a.Cell.X != b.Cell.X || a.Cell.Y != b.Cell.Y)
                diffs.Add($"interventions.pending[{i}] differs");
        }

        var bloomsLeft = left.ActiveBlooms.OrderBy(effect => effect.Cell.Y).ThenBy(effect => effect.Cell.X).ToList();
        var bloomsRight = right.ActiveBlooms.OrderBy(effect => effect.Cell.Y).ThenBy(effect => effect.Cell.X).ToList();
        Eq(bloomsLeft.Count, bloomsRight.Count, "bloom_count");
        for (var i = 0; i < Math.Min(bloomsLeft.Count, bloomsRight.Count); i++)
        {
            var a = bloomsLeft[i];
            var b = bloomsRight[i];
            if (a.Cell.X != b.Cell.X || a.Cell.Y != b.Cell.Y || a.RemainingTicks != b.RemainingTicks ||
                a.BoostAmount != b.BoostAmount || a.RemainingBoost != b.RemainingBoost || a.CreatedSource != b.CreatedSource)
                diffs.Add($"interventions.bloom[{i}] differs");
        }

        var beaconsLeft = left.ActiveBeacons.OrderBy(effect => effect.Cell.Y).ThenBy(effect => effect.Cell.X).ToList();
        var beaconsRight = right.ActiveBeacons.OrderBy(effect => effect.Cell.Y).ThenBy(effect => effect.Cell.X).ToList();
        Eq(beaconsLeft.Count, beaconsRight.Count, "beacon_count");
        for (var i = 0; i < Math.Min(beaconsLeft.Count, beaconsRight.Count); i++)
        {
            var a = beaconsLeft[i];
            var b = beaconsRight[i];
            if (a.Cell.X != b.Cell.X || a.Cell.Y != b.Cell.Y || a.RemainingTicks != b.RemainingTicks ||
                BitConverter.SingleToUInt32Bits(a.Strength) != BitConverter.SingleToUInt32Bits(b.Strength))
                diffs.Add($"interventions.beacon[{i}] differs");
        }

        Eq(left.InterventionLog.Count, right.InterventionLog.Count, "log_count");
        for (var i = 0; i < Math.Min(left.InterventionLog.Count, right.InterventionLog.Count); i++)
        {
            var a = left.InterventionLog[i];
            var b = right.InterventionLog[i];
            if (a.Tick != b.Tick || a.Type != b.Type || a.Cell.X != b.Cell.X || a.Cell.Y != b.Cell.Y ||
                a.Cost != b.Cost || a.Success != b.Success || a.Reason != b.Reason)
                diffs.Add($"interventions.log[{i}] differs");
        }
    }

    private static void CompareLastGoals(EmergentSimulationWorld a, EmergentSimulationWorld b, List<string> diffs)
    {
        var goalsA = a.Factions.ToDictionary(f => f.Id, f => (byte)f.Goal);
        var goalsB = b.Factions.ToDictionary(f => f.Id, f => (byte)f.Goal);
        foreach (var key in goalsA.Keys.OrderBy(k => k))
        {
            if (!goalsB.TryGetValue(key, out var goalB) || goalsA[key] != goalB)
                diffs.Add($"current_faction_goal[{key}]: {goalsA[key]} vs {(goalsB.TryGetValue(key, out var g) ? g.ToString() : "missing")}");
        }
    }

    // ------------------------------------------------------------------
    // Canonical fingerprints (section-hashed, order-normalized)
    // ------------------------------------------------------------------

    private static Dictionary<string, string> ComputeSectionHashes(EmergentSimulationWorld world)
    {
        var result = new Dictionary<string, string>(SectionNames.Length);
        foreach (var section in SectionNames)
            result[section] = HashText(BuildSection(world, section));
        return result;
    }

    private static string HashText(string text)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash)[..16];
    }

    private static List<string> DiffHashes(
        Dictionary<string, string> left,
        Dictionary<string, string> right,
        EmergentSimulationWorld leftWorld,
        EmergentSimulationWorld rightWorld)
    {
        var details = new List<string>();
        foreach (var section in SectionNames)
        {
            var hashL = left.GetValueOrDefault(section, "<missing>");
            var hashR = right.GetValueOrDefault(section, "<missing>");
            if (hashL == hashR)
                continue;
            details.Add($"section {section}: {hashL} vs {hashR}");
            var linesL = BuildSection(leftWorld, section).Split('\n');
            var linesR = BuildSection(rightWorld, section).Split('\n');
            for (var i = 0; i < Math.Max(linesL.Length, linesR.Length); i++)
            {
                var lineL = i < linesL.Length ? linesL[i].TrimEnd() : "<missing>";
                var lineR = i < linesR.Length ? linesR[i].TrimEnd() : "<missing>";
                if (lineL != lineR)
                {
                    details.Add($"  first difference in {section}[{i}]:");
                    details.Add($"    A: {Truncate(lineL)}");
                    details.Add($"    B: {Truncate(lineR)}");
                    break;
                }
            }
        }
        return details;
    }

    private static string Truncate(string value) =>
        value.Length <= 150 ? value : value[..150] + "...";

    private static string BuildSection(EmergentSimulationWorld w, string section)
    {
        var sb = new StringBuilder(4096);
        switch (section)
        {
            case "meta":
                sb.AppendLine(System.Text.Json.JsonSerializer.Serialize(w.Ecology));
                sb.AppendLine($"seed={w.Seed};tick={w.Tick};rng={w.RandomState};lod={w.LodEnabled}");
                sb.AppendLine($"pop={w.AlivePopulation};food={w.FoodStockpile};stored={w.StoredFoodUnits}");
                sb.AppendLine($"births={w.Births};deaths={w.Deaths};starved={w.StarvationDeaths}");
                sb.AppendLine($"consumed={w.FoodConsumed};gathered={w.FoodGathered};shared={w.FoodShared};knowledge={w.KnowledgeShared}");
                sb.AppendLine($"surges={w.ResourceSurges};scarcity={w.ScarcityEvents};migrations={w.MigrationWaves}");
                sb.AppendLine($"event={w.CurrentEvent};event_ticks={w.EventTicksRemaining}");
                sb.AppendLine($"walls_built={w.WallBlocksBuilt};walls_removed={w.WallBlocksRemoved}");
                sb.AppendLine($"lod_counts={w.ActiveAgentCount}/{w.DormantAgentCount}/{w.AggregatedAgentCount}");
                sb.AppendLine($"settlements={w.SettlementCount};next_settlement={w.NextSettlementId};chronicle={w.ChronicleCount}");
                break;

            case "map":
                sb.Append(Convert.ToBase64String(w.Map.ExportCells()));
                break;

            case "walls":
                foreach (var wall in w.Map.WallCells.OrderBy(p => p.Y * EmergentSimulationWorld.Width + p.X))
                    sb.Append($"{wall.X},{wall.Y};");
                break;

            case "resources":
                foreach (var node in w.Food.OrderBy(n => n.Cell.Y * EmergentSimulationWorld.Width + n.Cell.X))
                {
                    var reserved = string.Join(",", node.ReservedBy.OrderBy(id => id));
                    sb.AppendLine($"{node.Cell.X},{node.Cell.Y}:{node.Amount}:{reserved}");
                }
                sb.AppendLine("#storage");
                foreach (var pair in w.FoodStorage.OrderBy(kv => kv.Key.Y * EmergentSimulationWorld.Width + kv.Key.X))
                    sb.AppendLine($"{pair.Key.X},{pair.Key.Y}={pair.Value}");
                break;

            case "agents":
                foreach (var a in w.Agents.OrderBy(x => x.Id))
                {
                    sb.AppendLine(string.Join(";",
                        a.Id, a.FactionId, a.Cell.X, a.Cell.Y, a.Facing.X, a.Facing.Y,
                        a.TargetCell.X, a.TargetCell.Y, a.FoodTargetCell.X, a.FoodTargetCell.Y, a.HasFoodTarget,
                        string.Join("|", a.Path.Select(p => $"{p.X},{p.Y}")), a.PathIndex,
                        Format(a.Energy), Format(a.Age), a.CarriedFood,
                        Format(a.MoveCooldown), Format(a.RestTimer), Format(a.BuildCooldown),
                        Format(a.FeedingCooldown), Format(a.ExplorationCooldown),
                        (int)a.Role, Format(a.RoleExperience),
                        a.HomeWallCell.X, a.HomeWallCell.Y, a.HasHomeWall,
                        a.KnownFoodCell.X, a.KnownFoodCell.Y, a.HasKnownFood, Format(a.FoodKnowledge),
                        a.SuccessfulFoodTrips, a.FailedFoodTrips, a.FoodEaten, a.ExplorationTrips, a.SharedMemories,
                        a.KnownDangerCell.X, a.KnownDangerCell.Y, a.HasDangerMemory,
                        Format(a.DangerKnowledge), Format(a.RouteKnowledge),
                        Format(a.BuildDrive), Format(a.ExplorationDrive), Format(a.RiskTolerance),
                        Format(a.LearningRate), Format(a.Intelligence), Format(a.PlanningSkill), Format(a.SocialAwareness),
                        a.PreferredBuildDirection,
                        Format(a.FoodUtilityBias), Format(a.BuildUtilityBias), Format(a.ExploreUtilityBias), Format(a.RestUtilityBias),
                        (int)a.LastDecisionAction, Format(a.LastDecisionScore),
                        a.DecisionsMade, a.LearningUpdates, a.PositiveOutcomes, a.NegativeOutcomes,
                        (int)a.Action, a.Alive,
                        a.Generation, a.ParentId1, a.ParentId2, Format(a.MaxAge), a.IsElder,
                        Format(a.ElderWisdomBonus), a.SettlementId));
                }
                break;

            case "factions":
                foreach (var f in w.Factions.OrderBy(x => x.Id))
                {
                    sb.AppendLine(string.Join(";",
                        f.Id, (int)f.Goal, f.Population, Format(f.AverageEnergy),
                        Format(f.FoodFocus), Format(f.BuildFocus), Format(f.ExploreFocus),
                        Format(f.Cohesion), Format(f.TerritoryPressure),
                        f.FoodStored, f.FoodConsumed, f.KnowledgeShared, f.Births, f.Deaths));
                }
                break;

            case "settlements":
                foreach (var s in w.Settlements.Values.OrderBy(x => x.Id))
                {
                    sb.AppendLine(string.Join(";",
                        s.Id, s.FactionId, s.CenterCell.X, s.CenterCell.Y, s.Population, s.WallCount,
                        s.FoodStored, Format(s.AverageEnergy), s.FoundedTick, s.LastActiveTick,
                        s.Generation, s.ElderCount, Format(s.Cohesion), s.Births, s.Deaths,
                        Format(s.TerritoryPressure), s.IsAbandoned));
                }
                sb.AppendLine("#milestones");
                foreach (var m in w.RecordedSettlementMilestones.OrderBy(x => x))
                    sb.Append(m).Append(',');
                break;

            case "chunks":
                for (var cy = 0; cy < w.Chunks.ChunkHeight; cy++)
                {
                    for (var cx = 0; cx < w.Chunks.ChunkWidth; cx++)
                    {
                        var c = w.Chunks.GetChunk(cx, cy);
                        sb.AppendLine(string.Join(";",
                            cx, cy, c.AgentCount, c.FoodCount, c.WallCount,
                            Format(c.ActivityLevel), c.LastUpdateTick,
                            c.Faction0Count, c.Faction1Count, Format(c.BirthRate), Format(c.DeathRate),
                            Format(c.MigrationPressure), c.SettlementId, Format(c.AvgEnergy),
                            c.TotalFoodConsumed, c.TotalFoodStored,
                            c.AggregatedPopulation, Format(c.AggregatedAvgEnergy), c.AggregatedFoodStockpile,
                            c.AggregatedFactionGoal, Format(c.AggregatedCohesion),
                            c.LastAggregatedUpdateTick, c.IsAggregated));
                    }
                }
                break;

            case "chronicle":
                foreach (var e in w.Chronicle)
                    sb.AppendLine($"{e.Tick};{(int)e.Type};{e.FactionId};{e.HasCell};{e.Cell.X};{e.Cell.Y};{(int)e.Importance};{e.Description}");
                sb.AppendLine("#once");
                foreach (var t in w.FiredOnceEvents.Select(t => (int)t).OrderBy(t => t))
                    sb.Append(t).Append(',');
                sb.AppendLine();
                sb.AppendLine("#generations");
                foreach (var g in GetRecordedGenerations(w).OrderBy(g => g))
                    sb.Append(g).Append(',');
                sb.AppendLine();
                sb.AppendLine("#cooldowns");
                foreach (var kv in w.EventCooldowns.OrderBy(kv => (int)kv.Key))
                    sb.Append($"{(int)kv.Key}:{kv.Value};");
                sb.AppendLine();
                sb.AppendLine("#clusters");
                foreach (var c in w.KnownClusterChunks.OrderBy(c => c))
                    sb.Append(c).Append(',');
                break;

            case "interventions":
                var snapshot = w.CreateSnapshot();
                sb.AppendLine($"resonance={snapshot.Resonance};regen={snapshot.ResonanceRegenTick};spent={snapshot.TotalResonanceSpent};success={snapshot.SuccessfulInterventions};failed={snapshot.FailedInterventions}");
                sb.AppendLine($"score={Format(snapshot.ScorePopulationComponent)};{Format(snapshot.ScoreFoodComponent)};{Format(snapshot.ScoreCrisisComponent)};{Format(snapshot.ScoreSettlementComponent)};{Format(snapshot.ScoreEfficiencyComponent)};{Format(snapshot.ScoreTotal)};tick={snapshot.ScoreLastTick}");
                sb.AppendLine($"crises={snapshot.FoodCrisisCount};recoveries={snapshot.FoodCrisisRecoveredCount}");
                foreach (var command in snapshot.PendingInterventions.OrderBy(command => command.RequestedTick).ThenBy(command => command.Cell.Y).ThenBy(command => command.Cell.X))
                    sb.AppendLine($"pending={command.RequestedTick};{command.Type};{command.Cell.X},{command.Cell.Y};{command.Cost}");
                foreach (var bloom in snapshot.ActiveBlooms.OrderBy(effect => effect.Cell.Y).ThenBy(effect => effect.Cell.X))
                    sb.AppendLine($"bloom={bloom.Cell.X},{bloom.Cell.Y};{bloom.RemainingTicks};{bloom.BoostAmount};{bloom.RemainingBoost};{bloom.CreatedSource}");
                foreach (var beacon in snapshot.ActiveBeacons.OrderBy(effect => effect.Cell.Y).ThenBy(effect => effect.Cell.X))
                    sb.AppendLine($"beacon={beacon.Cell.X},{beacon.Cell.Y};{beacon.RemainingTicks};{Format(beacon.Strength)}");
                foreach (var entry in snapshot.InterventionLog)
                    sb.AppendLine($"log={entry.Tick};{entry.Type};{entry.Cell.X},{entry.Cell.Y};{entry.Cost};{entry.Success};{entry.Reason}");
                break;
        }

        return sb.ToString();
    }

    private static string Format(float value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------
    // Small helpers
    // ------------------------------------------------------------------

    private static readonly FieldInfo OnceEventsField =
        typeof(EmergentSimulationWorld).GetField("_onceEvents", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("_onceEvents field not found");

    private static readonly FieldInfo RecordedGenerationsField =
        typeof(EmergentSimulationWorld).GetField("_recordedGenerations", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("_recordedGenerations field not found");

    private static readonly MethodInfo RecordEventMethod =
        typeof(EmergentSimulationWorld).GetMethod("RecordEvent", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("RecordEvent method not found");

    private static HashSet<WorldEventType> GetOnceEvents(EmergentSimulationWorld world) =>
        (HashSet<WorldEventType>?)OnceEventsField.GetValue(world)
        ?? throw new InvalidOperationException("_onceEvents is null");

    private static HashSet<int> GetRecordedGenerations(EmergentSimulationWorld world) =>
        (HashSet<int>?)RecordedGenerationsField.GetValue(world)
        ?? throw new InvalidOperationException("_recordedGenerations is null");

    /// <summary>
    /// Injects a chronicle event through the engine's own recording path
    /// (ring-buffer trim included). Used to deterministically overflow the
    /// 48-slot buffer without relying on long RNG-driven runs.
    /// </summary>
    private static void RecordSyntheticEvent(EmergentSimulationWorld world, string description) =>
        RecordEventMethod.Invoke(world, new object?[]
        {
            WorldEventType.SourceDepleted, description, WorldEventImportance.Minor, -1, null,
        });

    private static string TempSavePath() =>
        Path.Combine(Path.GetTempPath(), $"hollowbound-selftest-{Guid.NewGuid():N}.json");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort cleanup of our own temp file only.
        }
    }

    private static string Formatted(string summary, List<string>? notes)
    {
        if (notes == null || notes.Count == 0)
            return summary;
        return summary + " [" + string.Join("; ", notes) + "]";
    }
}
