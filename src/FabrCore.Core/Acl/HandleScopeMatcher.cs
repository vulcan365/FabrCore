namespace FabrCore.Core.Acl
{
    /// <summary>
    /// Matches <see cref="PermissionGrant.Resource"/> patterns against a target
    /// <c>"principal:agent"</c> handle. Shared by the evaluator and tests so pattern
    /// semantics live in exactly one place.
    /// </summary>
    public static class HandleScopeMatcher
    {
        /// <summary>
        /// Matches a resource pattern against a target principal + agent pair.
        /// <para>
        /// The pattern is split on its first <c>':'</c> into a principal segment and an agent
        /// segment (a missing agent segment means <c>"*"</c>). Each segment matches with
        /// <c>"*"</c> (any), <c>"prefix*"</c> (starts-with), or exact comparison — all
        /// case-insensitive. A principal segment of the form <c>"group:&lt;name&gt;"</c> matches
        /// when the target principal is a member of that group, resolved through
        /// <paramref name="isPrincipalInGroup"/> (dynamic groups included).
        /// </para>
        /// </summary>
        public static bool Matches(
            string resourcePattern,
            string targetPrincipal,
            string targetAgent,
            Func<string, string, bool> isPrincipalInGroup)
        {
            if (string.IsNullOrEmpty(resourcePattern))
                return false;

            string principalPattern;
            string agentPattern;

            if (resourcePattern.StartsWith("group:", StringComparison.OrdinalIgnoreCase))
            {
                // "group:<name>" or "group:<name>:<agentPattern>" — split after the group name.
                var afterPrefix = "group:".Length;
                var agentSeparator = resourcePattern.IndexOf(':', afterPrefix);
                if (agentSeparator < 0)
                {
                    principalPattern = resourcePattern;
                    agentPattern = "*";
                }
                else
                {
                    principalPattern = resourcePattern[..agentSeparator];
                    agentPattern = resourcePattern[(agentSeparator + 1)..];
                }
            }
            else
            {
                var separator = resourcePattern.IndexOf(':');
                if (separator < 0)
                {
                    principalPattern = resourcePattern;
                    agentPattern = "*";
                }
                else
                {
                    principalPattern = resourcePattern[..separator];
                    agentPattern = resourcePattern[(separator + 1)..];
                }
            }

            return MatchesPrincipalSegment(principalPattern, targetPrincipal, isPrincipalInGroup)
                && MatchesSegment(agentPattern, targetAgent);
        }

        private static bool MatchesPrincipalSegment(
            string pattern,
            string targetPrincipal,
            Func<string, string, bool> isPrincipalInGroup)
        {
            if (pattern.StartsWith("group:", StringComparison.OrdinalIgnoreCase))
            {
                var groupName = pattern["group:".Length..];
                return groupName.Length > 0 && isPrincipalInGroup(groupName, targetPrincipal);
            }

            return MatchesSegment(pattern, targetPrincipal);
        }

        private static bool MatchesSegment(string pattern, string target)
        {
            if (pattern == "*")
                return true;

            if (pattern.EndsWith('*'))
                return target.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);

            return string.Equals(pattern, target, StringComparison.OrdinalIgnoreCase);
        }
    }
}
