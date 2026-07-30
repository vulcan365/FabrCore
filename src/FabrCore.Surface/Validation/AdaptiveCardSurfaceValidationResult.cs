namespace FabrCore.Surface.Validation;

public sealed class AdaptiveCardSurfaceValidationResult
{
    public static AdaptiveCardSurfaceValidationResult Valid { get; } = new(true, []);

    public AdaptiveCardSurfaceValidationResult(
        bool isValid,
        IReadOnlyList<string> errors,
        int plannedActionCount = 0,
        int validatedActionCount = 0,
        IReadOnlyList<string>? rejectedActionReasons = null)
    {
        IsValid = isValid;
        Errors = errors;
        PlannedActionCount = plannedActionCount;
        ValidatedActionCount = validatedActionCount;
        RejectedActionReasons = rejectedActionReasons ?? [];
    }

    public bool IsValid { get; }

    public IReadOnlyList<string> Errors { get; }

    public int PlannedActionCount { get; }

    public int ValidatedActionCount { get; }

    public IReadOnlyList<string> RejectedActionReasons { get; }

    public int RejectedActionCount => RejectedActionReasons.Count;
}
