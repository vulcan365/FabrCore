namespace FabrCore.Services.Microsoft365Copilot.Tests;

[TestClass]
public sealed class CopilotHandleSanitizerTests
{
    [TestMethod]
    public void RemovesColons_FromTeamsConversationIds()
    {
        var result = CopilotHandleSanitizer.SanitizeAgentHandleFragment("19:abc123@thread.v2");

        Assert.IsFalse(result.Contains(':'));
        Assert.AreEqual("19-abc123@thread.v2", result);
    }

    [TestMethod]
    public void Lowercases_AndPreservesGuids()
    {
        var result = CopilotHandleSanitizer.SanitizePrincipalHandle("A1B2C3D4-0000-0000-0000-000000000000");

        Assert.AreEqual("a1b2c3d4-0000-0000-0000-000000000000", result);
    }

    [TestMethod]
    public void CollapsesRuns_OfInvalidCharacters_AndTrimsDashes()
    {
        var result = CopilotHandleSanitizer.SanitizePrincipalHandle(":::user  name:::");

        Assert.AreEqual("user-name", result);
    }

    [TestMethod]
    public void CapsLength()
    {
        var result = CopilotHandleSanitizer.SanitizePrincipalHandle(new string('a', 500));

        Assert.IsTrue(result.Length <= 96);
    }

    [TestMethod]
    public void PreservesUpnShape()
    {
        var result = CopilotHandleSanitizer.SanitizePrincipalHandle("Eric.Brasher@Vulcan365.com");

        Assert.AreEqual("eric.brasher@vulcan365.com", result);
    }
}
