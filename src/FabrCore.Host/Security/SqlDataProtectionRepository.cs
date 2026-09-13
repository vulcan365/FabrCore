using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Data.SqlClient;

namespace FabrCore.Host.Security;

/// <summary>Append-only key-ring storage. Key XML is encrypted by Data Protection before persistence.</summary>
internal sealed class SqlDataProtectionRepository(string connectionString, string application) : IXmlRepository
{
    internal void Initialize(bool initialize)
    {
        using var connection = Open();
        if (initialize)
        {
            using var transaction = connection.BeginTransaction();
            using var command = new SqlCommand("""
                DECLARE @result int;
                EXEC @result=sys.sp_getapplock @Resource='FabrCore.DataProtection.Schema.v1', @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=25000;
                IF @result < 0 THROW 51000, 'Protection schema lock could not be acquired.', 1;
                """ + Schema, connection, transaction);
            command.ExecuteNonQuery(); transaction.Commit();
        }
        using var validation = new SqlCommand("SELECT TOP(0) ApplicationId, Id, FriendlyName, Xml FROM fabrOps.DataProtectionKey;", connection);
        validation.ExecuteNonQuery();
    }

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var connection = Open();
        using var command = new SqlCommand("SELECT Xml FROM fabrOps.DataProtectionKey WHERE ApplicationId=@application", connection);
        command.Parameters.AddWithValue("@application", application);
        using var reader = command.ExecuteReader();
        var result = new List<XElement>();
        while (reader.Read()) result.Add(XElement.Parse(reader.GetString(0)));
        return result;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var connection = Open();
        using var command = new SqlCommand("INSERT INTO fabrOps.DataProtectionKey(ApplicationId,Id,FriendlyName,Xml) VALUES(@application,@id,@name,@xml)", connection);
        command.Parameters.AddWithValue("@application", application);
        command.Parameters.AddWithValue("@id", Guid.NewGuid());
        command.Parameters.AddWithValue("@name", (object?)friendlyName ?? DBNull.Value);
        command.Parameters.AddWithValue("@xml", element.ToString(SaveOptions.DisableFormatting));
        command.ExecuteNonQuery();
    }

    private SqlConnection Open()
    {
        var connection = new SqlConnection(connectionString);
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    internal const string Schema = """
        IF SCHEMA_ID('fabrOps') IS NULL EXEC('CREATE SCHEMA fabrOps');
        IF OBJECT_ID('fabrOps.DataProtectionKey','U') IS NULL
            CREATE TABLE fabrOps.DataProtectionKey(
                ApplicationId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Id uniqueidentifier NOT NULL,
                FriendlyName nvarchar(256) NULL,
                Xml nvarchar(max) NOT NULL,
                CONSTRAINT PK_DataProtectionKey PRIMARY KEY(ApplicationId,Id));
        """;
}
