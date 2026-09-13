using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FabrCore.Services.GraphRag.EvalConsole;

internal sealed record CorpusEntry(string FileName, string Url, string Query, string[] ExpectedEntities, string[] EvidenceTerms)
{
    public string? ExtractionInstructions { get; init; }
    public List<FactualEdgeLabel>? ExpectedEdges { get; init; }
}
internal sealed record CorpusDocument(CorpusEntry Entry, string Content, string Sha256, string SourceSha256, int SourceCharacters);

internal static class Corpus
{
    public static async Task<List<CorpusDocument>> LoadAsync(string manifest, string cache, int maxDocuments, int maxChars, CancellationToken ct)
    {
        var entries = JsonSerializer.Deserialize<List<CorpusEntry>>(await File.ReadAllTextAsync(manifest, ct),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidOperationException("Empty corpus manifest.");
        if (entries.Count == 0 || entries.Select(e => e.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count)
            throw new InvalidOperationException("Corpus must contain unique document names.");
        Directory.CreateDirectory(cache);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60), MaxResponseContentBufferSize = 5_000_000 };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FabrCore-GraphRag-Eval/1.0");
        var documents = new List<CorpusDocument>();
        foreach (var entry in entries.Take(maxDocuments))
        {
            var uri = new Uri(entry.Url);
            if (uri.Scheme != "https" || !(uri.AbsolutePath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Corpus URLs must be HTTPS .txt or .md resources.");
            if (Path.GetFileName(entry.FileName) != entry.FileName || string.IsNullOrWhiteSpace(entry.FileName))
                throw new InvalidOperationException("Corpus filenames must be simple file names.");
            var path = Path.Combine(cache, Hash(entry.Url) + ".txt");
            if (!File.Exists(path))
            {
                Console.WriteLine($"Downloading {entry.Url}");
                var downloaded = await http.GetStringAsync(uri, ct);
                if (string.IsNullOrWhiteSpace(downloaded) || downloaded.TrimStart().StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Source {entry.FileName} did not return plain text.");
                await File.WriteAllTextAsync(path, downloaded, ct);
            }
            var source = await File.ReadAllTextAsync(path, ct);
            var length = maxChars == 0 ? source.Length : Math.Min(source.Length, maxChars);
            if (length < source.Length && char.IsHighSurrogate(source[length - 1])) length--;
            var content = source[..length];
            documents.Add(new(entry, content, Hash(content), Hash(source), source.Length));
            Console.WriteLine($"Corpus: {entry.FileName}, {content.Length:N0}/{source.Length:N0} characters");
        }
        return documents;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
