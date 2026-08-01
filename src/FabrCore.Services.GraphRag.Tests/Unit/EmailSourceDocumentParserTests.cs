using System.Text.Json;
using FabrCore.Services.GraphRag.Services;

namespace FabrCore.Services.GraphRag.Tests.Unit;

[TestClass]
public sealed class EmailSourceDocumentParserTests
{
    private const string Email = """
        ---
        messageId: "provider-id"
        internetMessageId: "<apollo@example.com>"
        subject: "Apollo deployment"
        from: "Jane <jane@example.com>"
        to: "Eric <eric@example.com>"
        receivedDateTimeUtc: "2026-05-17T15:42:18Z"
        hasAttachments: "False"
        ---

        # Apollo deployment

        ## Email Metadata
        **From:** Jane <jane@example.com>

        ## Body
        The Apollo launch window is Thursday at 09:00 UTC.
        """;

    [TestMethod]
    public void Normalize_Email_UsesDurableIdentityAndSearchableBodyOnly()
    {
        var result = EmailSourceDocumentParser.Normalize("upload-42.md", Email);

        Assert.AreEqual("Email", result.SourceKind);
        Assert.AreEqual("<apollo@example.com>", result.SourceKey);
        Assert.AreEqual("Apollo deployment", result.SourceTitle);
        Assert.AreEqual(new DateTime(2026, 5, 17, 15, 42, 18, DateTimeKind.Utc), result.SourceOccurredAtUtc);
        StringAssert.Contains(result.ContentForIngestion, "launch window is Thursday");
        Assert.IsFalse(result.ContentForIngestion.Contains("Email Metadata", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(result.ContentForIngestion.Contains("internetMessageId", StringComparison.OrdinalIgnoreCase));

        using var metadata = JsonDocument.Parse(result.MetadataJson!);
        Assert.AreEqual("Email", metadata.RootElement.GetProperty("sourceKind").GetString());
        Assert.IsFalse(metadata.RootElement.GetProperty("hasAttachments").GetBoolean());
    }

    [TestMethod]
    public void Normalize_Markdown_PreservesContentAndUsesFileIdentity()
    {
        const string markdown = "# Runbook\n\nRestart the database replica.";
        var result = EmailSourceDocumentParser.Normalize("runbook.md", markdown);

        Assert.AreEqual("Markdown", result.SourceKind);
        Assert.AreEqual("runbook.md", result.SourceKey);
        Assert.AreEqual(markdown, result.ContentForIngestion);
        Assert.IsNull(result.MetadataJson);
    }

    [TestMethod]
    public void Normalize_EmailWithoutInternetMessageId_FallsBackToProviderMessageId()
    {
        var result = EmailSourceDocumentParser.Normalize("upload.md", """
            ---
            messageId: "provider-message-id"
            subject: "Status"
            ---
            Body
            """);

        Assert.AreEqual("provider-message-id", result.SourceKey);
    }
}
