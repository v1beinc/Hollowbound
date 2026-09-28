namespace Hollowbound.Simulation;

/// <summary>
/// Small bounded memory of one sender. It is intentionally not a global
/// dictionary: each agent only remembers a few recent sources, which keeps the
/// representation viable for large populations.
/// </summary>
public sealed class ShoutReputation
{
    public int SenderId { get; init; }
    public float FoodTrust { get; set; } = 0.5f;
    public float DangerTrust { get; set; } = 0.5f;
    public float RallyTrust { get; set; } = 0.5f;
    public int HeardCount { get; set; }
    public int LearningEvents { get; set; }
    public long LastSeenTick { get; set; } = -1;
}
