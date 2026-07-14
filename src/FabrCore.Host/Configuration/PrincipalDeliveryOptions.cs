namespace FabrCore.Host.Configuration;

/// <summary>Limits for provider metadata stored with a principal.</summary>
public sealed class PrincipalContextOptions
{
    public const string SectionName = "FabrCore:PrincipalContext";

    public int MaxEntries { get; set; } = 64;

    public int MaxKeyLength { get; set; } = 128;

    public int MaxValueBytes { get; set; } = 128 * 1024;

    public int MaxTotalBytes { get; set; } = 256 * 1024;
}

/// <summary>Durability and recovery settings for principal message delivery.</summary>
public sealed class PrincipalDeliveryOptions
{
    public const string SectionName = "FabrCore:PrincipalDelivery";

    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan RecoveryReminderPeriod { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan MaxDeliveryAge { get; set; } = TimeSpan.FromHours(24);

    public TimeSpan DeadLetterRetention { get; set; } = TimeSpan.FromDays(7);

    public int MaxDeadLetters { get; set; } = 100;
}
