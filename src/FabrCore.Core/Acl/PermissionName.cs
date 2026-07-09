using System.Text.RegularExpressions;

namespace FabrCore.Core.Acl
{
    /// <summary>Effect of a permission: grant or explicit denial. Deny overrides allow.</summary>
    public enum PermissionEffect
    {
        Allow,
        Deny
    }

    /// <summary>
    /// A validated permission name in 3-dot notation: <c>entity.behavior.effect</c>
    /// (e.g. <c>"agent.message.allow"</c>, <c>"agent.create.deny"</c>).
    /// <para>
    /// Segments are lowercase <c>[a-z0-9-]</c>. The entity/behavior vocabulary is open —
    /// consuming applications may define their own pairs (e.g. <c>"surface.adminview.allow"</c>) —
    /// but the effect segment is closed: only <c>allow</c> and <c>deny</c> are valid.
    /// Entities <c>agent</c>, <c>principal</c>, <c>acl</c>, <c>system</c>, and <c>fabrcore</c>
    /// are reserved for FabrCore built-ins.
    /// </para>
    /// </summary>
    public readonly record struct PermissionName
    {
        private static readonly Regex SegmentPattern = new("^[a-z0-9][a-z0-9-]*$", RegexOptions.Compiled);

        /// <summary>Entity segments reserved for FabrCore built-in permissions.</summary>
        public static readonly IReadOnlyList<string> ReservedEntities = new[] { "agent", "principal", "acl", "system", "fabrcore" };

        public string Entity { get; }
        public string Behavior { get; }
        public PermissionEffect Effect { get; }

        public PermissionName(string entity, string behavior, PermissionEffect effect)
        {
            ValidateSegment(entity, nameof(entity));
            ValidateSegment(behavior, nameof(behavior));
            Entity = entity;
            Behavior = behavior;
            Effect = effect;
        }

        /// <summary>The full 3-dot name, e.g. <c>"agent.message.allow"</c>.</summary>
        public string Value => $"{Entity}.{Behavior}.{(Effect == PermissionEffect.Allow ? "allow" : "deny")}";

        /// <summary>The effect-less action stem (<c>entity.behavior</c>).</summary>
        public AclAction Action => new(Entity, Behavior);

        /// <summary>True when the entity segment is reserved for FabrCore built-ins.</summary>
        public bool IsReservedEntity => ReservedEntities.Contains(Entity, StringComparer.Ordinal);

        /// <summary>Parses a 3-dot permission name. Throws <see cref="FormatException"/> when invalid.</summary>
        public static PermissionName Parse(string value)
        {
            if (!TryParse(value, out var name))
                throw new FormatException(
                    $"Invalid permission name '{value}'. Expected 'entity.behavior.effect' with lowercase " +
                    "[a-z0-9-] segments and an effect of 'allow' or 'deny' (e.g. 'agent.message.allow').");
            return name;
        }

        /// <summary>Attempts to parse a 3-dot permission name.</summary>
        public static bool TryParse(string? value, out PermissionName name)
        {
            name = default;
            if (string.IsNullOrEmpty(value))
                return false;

            var segments = value.Split('.');
            if (segments.Length != 3)
                return false;

            if (!SegmentPattern.IsMatch(segments[0]) || !SegmentPattern.IsMatch(segments[1]))
                return false;

            PermissionEffect effect;
            switch (segments[2])
            {
                case "allow": effect = PermissionEffect.Allow; break;
                case "deny": effect = PermissionEffect.Deny; break;
                default: return false;
            }

            name = new PermissionName(segments[0], segments[1], effect);
            return true;
        }

        public override string ToString() => Value;

        private static void ValidateSegment(string segment, string paramName)
        {
            if (string.IsNullOrEmpty(segment) || !SegmentPattern.IsMatch(segment))
                throw new ArgumentException(
                    $"Permission name segment '{segment}' is invalid. Segments must match [a-z0-9][a-z0-9-]*.",
                    paramName);
        }
    }

    /// <summary>
    /// An effect-less permission stem (<c>entity.behavior</c>) used at enforcement call sites:
    /// the caller asks for an action; grants supply the allow/deny effect.
    /// </summary>
    public readonly record struct AclAction(string Entity, string Behavior)
    {
        /// <summary>The allow form of this action.</summary>
        public PermissionName Allow => new(Entity, Behavior, PermissionEffect.Allow);

        /// <summary>The deny form of this action.</summary>
        public PermissionName Deny => new(Entity, Behavior, PermissionEffect.Deny);

        /// <summary>Parses an <c>entity.behavior</c> stem. Throws <see cref="FormatException"/> when invalid.</summary>
        public static AclAction Parse(string value)
        {
            var segments = (value ?? string.Empty).Split('.');
            if (segments.Length != 2)
                throw new FormatException($"Invalid action '{value}'. Expected 'entity.behavior' (e.g. 'agent.message').");
            // PermissionName's constructor validates the segments.
            return new PermissionName(segments[0], segments[1], PermissionEffect.Allow).Action;
        }

        public override string ToString() => $"{Entity}.{Behavior}";
    }
}
