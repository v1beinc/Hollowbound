using Microsoft.Xna.Framework;

namespace Hollowbound.Simulation;

// Explicit save state: no wall-clock timers, no render-dependent simulation.
public sealed class ColonyEcologyState
{
    public long NextDroughtTick { get; set; } = 3000;
    public long PhaseEndsTick { get; set; }
    public int Phase { get; set; } // 0 quiet, 1 warning, 2 drought
    public int CenterX { get; set; } = 64;
    public int CenterY { get; set; } = 40;
    public int Radius { get; set; } = 18;
    public int DroughtFactionId { get; set; } = -1;
    public long BirthAttempts { get; set; }
    public long BirthBlockedParents { get; set; }
    public long BirthBlockedFood { get; set; }
    public long BirthBlockedSpace { get; set; }
    public long FoodSpentOnBirths { get; set; }
    public long FoodSpoiled { get; set; }
    public long DroughtFoodLost { get; set; }
    public int DroughtsCompleted { get; set; }
    public long DroughtChecks { get; set; }
    public float LastDroughtProbability { get; set; }
    public int LastDroughtFoodAtRisk { get; set; }
    public int LastDroughtLocalPopulation { get; set; }
    public ColonyEcologyState Copy() => (ColonyEcologyState)MemberwiseClone();
    public ColonyEcologyState ValidatedCopy()
    {
        if (Phase is < 0 or > 2 || Radius is < 1 or > 40 || DroughtFactionId < -1 ||
            CenterX is < 0 or >= EmergentSimulationWorld.Width || CenterY is < 0 or >= EmergentSimulationWorld.Height ||
            NextDroughtTick < 0 || PhaseEndsTick < 0 || BirthAttempts < 0 || BirthBlockedParents < 0 ||
            BirthBlockedFood < 0 || BirthBlockedSpace < 0 || FoodSpentOnBirths < 0 || FoodSpoiled < 0 ||
            DroughtFoodLost < 0 || DroughtsCompleted < 0 || DroughtChecks < 0 ||
            !float.IsFinite(LastDroughtProbability) || LastDroughtProbability is < 0 or > 1 ||
            LastDroughtFoodAtRisk < 0 || LastDroughtLocalPopulation < 0)
            throw new InvalidDataException("Invalid colony ecology state.");
        return Copy();
    }
}

public sealed partial class EmergentSimulationWorld
{
    private static readonly Point[] FoodNeighbours = { new(0, -1), new(-1, 0), new(1, 0), new(0, 1) };
    public ColonyEcologyState Ecology { get; private set; } = new();
    public Point DroughtCenter => new(Ecology.CenterX, Ecology.CenterY);
    public string EcologyStatus => Ecology.Phase switch
    {
        1 => $"Истощение через {Math.Max(0, Ecology.PhaseEndsTick - Tick)} тиков",
        2 => $"Истощение: ещё {Math.Max(0, Ecology.PhaseEndsTick - Tick)} тиков",
        _ => Ecology.DroughtChecks == 0
            ? "Тихий период · экосистема наблюдает за колониями"
            : $"Тихий период · риск засухи {Ecology.LastDroughtProbability:P0} · пища рядом {Ecology.LastDroughtFoodAtRisk}"
    };
    public bool IsDroughtCell(Point cell) => Ecology.Phase == 2 &&
        Math.Abs(cell.X - Ecology.CenterX) + Math.Abs(cell.Y - Ecology.CenterY) <= Ecology.Radius;

    private void Nourish(AgentState agent)
    {
        agent.FoodEaten++;
        FoodConsumed++;
        EnsureFaction(agent.FactionId).FoodConsumed++;
        agent.FeedingCooldown = FeedingInterval;
        agent.Energy = Math.Min(100, agent.Energy + FoodEnergyValue + agent.Intelligence * 3);
    }

    // Bounded flood fill: storage on the other side of a solid wall is not food access.
    private List<Point> ReachableStorage(Point origin, int radius)
    {
        var result = new List<Point>();
        var seen = new HashSet<Point> { origin };
        var queue = new Queue<(Point Cell, int Distance)>();
        queue.Enqueue((origin, 0));
        while (queue.TryDequeue(out var node))
        {
            if (_foodStorage.GetValueOrDefault(node.Cell) > 0) result.Add(node.Cell);
            if (node.Distance >= radius) continue;
            foreach (var direction in FoodNeighbours)
            {
                var next = node.Cell + direction;
                if (_map.InBounds(next) && _map.IsWalkable(next) && seen.Add(next)) queue.Enqueue((next, node.Distance + 1));
            }
        }
        return result;
    }

