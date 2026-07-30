using FabrCore.Services.GraphRag.Tests.Infrastructure;
using Microsoft.Data.SqlClient;

namespace FabrCore.Services.GraphRag.Tests.Integration;

[TestClass]
[TestCategory("Integration")]
public sealed class ScopeIntegrationTests
{
    [TestMethod]
    public async Task ScopeRegistry_CreateReadListAndAudit_RoundTrips()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var scopeKey = fixture.CreateScopeKey("scope-roundtrip");

        var created = await fixture.Scopes.CreateScopeAsync(scopeKey, "Isolated test scope", 0.75, "{\"owner\":\"tests\"}");
        var loaded = await fixture.Scopes.GetScopeAsync(scopeKey);
        var listed = await fixture.Scopes.ListScopesAsync();

        Assert.AreEqual(scopeKey, created.ScopeKey);
        Assert.AreEqual(0.75, loaded!.DefaultPriority, 0.0001);
        Assert.IsTrue(await fixture.Scopes.ScopeExistsAsync(scopeKey));
        Assert.IsTrue(listed.Any(s => s.ScopeKey == scopeKey));
        Assert.AreEqual(0, await fixture.Scopes.CountEntitiesInScopeAsync(scopeKey));

        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT COUNT(*) FROM grag.ActionAudit
            WHERE ScopeKey = @scope AND ActionType = 'ScopeCreated';
            """, connection);
        command.Parameters.AddWithValue("@scope", scopeKey);
        Assert.AreEqual(1, Convert.ToInt32(await command.ExecuteScalarAsync()));
    }

    [TestMethod]
    public async Task ScopeRegistry_DuplicateKeyIsRejected()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var scopeKey = fixture.CreateScopeKey("scope-duplicate");
        await fixture.Scopes.CreateScopeAsync(scopeKey, "first");

        await Assert.ThrowsAsync<SqlException>(() => fixture.Scopes.CreateScopeAsync(scopeKey, "second"));
    }
}
