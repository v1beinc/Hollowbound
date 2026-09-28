namespace Hollowbound.Simulation;

public enum OnboardingStep : byte
{
    MoveCamera,
    SelectFounder,
    StartTime,
    ObserveFood,
    UseIntervention,
    OpenChronicle,
    Complete,
}

public sealed class FirstCycleUXState
{
    private bool _cameraMoved;
    private bool _agentSelected;
    private bool _timeStarted;
    private bool _foodObserved;
    private bool _interventionUsed;
    private bool _chronicleOpened;

    public FirstCycleUXState(bool completed = false)
    {
        if (completed)
            CompleteAll();
    }

    public OnboardingStep CurrentStep =>
        !_cameraMoved ? OnboardingStep.MoveCamera :
        !_agentSelected ? OnboardingStep.SelectFounder :
        !_timeStarted ? OnboardingStep.StartTime :
        !_foodObserved ? OnboardingStep.ObserveFood :
        !_interventionUsed ? OnboardingStep.UseIntervention :
        !_chronicleOpened ? OnboardingStep.OpenChronicle :
        OnboardingStep.Complete;

    public bool IsComplete => CurrentStep == OnboardingStep.Complete;

    public string Instruction => CurrentStep switch
    {
        OnboardingStep.MoveCamera => "Move the camera with WASD, middle drag, or the mouse wheel.",
        OnboardingStep.SelectFounder => "Click one of the founders to inspect it.",
        OnboardingStep.StartTime => "Press Space to start the experiment.",
        OnboardingStep.ObserveFood => "Watch until an agent gathers its first food.",
        OnboardingStep.UseIntervention => "Choose Bloom, Beacon, or Insight and spend 1 Resonance.",
        OnboardingStep.OpenChronicle => "Click a Chronicle entry to visit the event.",
        _ => "First observation cycle complete. The world is now yours.",
    };

    public void MarkCameraMoved() => _cameraMoved = true;
    public void MarkAgentSelected() => _agentSelected = true;
    public void MarkTimeStarted() => _timeStarted = true;
    public void ObserveFood(bool hasGatheredFood) => _foodObserved |= hasGatheredFood;
    public void ObserveIntervention(bool hasUsedIntervention) => _interventionUsed |= hasUsedIntervention;
    public void MarkChronicleOpened() => _chronicleOpened = true;

    public void Reset()
    {
        _cameraMoved = false;
        _agentSelected = false;
        _timeStarted = false;
        _foodObserved = false;
        _interventionUsed = false;
        _chronicleOpened = false;
    }

    private void CompleteAll()
    {
        _cameraMoved = true;
        _agentSelected = true;
        _timeStarted = true;
        _foodObserved = true;
        _interventionUsed = true;
        _chronicleOpened = true;
    }
}

public static class AgentDecisionExplainer
{
    public static IReadOnlyList<string> Explain(AgentState agent)
    {
        var reasons = new List<string>(3);

        if (agent.Energy < 30f)
            reasons.Add($"Low energy ({agent.Energy:0}) raises food and rest priority.");
        if (agent.CarriedFood > 0)
            reasons.Add($"Carrying {agent.CarriedFood} food toward storage.");
        if (agent.HasKnownFood && agent.Action is AgentAction.SearchingFood or AgentAction.GoingToFood)
            reasons.Add("Using a remembered food location.");
        if (agent.HasDangerMemory && agent.DangerKnowledge > 0.2f)
            reasons.Add("Avoiding a remembered danger area.");

        reasons.Add(agent.Action switch
        {
            AgentAction.Building => $"Build drive {agent.BuildDrive:0.00} and {agent.Role} role support construction.",
            AgentAction.Exploring => $"Exploration drive {agent.ExplorationDrive:0.00} outweighs known local options.",
            AgentAction.Resting => $"Rest policy bias {agent.RestUtilityBias:0.00} currently wins.",
            AgentAction.CarryingFood or AgentAction.ReturningToWall or AgentAction.StoringFood => "Protecting gathered food by returning it to the colony.",
            AgentAction.GatheringFood or AgentAction.GoingToFood or AgentAction.SearchingFood => $"Food policy bias {agent.FoodUtilityBias:0.00} currently wins.",
            AgentAction.Migrating => "Local pressure makes migration more useful than staying.",
            _ => $"Best learned action score: {agent.LastDecisionScore:0.00}.",
        });

        if (agent.LearningUpdates > 0)
            reasons.Add($"Adjusted by {agent.LearningUpdates} past learning updates.");

        return reasons.Distinct().Take(3).ToArray();
    }
}
