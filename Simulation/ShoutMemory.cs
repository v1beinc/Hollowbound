using Microsoft.Xna.Framework;

namespace Hollowbound.Simulation;

/// <summary>
/// A bounded memory of one concrete signal and place. Unlike source
/// reputation, this remembers what was said and where, so several useful
/// locations can coexist in one agent's mind.
/// </summary>
public sealed class ShoutMemory
{
    public int SenderId { get; init; } = -1;
    public int FactionId { get; init; } = -1;
    public Point Cell { get; init; }
    public ShoutType Type { get; init; }
    public float Confidence { get; set; } = 0.5f;
    public float Strength { get; set; }
    public int SuccessfulOutcomes { get; set; }
    public int FailedOutcomes { get; set; }
    public long HeardTick { get; set; } = -1;
    public long LastOutcomeTick { get; set; } = -1;
}
