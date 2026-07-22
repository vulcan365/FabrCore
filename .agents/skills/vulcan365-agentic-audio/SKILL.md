---
name: vulcan365-agentic-audio
description: >
  Integrate with the Vulcan365 Audio API for splitting audio files into chunks
  for transcription processing. Use when building audio chunking, transcription
  pipelines, or meeting recording processing. Triggers on: "Vulcan365", "audio split",
  "audio chunking", "audio.vulcan365.ai", "/split endpoint", "chunk audio",
  "audio preparation", or any audio file processing using the Vulcan365 service.
allowed-tools: "Bash(curl:*) Bash(dotnet:*)"
metadata:
  author: Synod
  version: 1.1.0
  documentation: https://audio.vulcan365.ai
---

# Vulcan365 Agentic Audio API (v1.1.0)

Audio file splitting service using FFmpeg. **Async job-based API** — submit a split job, poll for status, download when complete.

## Base URL

`https://audio.vulcan365.ai`

## Authentication

None required.

## Endpoints

### POST /split

Submits an audio file for splitting. Returns a job ID immediately (does NOT return the ZIP directly).

**Content-Type:** `multipart/form-data`

**Query Parameters:**

| Parameter | Type | Default | Range | Description |
|-----------|------|---------|-------|-------------|
| `chunk_size_mb` | float | 25.0 | 1.0-100.0 | Target chunk size in MB |
| `codec` | string | (stream copy) | mp3, aac, opus, vorbis, flac, wav, copy | Output codec |

**Form Data:**

| Field | Type | Description |
|-------|------|-------------|
| `file` | binary | The audio file to split |

**Supported Input Formats:** webm, m4a, mp3, wav, ogg, flac

**Response (200):**
```json
{
  "job_id": "abc123",
  "status": "processing"
}
```

**Example:**
```bash
curl -X POST "https://audio.vulcan365.ai/split?chunk_size_mb=20&codec=mp3" \
  -F "file=@recording.m4a"
# Returns: {"job_id": "abc123", "status": "processing"}
```

### GET /jobs/{job_id}

Check the status of a split job.

**Response — Processing:**
```json
{
  "job_id": "abc123",
  "status": "processing"
}
```

**Response — Complete:**
```json
{
  "job_id": "abc123",
  "status": "complete",
  "chunk_count": 5,
  "zip_size": 12345678,
  "download_url": "/jobs/abc123/download"
}
```

**Response — Failed:**
```json
{
  "job_id": "abc123",
  "status": "failed",
  "error": "Unsupported audio format"
}
```

### GET /jobs/{job_id}/download

Download the ZIP archive of split chunks. Only available when job status is `complete`. Files are cleaned up after **1 hour**.

**Response:** `application/zip` containing numbered audio chunks (e.g., `chunk_000.mp3`, `chunk_001.mp3`, ...)

### GET /health

Readiness probe. Returns 200.

### GET /alive

Liveness probe. Returns 200.

## .NET Integration Pattern

### HttpClient Registration

```csharp
builder.Services.AddHttpClient("vulcan365-audio", client =>
{
    client.BaseAddress = new Uri("https://audio.vulcan365.ai");
    client.Timeout = TimeSpan.FromMinutes(10);
});
```

### Complete Async Split + Download Pattern

```csharp
using System.IO.Compression;
using System.Text.Json;

public record AudioChunk(int Index, byte[] Data, string ContentType, string FileName);

public async Task<List<AudioChunk>> SplitAudioAsync(
    IHttpClientFactory httpFactory,
    byte[] audioData,
    string fileName,
    int chunkSizeMb = 20,
    string codec = "mp3",
    CancellationToken ct = default)
{
    var client = httpFactory.CreateClient("vulcan365-audio");

    // Step 1: Submit split job
    using var content = new MultipartFormDataContent();
    content.Add(new ByteArrayContent(audioData), "file", fileName);

    var submitResponse = await client.PostAsync(
        $"/split?chunk_size_mb={chunkSizeMb}&codec={codec}", content, ct);
    submitResponse.EnsureSuccessStatusCode();

    var submitJson = await submitResponse.Content.ReadAsStringAsync(ct);
    var jobId = JsonDocument.Parse(submitJson).RootElement.GetProperty("job_id").GetString()!;

    // Step 2: Poll for completion
    while (true)
    {
        ct.ThrowIfCancellationRequested();
        await Task.Delay(2000, ct); // Poll every 2 seconds

        var statusResponse = await client.GetAsync($"/jobs/{jobId}", ct);
        statusResponse.EnsureSuccessStatusCode();

        var statusJson = await statusResponse.Content.ReadAsStringAsync(ct);
        var statusDoc = JsonDocument.Parse(statusJson);
        var status = statusDoc.RootElement.GetProperty("status").GetString();

        if (status == "complete")
            break;
        if (status == "failed")
        {
            var error = statusDoc.RootElement.TryGetProperty("error", out var errProp)
                ? errProp.GetString() : "Unknown error";
            throw new InvalidOperationException($"Audio split failed: {error}");
        }
        // status == "processing" → continue polling
    }

    // Step 3: Download ZIP
    var downloadResponse = await client.GetAsync($"/jobs/{jobId}/download", ct);
    downloadResponse.EnsureSuccessStatusCode();

    // Step 4: Extract chunks
    var chunks = new List<AudioChunk>();
    using var zipStream = await downloadResponse.Content.ReadAsStreamAsync(ct);
    using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

    foreach (var entry in archive.Entries.OrderBy(e => e.Name))
    {
        using var entryStream = entry.Open();
        using var ms = new MemoryStream();
        await entryStream.CopyToAsync(ms, ct);
        chunks.Add(new AudioChunk(chunks.Count, ms.ToArray(), $"audio/{codec}", entry.Name));
    }

    return chunks;
}
```

## Error Handling

| Status | Meaning |
|--------|---------|
| 200 | Success |
| 422 | Validation error (invalid parameters) |
| 502 | Service unavailable (origin server down) |

## Best Practices

1. **Chunk size**: Use 5MB for gpt-4o-mini-transcribe (1500 second / 25 minute duration limit per chunk; 5MB MP3 ≈ 5-8 min which stays safely under)
2. **Codec**: Use `mp3` for best transcription compatibility
3. **Polling interval**: 2-3 seconds is reasonable; large files may take minutes
4. **Timeout**: The job stays available for 1 hour after completion
5. **Fallback**: If file is already under 25MB, skip chunking and transcribe directly
