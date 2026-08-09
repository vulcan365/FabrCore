namespace FabrCore.Sdk;

/// <summary>Controls mode selection when an inbound message omits <see cref="HarnessMessageArgs.PlanMode"/>.</summary>
public enum MissingPlanModeBehavior
{
    /// <summary>Select planning mode. This preserves FabrCore's original behavior.</summary>
    SelectPlanning,

    /// <summary>Leave the current session mode unchanged, honoring the configured default for a new session.</summary>
    PreserveCurrentMode,

    /// <summary>Select execution mode.</summary>
    SelectExecution
}
