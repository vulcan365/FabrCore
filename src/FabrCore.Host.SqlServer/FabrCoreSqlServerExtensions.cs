namespace FabrCore.Host.SqlServer;

/// <summary>
/// Extension methods for wiring the SQL Server Orleans provider into a FabrCore server.
/// </summary>
public static class FabrCoreSqlServerExtensions
{
    /// <summary>
    /// Uses SQL Server for Orleans clustering, grain persistence, and reminders.
    /// <para>
    /// Calling this is optional — referencing the FabrCore.Host.SqlServer package and setting
    /// <c>Orleans:ClusteringMode</c> to <c>SqlServer</c> is enough for the provider to be
    /// auto-discovered by <see cref="FabrCoreHostExtensions.AddFabrCoreServer"/>.
    /// </para>
    /// </summary>
    public static FabrCoreServerOptions UseSqlServer(this FabrCoreServerOptions options)
        => options.UseOrleansProvider(new SqlServerOrleansProvider());
}
