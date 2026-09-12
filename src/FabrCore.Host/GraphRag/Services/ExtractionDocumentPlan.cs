using System.Text;

namespace FabrCore.Services.GraphRag.Services;

/// <summary>Lossless source windows for extraction, independent of retrieval chunk overlap.</summary>
internal static class ExtractionDocumentPlan
{
    public static IReadOnlyList<string> Split(string content, int sectionSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sectionSize, 2);
        var sections = new List<string>();
        for (var start = 0; start < content.Length;)
        {
            var end = Math.Min(content.Length, start + sectionSize);
            if (end < content.Length)
            {
                // Prefer a paragraph/line boundary in the latter half of the window.
                var boundary = content.LastIndexOf('\n', end - 1, Math.Max(1, (end - start) / 2));
                if (boundary >= start) end = boundary + 1;
                else
                {
                    var space = content.LastIndexOf(' ', end - 1, Math.Max(1, (end - start) / 2));
                    if (space >= start) end = space + 1;
                }
                if (char.IsHighSurrogate(content[end - 1]) && char.IsLowSurrogate(content[end])) end--;
            }
            sections.Add(content[start..end]);
            start = end;
        }
        return sections;
    }

    // Sample the entire document, rather than making the first section its classifier.
    // This is bounded context for classification only; extraction still sees every character.
    public static string ClassificationEvidence(IReadOnlyList<string> sections, int maxChars = 8_000)
    {
        if (sections.Count == 0) return "";
        var selectedCount = Math.Min(sections.Count, 32);
        var perSection = Math.Max(1, maxChars / selectedCount);
        var evidence = new StringBuilder();
        for (var i = 0; i < selectedCount; i++)
        {
            var index = selectedCount == 1 ? 0 : (int)((long)i * (sections.Count - 1) / (selectedCount - 1));
            var section = sections[index];
            evidence.AppendLine($"Source section {index + 1}:");
            evidence.AppendLine(section[..Math.Min(perSection, section.Length)]);
        }
        return evidence.ToString();
    }
}
