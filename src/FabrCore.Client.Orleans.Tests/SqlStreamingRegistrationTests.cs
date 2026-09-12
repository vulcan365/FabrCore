using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;

namespace FabrCore.Client.Orleans.Tests;

[TestClass]
public class SqlStreamingRegistrationTests
{
    [TestMethod]
    public void SqlStreams_RegistersMatchingProviderAndConnection()
    {
        const string connection = "Server=localhost;Database=cluster;Integrated Security=true";
        using var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder().UseOrleansClient(client =>
        {
            client.UseLocalhostClustering();
            client.AddFabrCoreSqlServerStreams(connection);
        }).Build();
        var options = host.Services.GetRequiredService<IOptionsMonitor<AdoNetStreamOptions>>().Get("fabrcoreStreams");
        Assert.AreEqual(connection, options.ConnectionString);
        Assert.AreEqual("Microsoft.Data.SqlClient", options.Invariant);
    }
}
