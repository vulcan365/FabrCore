namespace Fabr.Sdk.Memory;

/// <summary>
/// Splits text into overlapping chunks by character count, respecting paragraph boundaries.
/// </summary>
public static class TextChunker
{
    /// <summary>
    /// Split text into overlapping chunks, breaking at paragraph boundaries.
    /// </summary>
    /// <param name="text">The text to chunk.</param>
    /// <param name="chunkSize">Target chunk size in characters.</param>
    /// <param name="overlap">Number of characters to overlap between chunks.</param>
    public static List<string> ChunkText(string text, int chunkSize = 500, int overlap = 64)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        if (text.Length <= chunkSize)
            return [text];

        var chunks = new List<string>();
        var paragraphs = text.Split(["\n\n", "\r\n\r\n"], StringSplitOptions.RemoveEmptyEntries);

        var currentChunk = new List<string>();
        int currentLength = 0;

        foreach (var paragraph in paragraphs)
        {
            var trimmed = paragraph.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            if (currentLength + trimmed.Length > chunkSize && currentChunk.Count > 0)
            {
                // Emit current chunk
                chunks.Add(string.Join("\n\n", currentChunk));

                // Keep overlap: include the last paragraph(s) that fit within the overlap window
                var overlapChunk = new List<string>();
                int overlapLength = 0;
                for (int i = currentChunk.Count - 1; i >= 0; i--)
                {
                    if (overlapLength + currentChunk[i].Length > overlap)
                        break;
                    overlapChunk.Insert(0, currentChunk[i]);
                    overlapLength += currentChunk[i].Length;
                }

                currentChunk = overlapChunk;
                currentLength = overlapLength;
            }

            currentChunk.Add(trimmed);
            currentLength += trimmed.Length;
        }

        if (currentChunk.Count > 0)
        {
            chunks.Add(string.Join("\n\n", currentChunk));
        }

        return chunks;
    }
}
