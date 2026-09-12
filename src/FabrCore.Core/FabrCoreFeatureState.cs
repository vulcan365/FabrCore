using System.Reflection;

namespace FabrCore.Core;

/// <summary>Immutable startup feature selection. Database mode enables ACL, Memory and GraphRAG.</summary>
public sealed class FabrCoreFeatureState(bool databaseEnabled)
{
    public bool DatabaseEnabled { get; } = databaseEnabled;
    public bool IsAvailable(MemberInfo member) => DatabaseEnabled ||
        !(member.IsDefined(typeof(RequiresFabrCoreDatabaseAttribute), true) ||
          member.DeclaringType?.IsDefined(typeof(RequiresFabrCoreDatabaseAttribute), true) == true);

    public void RequireAvailable(MemberInfo member)
    {
        if (!IsAvailable(member))
            throw new InvalidOperationException($"Feature '{member.Name}' requires FabrCore SQL mode. Configure ConnectionStrings:FabrCore and restart the host.");
    }
}

/// <summary>Marks an agent, plugin, tool or endpoint that requires the database feature set.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class RequiresFabrCoreDatabaseAttribute : Attribute;
