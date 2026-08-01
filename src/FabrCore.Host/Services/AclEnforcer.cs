using FabrCore.Core.Acl;
using FabrCore.Core.Auditing;
using Microsoft.Extensions.Logging;

namespace FabrCore.Host.Services;

/// <summary>
/// Compatibility name for the ACL enforcement helper now owned by
/// <see cref="FabrCore.Core.Acl.AclEnforcer"/>.
/// </summary>
public sealed class AclEnforcer(
    IAclEvaluator evaluator,
    IAuditProvider audit,
    ILogger<AclEnforcer> logger)
    : FabrCore.Core.Acl.AclEnforcer(evaluator, audit, logger);
