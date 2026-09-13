using System.Security.Cryptography;
using System.Text;

namespace FabrCore.Services.GraphRag.Services;

internal static class SourceSpanPlan
{
    public const string Guidance = """
        Each source section below has a source-span ID. For every relationship,
        add "sourceIds": ["ID"] listing one or more supplied spans supporting it.
        Copy IDs exactly. Do not generate quotes, offsets, or new IDs. An ID only
        identifies evidence; its text must support the claimed relation and direction.
        Include both endpoint entities with exactly matching names.
        """;

    public static string Id(string text) => "s_" + Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    public static string[] Label(IReadOnlyList<string> spans) => spans
        .Select(text => $"[SOURCE-SPAN {Id(text)}]\n{text}\n[END SOURCE-SPAN]").ToArray();

    public static bool ValidIds(IEnumerable<string>? ids, ISet<string> allowed)
    {
        var values = ids?.ToArray();
        return values is { Length: > 0 } && values.All(id => allowed.Contains(id));
    }
}
