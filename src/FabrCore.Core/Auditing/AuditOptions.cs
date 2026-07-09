namespace FabrCore.Core.Auditing
{
    /// <summary>Controls which audit events are recorded for a category.</summary>
    public enum AuditLevel
    {
        /// <summary>Record nothing.</summary>
        None,

        /// <summary>Record denials and errors only.</summary>
        Failures,

        /// <summary>Record everything, including successful/allowed operations.</summary>
        All
    }

    /// <summary>
    /// Options controlling security audit capture. Bound from the <c>FabrCore:Audit</c>
    /// configuration section and exposed through <see cref="IAuditProvider.Options"/>.
    /// Implementations of <see cref="IAuditProvider"/> apply <see cref="ShouldRecord"/>
    /// filtering — emit sites always call <see cref="IAuditProvider.RecordAsync"/>.
    /// </summary>
    public class AuditOptions
    {
        public const string SectionName = "FabrCore:Audit";

        /// <summary>
        /// Audit level for categories without an explicit override in <see cref="Categories"/>.
        /// Defaults to <see cref="AuditLevel.Failures"/> so per-message allow decisions stay quiet.
        /// </summary>
        public AuditLevel DefaultLevel { get; set; } = AuditLevel.Failures;

        /// <summary>
        /// Per-category level overrides. Management changes, boundary crossings, and bootstrap
        /// events default to <see cref="AuditLevel.All"/> — they are rare and always significant.
        /// </summary>
        public Dictionary<AuditCategory, AuditLevel> Categories { get; set; } = new()
        {
            [AuditCategory.AclManagement] = AuditLevel.All,
            [AuditCategory.BoundaryCrossing] = AuditLevel.All,
            [AuditCategory.Bootstrap] = AuditLevel.All
        };

        /// <summary>
        /// Maximum events retained by the in-memory provider before FIFO eviction. Default 10000.
        /// </summary>
        public int MaxBufferedEvents { get; set; } = 10_000;

        /// <summary>
        /// Whether an event in the given category with the given outcome should be recorded
        /// under the configured levels.
        /// </summary>
        public bool ShouldRecord(AuditCategory category, AuditOutcome outcome)
        {
            var level = Categories.TryGetValue(category, out var configured) ? configured : DefaultLevel;
            return level switch
            {
                AuditLevel.All => true,
                AuditLevel.Failures => outcome is AuditOutcome.Denied or AuditOutcome.Error,
                _ => false
            };
        }
    }
}
