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
    public long BirthAttempts { get; set; }
    public long BirthBlockedParents { get; set; }
    public long BirthBlockedFood { get; set; }
    public long BirthBlockedSpace { get; set; }
    public long FoodSpentOnBirths { get; set; }
    public long FoodSpoiled { get; set; }
    public long DroughtFoodLost { get; set; }
    public int DroughtsCompleted { get; set; }
    public ColonyEcologyState Copy() => (ColonyEcologyState)MemberwiseClone();
    public ColonyEcologyState ValidatedCopy()
    {
        if (Phase is < 0 or > 2 || Radius is < 1 or > 40 ||
            CenterX is < 0 or >= EmergentSimulationWorld.Width || CenterY is < 0 or >= EmergentSimulationWorld.Height ||
            NextDroughtTick < 0 || PhaseEndsTick < 0 || BirthAttempts < 0 || BirthBlockedParents < 0 ||
            BirthBlockedFood < 0 || BirthBlockedSpace < 0 || FoodSpentOnBirths < 0 || FoodSpoiled < 0 ||
            DroughtFoodLost < 0 || DroughtsCompleted < 0)
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
        _ => "Источники восстанавливаются"
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
        {
            var colony = _settlements.Values.Where(s => !s.IsAbandoned && s.Population > 0)
                .OrderByDescending(s => s.Population).ThenBy(s => s.Id).FirstOrDefault();
            var anchor = colony?.CenterCell ?? _agents.First(a => a.Alive).Cell;
            Ecology.CenterX = anchor.X;
            Ecology.CenterY = anchor.Y;
            Ecology.Phase = 1;
            Ecology.PhaseEndsTick = Tick + 800;
            RecordEvent(WorldEventType.DroughtWarning, "Источники в районе истощаются. Подготовьте запасы или новый маршрут: 800 тиков.", WorldEventImportance.Critical, colony?.FactionId ?? -1, anchor);
        }
        else if (Ecology.Phase == 1 && Tick >= Ecology.PhaseEndsTick)
        {
            Ecology.Phase = 2;
            Ecology.PhaseEndsTick = Tick + 1800;
            RecordEvent(WorldEventType.DroughtStarted, "Локальное истощение: природная пища убывает. Цветение игрока защищено.", WorldEventImportance.Critical, -1, DroughtCenter);
        }
        else if (Ecology.Phase == 2 && Tick >= Ecology.PhaseEndsTick)
        {
            Ecology.Phase = 0;
            Ecology.NextDroughtTick = Tick + 4500;
            Ecology.DroughtsCompleted++;
            RecordEvent(WorldEventType.DroughtEnded, "Район снова пригоден для восстановления источников.", WorldEventImportance.Major, -1, DroughtCenter);
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
}
