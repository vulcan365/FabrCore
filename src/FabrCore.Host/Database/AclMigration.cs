using System.Text.Json;
using System.Text.Json.Serialization;
using FabrCore.Core.Acl;

namespace FabrCore.Host.Database;

/// <summary>Explicit import conversion for legacy ACL exports and JSON seeds. Never runs during startup.</summary>
public static class AclMigration
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };

    public static AclSnapshotData Read(JsonElement document)
    {
        if (TryProperty(document, "FabrCore", out var fabrCore)) document = fabrCore;
        if (TryProperty(document, "Acl", out var acl)) document = acl;
        AclSnapshotData snapshot;
        if (TryProperty(document, "Seed", out var seedDocument))
        {
            var seed = seedDocument.Deserialize<AclSeedOptions>(Json) ?? throw new ArgumentException("ACL seed is empty.");
            snapshot = new()
            {
                Principals=seed.Principals.Select(p=>new AclPrincipal {Handle=p.Handle,DisplayName=p.DisplayName,Description=p.Description,Roles=p.Roles}).ToList(),
                Roles=seed.Roles.Select(r=>new AclRole {Name=r.Name,Description=r.Description,Grants=r.Grants.Select(g=>Grant(g,false)).ToList()}).ToList(),
                Groups=seed.Groups.Select(g=>new AclGroup {Name=g.Name,Description=g.Description,Roles=g.Roles,Members=g.Members.Select(m=> { var subject=AclSubject.Parse(m); return new GroupMember(subject.Kind,subject.Selector); }).ToList()}).ToList(),
                Grants=seed.Grants.Select(g=>Grant(g,true)).ToList()
            };
        }
        else snapshot = document.Deserialize<AclSnapshotData>(Json) ?? throw new ArgumentException("ACL export is empty.");
        if (TryProperty(document,"EnforcementMode",out var mode) && mode.ValueKind==JsonValueKind.String && Enum.TryParse<AclEnforcementMode>(mode.GetString(),true,out var parsed)) snapshot.ModeOverride=parsed;
        return snapshot;
    }

    public static AclSnapshotData MergeIntoEmpty(AclSnapshotData existing, AclSnapshotData incoming, FabrCoreAclOptions options)
    {
        if (existing.Principals.Any(p=>!p.IsSystem) || existing.Roles.Any(r=>!r.IsBuiltIn) || existing.Groups.Any(g=>!g.IsDynamic) ||
            existing.Grants.Any(g=>g.Id is not ("builtin-system-agent-message" or "builtin-system-agent-read")))
            throw new InvalidOperationException("ACL import requires an installation containing only built-in ACL data.");
        var result = JsonSerializer.Deserialize<AclSnapshotData>(JsonSerializer.Serialize(incoming,Json),Json)!;
        Unique(result.Principals.Select(p=>p.Handle),"principal");
        Unique(result.Roles.Select(r=>r.Name),"role");
        Unique(result.Groups.Select(g=>g.Name),"group");
        Unique(result.Grants.Select(g=>g.Id),"grant");
        foreach (var p in result.Principals) p.IsSystem=string.Equals(p.Handle,options.SystemPrincipal,StringComparison.OrdinalIgnoreCase);
        foreach (var r in result.Roles) r.IsBuiltIn=existing.Roles.Any(e=>e.IsBuiltIn && string.Equals(e.Name,r.Name,StringComparison.OrdinalIgnoreCase));
        foreach (var g in result.Groups) g.IsDynamic=existing.Groups.Any(e=>e.IsDynamic && string.Equals(e.Name,g.Name,StringComparison.OrdinalIgnoreCase));
        foreach (var p in existing.Principals.Where(p=>p.IsSystem)) if (!result.Principals.Any(i=>string.Equals(i.Handle,p.Handle,StringComparison.OrdinalIgnoreCase))) result.Principals.Add(p);
        foreach (var r in existing.Roles.Where(r=>r.IsBuiltIn)) if (!result.Roles.Any(i=>string.Equals(i.Name,r.Name,StringComparison.OrdinalIgnoreCase))) result.Roles.Add(r);
        foreach (var g in existing.Groups.Where(g=>g.IsDynamic)) if (!result.Groups.Any(i=>string.Equals(i.Name,g.Name,StringComparison.OrdinalIgnoreCase))) result.Groups.Add(g);
        // Exported grants replace demo defaults, including an intentionally empty grant set.
        var roles=result.Roles.Select(r=>r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var principals=result.Principals.Select(r=>r.Handle).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groups=result.Groups.Select(r=>r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in result.Principals.SelectMany(p=>p.Roles).Concat(result.Groups.SelectMany(g=>g.Roles)))
            if (!roles.Contains(name)) throw new ArgumentException($"ACL import references missing role '{name}'.");
        foreach (var group in result.Groups)
        {
            if (group.IsDynamic && group.Members.Count>0) throw new ArgumentException("Dynamic groups cannot have stored members.");
            foreach (var member in group.Members)
                if (member.Kind is not (SubjectKind.Principal or SubjectKind.Agent) || !principals.Contains(member.Handle.Split(':')[0]))
                    throw new ArgumentException($"Invalid or unknown group member '{member.Handle}'.");
        }
        foreach (var role in result.Roles) { Unique(role.Grants.Select(g=>g.Id),"role grant"); foreach (var grant in role.Grants) ValidateGrant(grant,false); }
        foreach (var grant in result.Grants)
        {
            ValidateGrant(grant,true);
            var subject=grant.Subject!;
            var exists=subject.Kind switch {SubjectKind.Principal=>principals.Contains(subject.Selector),SubjectKind.Agent=>principals.Contains(subject.Selector.Split(':')[0]),SubjectKind.Role=>roles.Contains(subject.Selector),SubjectKind.Group=>groups.Contains(subject.Selector),_=>false};
            if (!exists) throw new ArgumentException($"ACL import references unknown subject '{subject}'.");
        }
        result.Version=existing.Version;
        return result;
    }

    private static PermissionGrant Grant(AclGrantSeed seed,bool subject) => new() {Subject=subject?AclSubject.Parse(seed.Subject):null,Permission=PermissionName.Parse(seed.Permission).Value,Resource=string.IsNullOrWhiteSpace(seed.Resource)?"*:*":seed.Resource};
    private static void ValidateGrant(PermissionGrant grant,bool subject)
    {
        PermissionName.Parse(grant.Permission);
        if (string.IsNullOrWhiteSpace(grant.Resource) || (subject && (grant.Subject is null || string.IsNullOrWhiteSpace(grant.Subject.Selector)))) throw new ArgumentException("ACL grant requires a resource and subject.");
    }
    private static void Unique(IEnumerable<string> values,string kind)
    {
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values) if (string.IsNullOrWhiteSpace(value) || value.Length>200 || !seen.Add(value)) throw new ArgumentException($"Invalid or duplicate {kind} key '{value}'.");
    }
    private static bool TryProperty(JsonElement document,string name,out JsonElement value)
    {
        if (document.ValueKind==JsonValueKind.Object) foreach (var property in document.EnumerateObject()) if (property.Name.Equals(name,StringComparison.OrdinalIgnoreCase)) {value=property.Value;return true;}
        value=default;return false;
    }
}
