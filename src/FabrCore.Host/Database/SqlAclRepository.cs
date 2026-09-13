using System.Data;
using System.Text.Json;
using FabrCore.Core.Acl;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace FabrCore.Host.Database;

/// <summary>Relational ACL persistence, independent of the Orleans storage provider.</summary>
public sealed class SqlAclRepository(FabrCoreDatabaseOptions options, IConfiguration configuration)
{
    internal string ConnectionString => configuration.GetConnectionString(options.AclConnectionStringName ?? options.ConnectionStringName)
        ?? throw new InvalidOperationException("ACL requires the FabrCore SQL database.");

    internal const string SchemaSql = """
        IF SCHEMA_ID('acl') IS NULL EXEC('CREATE SCHEMA acl');
        IF OBJECT_ID('acl.Configuration') IS NULL
        BEGIN
            CREATE TABLE acl.Configuration (Id int NOT NULL PRIMARY KEY CHECK (Id=1), Version bigint NOT NULL, ModeOverride int NULL);
            INSERT acl.Configuration VALUES (1,0,NULL);
            CREATE TABLE acl.Principal (Handle nvarchar(200) COLLATE Latin1_General_100_CI_AS NOT NULL PRIMARY KEY, DisplayName nvarchar(max) NULL, IsSystem bit NOT NULL);
            CREATE TABLE acl.Role (Name nvarchar(200) COLLATE Latin1_General_100_CI_AS NOT NULL PRIMARY KEY, Description nvarchar(max) NULL, IsBuiltIn bit NOT NULL);
            CREATE TABLE acl.[Group] (Name nvarchar(200) COLLATE Latin1_General_100_CI_AS NOT NULL PRIMARY KEY, Description nvarchar(max) NULL, IsDynamic bit NOT NULL);
            CREATE TABLE acl.PrincipalRole (Handle nvarchar(200) COLLATE Latin1_General_100_CI_AS NOT NULL, RoleName nvarchar(200) COLLATE Latin1_General_100_CI_AS NOT NULL, PRIMARY KEY(Handle,RoleName));
            CREATE TABLE acl.GroupRole (Name nvarchar(200) COLLATE Latin1_General_100_CI_AS NOT NULL, RoleName nvarchar(200) COLLATE Latin1_General_100_CI_AS NOT NULL, PRIMARY KEY(Name,RoleName));
            CREATE TABLE acl.GroupMember (Name nvarchar(200) COLLATE Latin1_General_100_CI_AS NOT NULL, Kind int NOT NULL, Handle nvarchar(200) COLLATE Latin1_General_100_CI_AS NOT NULL, PRIMARY KEY(Name,Kind,Handle));
            CREATE TABLE acl.PermissionGrant (OwnerRole nvarchar(200) COLLATE Latin1_General_100_CI_AS NOT NULL, Id nvarchar(200) COLLATE Latin1_General_100_CI_AS NOT NULL, SubjectKind int NULL, Selector nvarchar(200) NULL, Permission nvarchar(200) NOT NULL, Resource nvarchar(1000) NOT NULL, PRIMARY KEY(OwnerRole,Id));
        END
        IF COL_LENGTH('acl.Principal', 'Description') IS NULL ALTER TABLE acl.Principal ADD Description nvarchar(max) NULL;
        """;

