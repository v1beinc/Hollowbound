using Microsoft.Xna.Framework;

namespace Hollowbound.Simulation;

public sealed class AgentState
{
    public int Id { get; init; }
    public int FactionId { get; set; }
    public Vector2 Position { get; set; }
    public Vector2 Target { get; set; }
    public float Energy { get; set; } = 100f;
    public float Age { get; set; }
    public int CarriedFood { get; set; }
    public float MoveCooldown { get; set; }
    public string Action { get; set; } = "idle";
    public bool Alive { get; set; } = true;
}
