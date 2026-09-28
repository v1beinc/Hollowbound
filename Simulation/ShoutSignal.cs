using Microsoft.Xna.Framework;

namespace Hollowbound.Simulation;

public enum ShoutType : byte
{
    Food = 0,
    Danger = 1,
    Rally = 2,
}

/// <summary>
/// A short-lived local communication pulse. It is intentionally a small
/// value-object-like class: propagation is immediate, while the instance is
/// retained briefly only for visualization and inspection.
/// </summary>
public sealed class ShoutSignal
{
    public int SenderId { get; init; }
    public int FactionId { get; init; }
    public Point Cell { get; init; }
    public ShoutType Type { get; init; }
    public float Strength { get; init; }
    public int Radius { get; init; }
    public long CreatedTick { get; init; }
    public long ExpiresTick { get; init; }
}