    private bool SpendLocalBirthFood(Point home, int population)
    {
        var piles = ReachableStorage(home, 12);
        var amount = piles.Sum(p => _foodStorage[p]);
        // Keep a small local feeding reserve after paying for the offspring.
        if (amount < 6 + Math.Max(2, population / 2)) return false;
        var remaining = 6;
        foreach (var p in piles)
        {
            var spent = Math.Min(remaining, _foodStorage[p]);
            _foodStorage[p] -= spent;
            if (_foodStorage[p] == 0) _foodStorage.Remove(p);
            remaining -= spent;
            if (remaining == 0) break;
        }
        FoodStockpile = Math.Max(0, FoodStockpile - 6);
        Ecology.FoodSpentOnBirths += 6;
        return true;
    }

    private void UpdateColonyEcology()
    {
        if (Ecology.Phase == 0 && Tick >= Ecology.NextDroughtTick && AlivePopulation >= 8)
            EvaluateDroughtRisk();
        else if (Ecology.Phase == 1 && Tick >= Ecology.PhaseEndsTick)
        {
            Ecology.Phase = 2;
            Ecology.PhaseEndsTick = Tick + 1800;
            RecordEvent(WorldEventType.DroughtStarted, "Локальное истощение: природная пища убывает. Цветение игрока защищено.", WorldEventImportance.Critical, Ecology.DroughtFactionId, DroughtCenter);
        }
        else if (Ecology.Phase == 2 && Tick >= Ecology.PhaseEndsTick)
        {
            var droughtFactionId = Ecology.DroughtFactionId;
            Ecology.Phase = 0;
            Ecology.NextDroughtTick = Tick + 4500;
            Ecology.DroughtsCompleted++;
            Ecology.DroughtFactionId = -1;
            RecordEvent(WorldEventType.DroughtEnded, "Район снова пригоден для восстановления источников.", WorldEventImportance.Major, droughtFactionId, DroughtCenter);
        }
        if (Tick % 50 == 0 && Ecology.Phase == 2)
        {
            foreach (var node in _food)
            {
                if (node.Amount <= 0 || !IsDroughtCell(node.Cell) || _activeBlooms.ContainsKey(node.Cell)) continue;
                if ((node.Cell.X + node.Cell.Y + Tick / 50) % 4 != 0) continue;
                node.Amount--;
                Ecology.DroughtFoodLost++;
                if (node.Amount == 0) _chunks.UnregisterFood(node.Cell);
            }
        }
        // Large unconsumed stockpiles slowly spoil. Existing saves are not reset.
        if (Tick % 500 == 0)
        {
            foreach (var cell in _foodStorage.Keys.OrderBy(p => p.Y).ThenBy(p => p.X).ToArray())
            {
                var amount = _foodStorage[cell];
                if (amount <= 8) continue;
                var loss = Math.Max(1, amount / 100);
                _foodStorage[cell] -= loss;
                FoodStockpile = Math.Max(0, FoodStockpile - loss);
                Ecology.FoodSpoiled += loss;
            }
        }
    }

