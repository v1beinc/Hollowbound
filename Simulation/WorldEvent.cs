using Microsoft.Xna.Framework;

namespace Hollowbound.Simulation;

/// <summary>
/// Type of a chronicle event. Events are generated only by real world
/// changes (first wall, source depletion, faction goal switch, ...) and
/// guarded against spam by one-shot flags and tick cooldowns.
/// </summary>
public enum WorldEventType : byte
{
    FirstFoodGathered = 0,
    FirstWallBuilt = 1,
    FirstBirth = 2,
    FirstDeath = 3,
    SourceDepleted = 4,
    FactionGoalChanged = 5,
    MigrationStarted = 6,
    NewClusterDetected = 7,
    WallMilestone = 8,
    FoodCrisis = 9,
    FoodRecovered = 10,
    FirstElder = 11,
    ElderDeath = 12,
    SettlementFounded = 13,
    SettlementAbandoned = 14,
    SettlementMilestone = 15,
    PlayerIntervention = 16,
    DroughtWarning = 17,
    DroughtStarted = 18,
    DroughtEnded = 19,
}

public enum WorldEventImportance : byte
{
    Minor = 0,
    Major = 1,
    Critical = 2,
}

/// <summary>
/// A single significant world event. Compact by design: it is held in a
/// bounded ring buffer inside the world, serialized into snapshots, and
/// written to the JSONL log as its own record.
/// </summary>
public sealed class WorldEvent
{
    public long Tick { get; set; }
    public WorldEventType Type { get; set; }
    public int FactionId { get; set; } = -1;
    public Point Cell { get; set; }
    public bool HasCell { get; set; }
    public string Description { get; set; } = string.Empty;
    public WorldEventImportance Importance { get; set; }

    public WorldEvent()
    {
    }

    public WorldEvent(long tick, WorldEventType type, string description,
        WorldEventImportance importance, int factionId = -1, Point? cell = null)
    {
        Tick = tick;
        Type = type;
        Description = description;
        Importance = importance;
        FactionId = factionId;
        if (cell.HasValue)
        {
            Cell = cell.Value;
            HasCell = true;
        }
    }
}

/// <summary>
/// Serializable snapshot of a single chronicle event. Kept separate from
/// WorldEvent so the live model stays plain and the snapshot format stays
/// stable across versions.
/// </summary>
public sealed class WorldEventSnapshot
{
    public long Tick { get; set; }
    public byte Type { get; set; }
    public int FactionId { get; set; } = -1;
    public PointSnapshot Cell { get; set; } = new();
    public bool HasCell { get; set; }
    public string Description { get; set; } = string.Empty;
    public byte Importance { get; set; }

    public static WorldEventSnapshot From(WorldEvent worldEvent) => new()
    {
        Tick = worldEvent.Tick,
        Type = (byte)worldEvent.Type,
        FactionId = worldEvent.FactionId,
        Cell = PointSnapshot.From(worldEvent.Cell),
        HasCell = worldEvent.HasCell,
        Description = worldEvent.Description,
        Importance = (byte)worldEvent.Importance,
    };

    public WorldEvent ToWorldEvent() => new()
    {
        Tick = Math.Max(0, Tick),
        Type = (WorldEventType)Type,
        FactionId = FactionId,
        Cell = Cell.ToPoint(),
        HasCell = HasCell,
        Description = Description ?? string.Empty,
        Importance = (WorldEventImportance)Importance,
    };
}