    public async Task<AclSnapshotData> LoadAsync(CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        // A shared lock on the version row provides a consistent multi-table snapshot.
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await using var cmd = new SqlCommand("""
            SELECT Version, ModeOverride FROM acl.Configuration WITH(HOLDLOCK) WHERE Id=1;
            SELECT Handle,DisplayName,IsSystem,Description FROM acl.Principal;
            SELECT Name,Description,IsBuiltIn FROM acl.Role;
            SELECT Name,Description,IsDynamic FROM acl.[Group];
            SELECT Handle,RoleName FROM acl.PrincipalRole;
            SELECT Name,RoleName FROM acl.GroupRole;
            SELECT Name,Kind,Handle FROM acl.GroupMember;
            SELECT OwnerRole,Id,SubjectKind,Selector,Permission,Resource FROM acl.PermissionGrant;
            """, connection, transaction);
        var result = new AclSnapshotData();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("ACL configuration row is missing.");
            result.Version = reader.GetInt64(0);
            result.ModeOverride = reader.IsDBNull(1) ? null : (AclEnforcementMode)reader.GetInt32(1);
            await reader.NextResultAsync(ct);
            while (await reader.ReadAsync(ct)) result.Principals.Add(new() { Handle=reader.GetString(0), DisplayName=reader.IsDBNull(1)?null:reader.GetString(1), IsSystem=reader.GetBoolean(2), Description=reader.IsDBNull(3)?null:reader.GetString(3) });
            await reader.NextResultAsync(ct);
            while (await reader.ReadAsync(ct)) result.Roles.Add(new() { Name=reader.GetString(0), Description=reader.IsDBNull(1)?null:reader.GetString(1), IsBuiltIn=reader.GetBoolean(2) });
            await reader.NextResultAsync(ct);
            while (await reader.ReadAsync(ct)) result.Groups.Add(new() { Name=reader.GetString(0), Description=reader.IsDBNull(1)?null:reader.GetString(1), IsDynamic=reader.GetBoolean(2) });
            var principals=result.Principals.ToDictionary(p=>p.Handle,StringComparer.OrdinalIgnoreCase);
            var roles=result.Roles.ToDictionary(p=>p.Name,StringComparer.OrdinalIgnoreCase);
            var groups=result.Groups.ToDictionary(p=>p.Name,StringComparer.OrdinalIgnoreCase);
            await reader.NextResultAsync(ct);
            while (await reader.ReadAsync(ct)) principals[reader.GetString(0)].Roles.Add(reader.GetString(1));
            await reader.NextResultAsync(ct);
            while (await reader.ReadAsync(ct)) groups[reader.GetString(0)].Roles.Add(reader.GetString(1));
            await reader.NextResultAsync(ct);
            while (await reader.ReadAsync(ct)) groups[reader.GetString(0)].Members.Add(new((SubjectKind)reader.GetInt32(1),reader.GetString(2)));
            await reader.NextResultAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var grant=new PermissionGrant { Id=reader.GetString(1), Subject=reader.IsDBNull(2)?null:new((SubjectKind)reader.GetInt32(2),reader.GetString(3)), Permission=reader.GetString(4), Resource=reader.GetString(5) };
                if (reader.GetString(0) is { Length: > 0 } owner) roles[owner].Grants.Add(grant);
                else result.Grants.Add(grant);
            }
        }
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task SaveAsync(AclSnapshotData snapshot, long expectedVersion, CancellationToken ct = default)
    {
        ValidateLengths(snapshot);
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        await using var version = new SqlCommand("UPDATE acl.Configuration SET Version=@next, ModeOverride=@mode WHERE Id=1 AND Version=@expected", connection, tx);
        version.Parameters.AddWithValue("@next", expectedVersion+1);
        version.Parameters.AddWithValue("@expected", expectedVersion);
        version.Parameters.AddWithValue("@mode", (object?)snapshot.ModeOverride is null ? DBNull.Value : (int)snapshot.ModeOverride.Value);
        if (await version.ExecuteNonQueryAsync(ct)!=1) throw new InvalidOperationException("ACL changed concurrently. Reload and retry the operation.");

        // JSON is only a parameter transport for set-based synchronization; no JSON is persisted.
        await Sync("Principal", snapshot.Principals, "Handle nvarchar(200), DisplayName nvarchar(max), IsSystem bit, Description nvarchar(max)", ["Handle"], ["DisplayName","IsSystem","Description"]);
        await Sync("Role", snapshot.Roles, "Name nvarchar(200), Description nvarchar(max), IsBuiltIn bit", ["Name"], ["Description","IsBuiltIn"]);
        await Sync("Group", snapshot.Groups, "Name nvarchar(200), Description nvarchar(max), IsDynamic bit", ["Name"], ["Description","IsDynamic"]);
        await Sync("PrincipalRole", snapshot.Principals.SelectMany(p=>p.Roles.Distinct(StringComparer.OrdinalIgnoreCase).Select(r=>new {p.Handle,RoleName=r})), "Handle nvarchar(200), RoleName nvarchar(200)", ["Handle","RoleName"], []);
        await Sync("GroupRole", snapshot.Groups.SelectMany(p=>p.Roles.Distinct(StringComparer.OrdinalIgnoreCase).Select(r=>new {p.Name,RoleName=r})), "Name nvarchar(200), RoleName nvarchar(200)", ["Name","RoleName"], []);
        await Sync("GroupMember", snapshot.Groups.SelectMany(p=>p.Members.Select(m=>new {p.Name,m.Kind,m.Handle})), "Name nvarchar(200), Kind int, Handle nvarchar(200)", ["Name","Kind","Handle"], []);
        var grants = snapshot.Roles.SelectMany(r=>r.Grants.Select(g=>Row(r.Name,g))).Concat(snapshot.Grants.Select(g=>Row("",g)));
        await Sync("PermissionGrant", grants, "OwnerRole nvarchar(200), Id nvarchar(200), SubjectKind int, Selector nvarchar(200), Permission nvarchar(200), Resource nvarchar(1000)", ["OwnerRole","Id"], ["SubjectKind","Selector","Permission","Resource"]);
        await tx.CommitAsync(ct);

        static object Row(string owner, PermissionGrant g) => new {OwnerRole=owner,g.Id,SubjectKind=(int?)g.Subject?.Kind,Selector=g.Subject?.Selector,g.Permission,g.Resource};
        async Task Sync(string table, object rows, string shape, string[] keys, string[] values)
        {
            var columns=keys.Concat(values).ToArray();
            var update=values.Length==0?"":$"WHEN MATCHED THEN UPDATE SET {string.Join(",",values.Select(v=>$"t.[{v}]=s.[{v}]"))}";
            var sql=$"MERGE acl.[{table}] WITH(HOLDLOCK) t USING (SELECT * FROM OPENJSON(@rows) WITH ({shape})) s ON {string.Join(" AND ",keys.Select(k=>$"t.[{k}]=s.[{k}]"))} {update} WHEN NOT MATCHED THEN INSERT ({string.Join(",",columns.Select(c=>$"[{c}]"))}) VALUES ({string.Join(",",columns.Select(c=>$"s.[{c}]"))}) WHEN NOT MATCHED BY SOURCE THEN DELETE;";
            await using var command=new SqlCommand(sql,connection,tx);
            command.Parameters.Add("@rows",SqlDbType.NVarChar,-1).Value=JsonSerializer.Serialize(rows);
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    internal async Task<long> GetVersionAsync(CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT Version FROM acl.Configuration WHERE Id=1", connection);
        return (long)(await cmd.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException("ACL configuration is missing."));
    }

    private static void ValidateLengths(AclSnapshotData snapshot)
    {
        static void Check(string? value, int maximum, string field, bool optional=false)
        {
            if ((!optional && string.IsNullOrWhiteSpace(value)) || value?.Length>maximum)
                throw new ArgumentException($"ACL {field} must be nonempty and at most {maximum} characters.");
        }
        foreach(var p in snapshot.Principals) { Check(p.Handle,200,"principal handle"); foreach(var role in p.Roles) Check(role,200,"role name"); }
        foreach(var r in snapshot.Roles) Check(r.Name,200,"role name");
        foreach(var g in snapshot.Groups)
        {
            Check(g.Name,200,"group name"); foreach(var role in g.Roles) Check(role,200,"role name");
            foreach(var member in g.Members) { Check(member.Handle,200,"member handle"); if(member.Kind is not (SubjectKind.Agent or SubjectKind.Principal)) throw new ArgumentException("ACL group members must be principals or agents."); }
        }
        foreach(var g in snapshot.Grants.Concat(snapshot.Roles.SelectMany(r=>r.Grants)))
        {
            Check(g.Id,200,"grant id"); Check(g.Permission,200,"permission"); PermissionName.Parse(g.Permission);
            Check(g.Resource,1000,"resource"); if(g.Subject is not null) Check(g.Subject.Selector,200,"subject");
        }
        if(snapshot.ModeOverride is { } mode && !Enum.IsDefined(mode)) throw new ArgumentException("Invalid ACL enforcement mode.");
    }
}