    private void EvaluateDroughtRisk()
    {
        const int evaluationInterval = 500;
        const int minimumFoodAtRisk = 8;
        Ecology.DroughtChecks++;
        Ecology.NextDroughtTick = Tick + evaluationInterval;

        Point selectedCenter = default;
        int selectedFaction = -1;
        int selectedPopulation = 0;
        int selectedFood = 0;
        int selectedStoredFood = 0;
        var bestCandidateScore = float.NegativeInfinity;
        const int maxCandidatesPerCheck = 8;
        var candidates = _settlements.Values
            .Where(s => !s.IsAbandoned && s.Population >= 5)
            .OrderBy(s => s.Id)
            .ToArray();
        var firstCandidate = candidates.Length == 0
            ? 0
            : (int)(((Tick / evaluationInterval) * maxCandidatesPerCheck) % candidates.Length);

        // Limit candidate scans: drought checks are infrequent, but settlement
        // counts can be large in long-running worlds. Rotate a stable ID-sorted
        // window so small colonies are eventually considered too.
        for (var candidateOffset = 0; candidateOffset < Math.Min(maxCandidatesPerCheck, candidates.Length); candidateOffset++)
        {
            var settlement = candidates[(firstCandidate + candidateOffset) % candidates.Length];
            var center = settlement.CenterCell;
            if (!_map.IsWalkable(center))
                continue;

            // A rich stockpile behind an impassable wall must not make an
            // exposed colony look safe. The map is small and checks are rare,
            // so one reverse multi-source BFS per candidate is bounded.
            var reachableDistances = _pathFinder.ComputeDistanceToNearestTarget(new[] { center });
            var naturalFood = CountReachableFoodAround(center, Ecology.Radius, reachableDistances);
            if (naturalFood < minimumFoodAtRisk)
                continue;

            var storedFood = CountReachableStoredFoodAround(center, Ecology.Radius, reachableDistances);
            var candidateExpectedDemand = Math.Max(20f, settlement.Population * 3f);
            var candidateFoodPressure = Math.Clamp(1f - (naturalFood + storedFood) / candidateExpectedDemand, 0f, 1f);
            // Prefer a genuinely pressured colony, with population only as a
            // small deterministic tie-break. Do not target the richest base.
            var candidateScore = candidateFoodPressure * 100f + Math.Min(100, settlement.Population) * 0.01f;
            if (candidateScore <= bestCandidateScore)
                continue;

            bestCandidateScore = candidateScore;
            selectedCenter = center;
            selectedFaction = settlement.FactionId;
            selectedPopulation = settlement.Population;
            selectedFood = naturalFood;
            selectedStoredFood = storedFood;
        }

        // Early worlds may not have founded a settlement yet. Use the local
        // cluster around a living founder rather than inventing a map-wide crisis.
        if (candidates.Length == 0)
        {
            var founder = _agents.FirstOrDefault(agent => agent.Alive);
            if (founder is not null && _map.IsWalkable(founder.Cell))
            {
                selectedCenter = founder.Cell;
                selectedFaction = founder.FactionId;
                selectedPopulation = _agents.Count(agent => agent.Alive &&
                    Math.Abs(agent.Cell.X - selectedCenter.X) + Math.Abs(agent.Cell.Y - selectedCenter.Y) <= Ecology.Radius);
                var reachableDistances = _pathFinder.ComputeDistanceToNearestTarget(new[] { selectedCenter });
                selectedFood = CountReachableFoodAround(selectedCenter, Ecology.Radius, reachableDistances);
                selectedStoredFood = CountReachableStoredFoodAround(selectedCenter, Ecology.Radius, reachableDistances);
            }
        }

        Ecology.LastDroughtFoodAtRisk = selectedFood;
        Ecology.LastDroughtLocalPopulation = selectedPopulation;
        if (selectedFood < minimumFoodAtRisk)
        {
            Ecology.LastDroughtProbability = 0;
            return;
        }

        var expectedDemand = Math.Max(20f, selectedPopulation * 3f);
        var foodPressure = Math.Clamp(1f - (selectedFood + selectedStoredFood) / expectedDemand, 0f, 1f);
        var colonyPressure = Math.Clamp((selectedPopulation - 8f) / 72f, 0f, 1f);
        var probability = Math.Clamp(0.01f + foodPressure * 0.10f + colonyPressure * 0.06f, 0.01f, 0.17f);
        Ecology.LastDroughtProbability = probability;
        if (_rng.NextDouble() >= probability)
            return;

        Ecology.CenterX = selectedCenter.X;
        Ecology.CenterY = selectedCenter.Y;
        Ecology.DroughtFactionId = selectedFaction;
        Ecology.Phase = 1;
        Ecology.PhaseEndsTick = Tick + 800;
        RecordEvent(WorldEventType.DroughtWarning,
            $"Над колонией сгущается засуха: шанс события {probability:P0}, под угрозой {selectedFood} еды. Есть 800 тиков на запасы или новый маршрут.",
            WorldEventImportance.Critical, selectedFaction, selectedCenter);
    }

    private int CountReachableFoodAround(Point center, int radius, int[] reachableDistances)
    {
        var total = 0;
        foreach (var node in _food)
        {
            if (node.Amount <= 0 || !IsReachableRiskCell(center, node.Cell, radius, reachableDistances))
                continue;
            total = (int)Math.Min(int.MaxValue, (long)total + node.Amount);
        }
        return total;
    }

    private int CountReachableStoredFoodAround(Point center, int radius, int[] reachableDistances)
    {
        var total = 0;
        foreach (var (cell, amount) in _foodStorage)
        {
            if (amount <= 0 || !IsReachableRiskCell(center, cell, radius, reachableDistances))
                continue;
            total = (int)Math.Min(int.MaxValue, (long)total + amount);
        }
        return total;
    }

    private bool IsReachableRiskCell(Point center, Point cell, int radius, int[] reachableDistances)
    {
        var manhattanDistance = Math.Abs(cell.X - center.X) + Math.Abs(cell.Y - center.Y);
        if (manhattanDistance > radius || !_map.InBounds(cell))
            return false;

        var pathDistance = reachableDistances[cell.Y * Width + cell.X];
        return pathDistance >= 0 && pathDistance <= radius * 2;
    }
}
