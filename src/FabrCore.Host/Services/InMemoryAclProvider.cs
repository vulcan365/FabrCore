using FabrCore.Core.Acl;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FabrCore.Host.Services
{
    /// <summary>
    /// Default in-memory ACL provider that loads rules from <see cref="FabrCoreAclOptions"/> configuration.
    /// Rules are evaluated in order; first match wins. Supports runtime add/remove (in-memory only).
    /// <para>
    /// If no rules are configured, a default rule is applied: all callers can Message and Read system-owned agents.
    /// </para>
    /// </summary>
    public class InMemoryAclProvider : IAclProvider
    {
        private readonly ILogger<InMemoryAclProvider> _logger;
        private readonly object _lock = new();
        private readonly List<AclRule> _rules;
        private readonly Dictionary<string, HashSet<string>> _groups;

        public InMemoryAclProvider(IOptions<FabrCoreAclOptions> options, ILogger<InMemoryAclProvider> logger)
        {
            _logger = logger;

            var opts = options.Value;
            _rules = new List<AclRule>(opts.Rules);
            _groups = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var (groupName, members) in opts.Groups)
            {
                _groups[groupName] = new HashSet<string>(members, StringComparer.OrdinalIgnoreCase);
            }

            // If no rules configured, seed a default: all callers can Message+Read system agents
            if (_rules.Count == 0)
            {
                _rules.Add(new AclRule
                {
                    OwnerPattern = "system",
                    AgentPattern = "*",
                    CallerPattern = "*",
                    Permission = AclPermission.Message | AclPermission.Read
                });

                _logger.LogInformation("No ACL rules configured — seeded default rule: system:* -> * -> Message,Read");
            }

            _logger.LogInformation("InMemoryAclProvider initialized with {RuleCount} rules and {GroupCount} groups",
                _rules.Count, _groups.Count);
        }

        public Task<AclEvaluationResult> EvaluateAsync(
            string callerOwner,
            string targetOwner,
            string agentAlias,
            AclPermission required)
        {
            // Implicit rule: owner always has full access to their own agents
            if (string.Equals(callerOwner, targetOwner, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new AclEvaluationResult(true, AclPermission.All));
            }

            lock (_lock)
            {
                foreach (var rule in _rules)
                {
                    if (MatchesPattern(rule.OwnerPattern, targetOwner) &&
                        MatchesPattern(rule.AgentPattern, agentAlias) &&
                        MatchesCaller(rule.CallerPattern, callerOwner))
                    {
                        if (rule.Permission.HasFlag(required))
                        {
                            return Task.FromResult(new AclEvaluationResult(true, rule.Permission));
                        }

                        // Rule matched the target but doesn't grant the required permission
                        return Task.FromResult(new AclEvaluationResult(
                            false,
                            rule.Permission,
                            $"Rule matched ({rule.OwnerPattern}:{rule.AgentPattern} -> {rule.CallerPattern}) but grants [{rule.Permission}], not [{required}]"));
                    }
                }
            }

            return Task.FromResult(new AclEvaluationResult(
                false,
                AclPermission.None,
                $"No ACL rule matched for caller '{callerOwner}' accessing '{targetOwner}:{agentAlias}'"));
        }

        public Task<List<AclRule>> GetRulesAsync()
        {
            lock (_lock)
            {
                return Task.FromResult(new List<AclRule>(_rules));
            }
        }

        public Task AddRuleAsync(AclRule rule)
        {
            lock (_lock)
            {
                _rules.Add(rule);
            }

            _logger.LogInformation("ACL rule added: {Owner}:{Agent} -> {Caller} [{Permission}]",
                rule.OwnerPattern, rule.AgentPattern, rule.CallerPattern, rule.Permission);

            return Task.CompletedTask;
        }

        public Task RemoveRuleAsync(AclRule rule)
        {
            lock (_lock)
            {
                _rules.RemoveAll(r =>
                    string.Equals(r.OwnerPattern, rule.OwnerPattern, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r.AgentPattern, rule.AgentPattern, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r.CallerPattern, rule.CallerPattern, StringComparison.OrdinalIgnoreCase) &&
                    r.Permission == rule.Permission);
            }

            _logger.LogInformation("ACL rule removed: {Owner}:{Agent} -> {Caller} [{Permission}]",
                rule.OwnerPattern, rule.AgentPattern, rule.CallerPattern, rule.Permission);

            return Task.CompletedTask;
        }

        public Task<Dictionary<string, HashSet<string>>> GetGroupsAsync()
        {
            lock (_lock)
            {
                var copy = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, value) in _groups)
                {
                    copy[key] = new HashSet<string>(value, StringComparer.OrdinalIgnoreCase);
                }
                return Task.FromResult(copy);
            }
        }

        public Task AddToGroupAsync(string groupName, string member)
        {
            lock (_lock)
            {
                if (!_groups.TryGetValue(groupName, out var members))
                {
                    members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _groups[groupName] = members;
                }
                members.Add(member);
            }

            _logger.LogInformation("Added '{Member}' to group '{Group}'", member, groupName);
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(string groupName, string member)
        {
            lock (_lock)
            {
                if (_groups.TryGetValue(groupName, out var members))
                {
                    members.Remove(member);
                    if (members.Count == 0)
                        _groups.Remove(groupName);
                }
            }

            _logger.LogInformation("Removed '{Member}' from group '{Group}'", member, groupName);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Matches a pattern against a value. Supports <c>"*"</c> (any), <c>"prefix*"</c> (starts-with),
        /// and exact case-insensitive match.
        /// </summary>
        private static bool MatchesPattern(string pattern, string value)
        {
            if (pattern == "*")
                return true;

            if (pattern.EndsWith('*'))
                return value.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);

            return string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Matches a caller pattern, with additional support for <c>"group:name"</c> syntax.
        /// </summary>
        private bool MatchesCaller(string pattern, string callerOwner)
        {
            if (pattern.StartsWith("group:", StringComparison.OrdinalIgnoreCase))
            {
                var groupName = pattern[6..];
                return IsInGroup(groupName, callerOwner);
            }

            return MatchesPattern(pattern, callerOwner);
        }

        private bool IsInGroup(string groupName, string member)
        {
            // Lock already held by caller (EvaluateAsync) in the evaluation path,
            // but this is also called from MatchesCaller inside the lock.
            if (_groups.TryGetValue(groupName, out var members))
            {
                return members.Contains(member);
            }

            return false;
        }
    }
}
