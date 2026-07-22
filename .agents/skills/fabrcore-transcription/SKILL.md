---
name: fabrcore-transcription
description: >
  Transcribe audio files using Azure OpenAI gpt-4o-mini-transcribe (or gpt-4o-transcribe) via the
  Azure.AI.OpenAI SDK in .NET. Use when building audio transcription, speech-to-text pipelines,
  or processing meeting recordings into text. Triggers on: "transcribe", "transcription",
  "speech-to-text", "audio to text", "gpt-4o-mini-transcribe", "gpt-4o-transcribe", "whisper",
  "AudioClient", "TranscribeAudio", "audio transcription", or any audio-to-text processing.
allowed-tools: "Bash(dotnet:*)"
metadata:
  author: Synod
  version: 1.0.0
---

# Audio Transcription with Azure OpenAI

Transcribe audio files using Azure OpenAI's gpt-4o-mini-transcribe model via the `Azure.AI.OpenAI` and `OpenAI` .NET SDKs.

## Model Details

| Model | File Limit | Duration Limit | Best For |
|-------|-----------|---------------|----------|
| `gpt-4o-mini-transcribe` | 25 MB | **1500 seconds (25 min)** | Fast, cost-effective transcription |
| `gpt-4o-transcribe` | 25 MB | **1500 seconds (25 min)** | Higher accuracy transcription |
| `whisper` (legacy) | 25 MB | No duration limit | Backward compatibility |

**CRITICAL:** The gpt-4o transcribe models have a **25-minute duration limit per request**, not just a file size limit. When chunking audio, use **5MB chunks** (not 20-25MB) to keep each chunk under 25 minutes regardless of bitrate.

**Supported audio formats:** mp3, mp4, mpeg, mpga, m4a, wav, webm

## NuGet Package

```xml
<PackageReference Include="Azure.AI.OpenAI" Version="2.*" />
```

This transitively brings in the `OpenAI` package which contains the `AudioClient` and transcription types.

## Key Types and Namespaces

```csharp
using Azure.AI.OpenAI;          // AzureOpenAIClient
using OpenAI.Audio;              // AudioClient, AudioTranscription, AudioTranscriptionOptions
using System.ClientModel;        // ClientResult<T>
```

## Creating the Audio Client

```csharp
using Azure;
using Azure.AI.OpenAI;
using OpenAI.Audio;

// Create the Azure OpenAI client
var azureClient = new AzureOpenAIClient(
    new Uri("https://your-resource.openai.azure.com/"),
    new AzureKeyCredential("your-api-key"));

// Get the audio client for your deployment
AudioClient audioClient = azureClient.GetAudioClient("gpt-4o-mini-transcribe");
```

**Important:** The deployment name passed to `GetAudioClient()` must match your Azure AI deployment name (e.g., `"gpt-4o-mini-transcribe"`).

## Transcribing Audio

### From a file path

```csharp
AudioTranscription transcription = await audioClient.TranscribeAudioAsync(
    "/path/to/audio.mp3");

Console.WriteLine(transcription.Text);
```

### From a stream (recommended for chunked processing)

```csharp
using var audioStream = File.OpenRead("/path/to/audio.mp3");

var options = new AudioTranscriptionOptions
{
    Filename = "audio.mp3",
    ResponseFormat = AudioTranscriptionFormat.Verbose,
    Temperature = 0f,
    Language = "en"
};

AudioTranscription transcription = await audioClient.TranscribeAudioAsync(
    audioStream, "audio.mp3", options);

Console.WriteLine(transcription.Text);
Console.WriteLine($"Duration: {transcription.Duration}");
Console.WriteLine($"Language: {transcription.Language}");
```

### From a byte array

```csharp
byte[] audioData = /* from Vulcan365 chunk or file read */;

using var stream = new MemoryStream(audioData);
AudioTranscription transcription = await audioClient.TranscribeAudioAsync(
    stream, "chunk.mp3");

string text = transcription.Text;
```

## AudioTranscriptionOptions

```csharp
var options = new AudioTranscriptionOptions
{
    // Response format: Simple, Verbose, Srt, Vtt, Text
    ResponseFormat = AudioTranscriptionFormat.Verbose,

    // Temperature: 0 = deterministic, 1 = creative (default: 0)
    Temperature = 0f,

    // ISO-639-1 language code (optional, auto-detected if omitted)
    Language = "en",

    // Filename hint for the audio format
    Filename = "recording.mp3",

    // Optional prompt to guide the transcription style/vocabulary
    // Useful for technical terms, proper nouns, acronyms
    Prompt = "This is a board meeting discussing fiscal year budgets and EBITDA margins."
};
```

## AudioTranscription Response

```csharp
AudioTranscription transcription = await audioClient.TranscribeAudioAsync(stream, filename, options);

// Always available
string text = transcription.Text;        // The full transcription text

// Available with Verbose format
TimeSpan? duration = transcription.Duration;  // Audio duration
string? language = transcription.Language;    // Detected language

// Segments (with Verbose format)
foreach (var segment in transcription.Segments)
{
    Console.WriteLine($"[{segment.StartTime} - {segment.EndTime}] {segment.Text}");
}
```

## Response Format Options

| Format | Type | Use Case |
|--------|------|----------|
| `AudioTranscriptionFormat.Simple` | JSON `{ "text": "..." }` | Default, just text |
| `AudioTranscriptionFormat.Verbose` | JSON with segments, duration, language | Detailed analysis |
| `AudioTranscriptionFormat.Text` | Plain text string | Simplest output |
| `AudioTranscriptionFormat.Srt` | SubRip subtitle format | Subtitle generation |
| `AudioTranscriptionFormat.Vtt` | WebVTT format | Web video subtitles |

## Complete Transcription Pipeline

This pattern processes multiple audio chunks (from Vulcan365 audio splitting) and combines them into a single transcript:

```csharp
using Azure;
using Azure.AI.OpenAI;
using OpenAI.Audio;
using System.Text;

public class AudioTranscriptionService
{
    private readonly AudioClient _audioClient;

    public AudioTranscriptionService(string endpoint, string apiKey, string deploymentName)
    {
        var azureClient = new AzureOpenAIClient(
            new Uri(endpoint),
            new AzureKeyCredential(apiKey));
        _audioClient = azureClient.GetAudioClient(deploymentName);
    }

    /// <summary>
    /// Transcribe a single audio file (must be under 25MB).
    /// </summary>
    public async Task<string> TranscribeAsync(
        byte[] audioData,
        string fileName,
        string? language = null,
        string? prompt = null,
        CancellationToken ct = default)
    {
        using var stream = new MemoryStream(audioData);

        var options = new AudioTranscriptionOptions
        {
            Filename = fileName,
            ResponseFormat = AudioTranscriptionFormat.Verbose,
            Temperature = 0f
        };

        if (!string.IsNullOrEmpty(language))
            options.Language = language;
        if (!string.IsNullOrEmpty(prompt))
            options.Prompt = prompt;

        var result = await _audioClient.TranscribeAudioAsync(stream, fileName, options, ct);
        return result.Text;
    }

    /// <summary>
    /// Transcribe multiple audio chunks in order and combine into a single transcript.
    /// Each chunk must be under 25MB.
    /// </summary>
    public async Task<string> TranscribeChunksAsync(
        IReadOnlyList<AudioChunk> chunks,
        string? language = null,
        string? prompt = null,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder();

        for (int i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];

            var text = await TranscribeWithRetryAsync(
                chunk.Data,
                chunk.FileName,
                language,
                prompt,
                maxRetries: 3,
                ct: ct);

            if (!string.IsNullOrWhiteSpace(text))
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(text);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Transcribe with exponential backoff retry.
    /// </summary>
    private async Task<string> TranscribeWithRetryAsync(
        byte[] audioData,
        string fileName,
        string? language,
        string? prompt,
        int maxRetries = 3,
        CancellationToken ct = default)
    {
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await TranscribeAsync(audioData, fileName, language, prompt, ct);
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                var delay = TimeSpan.FromMilliseconds(
                    1000 * Math.Pow(2, attempt) + Random.Shared.Next(0, 500));
                await Task.Delay(delay, ct);
            }
        }

        throw new InvalidOperationException("Transcription failed after all retries.");
    }
}

public record AudioChunk(int Index, byte[] Data, string ContentType, string FileName);
```

## DI Registration Pattern

```csharp
// In Program.cs or service registration
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var endpoint = config["AzureOpenAI:Endpoint"]!;
    var apiKey = config["AzureOpenAI:ApiKey"]!;
    var deployment = config["AzureOpenAI:TranscriptionDeployment"] ?? "gpt-4o-mini-transcribe";

    var azureClient = new AzureOpenAIClient(
        new Uri(endpoint),
        new AzureKeyCredential(apiKey));

    return azureClient.GetAudioClient(deployment);
});
```

```json
// appsettings.json
{
  "AzureOpenAI": {
    "Endpoint": "https://your-resource.openai.azure.com/",
    "ApiKey": "your-api-key",
    "TranscriptionDeployment": "gpt-4o-mini-transcribe"
  }
}
```

## REST API Reference

If calling the API directly via HttpClient (without the SDK):

```
POST https://{resource}.openai.azure.com/openai/deployments/{deployment}/audio/transcriptions?api-version=2024-02-01

Headers:
  api-key: {api-key}
  Content-Type: multipart/form-data

Form Data:
  file: (binary audio data)
  response_format: verbose_json  (optional)
  temperature: 0                 (optional)
  language: en                   (optional)
  prompt: "context hint"         (optional)
```

**Response (verbose_json):**
```json
{
  "text": "Full transcription text here...",
  "language": "en",
  "duration": 125.5,
  "segments": [
    {
      "id": 0,
      "seek": 0,
      "start": 0.0,
      "end": 4.2,
      "text": "Welcome to the board meeting.",
      "avg_logprob": -0.15,
      "compression_ratio": 1.2,
      "no_speech_prob": 0.001
    }
  ]
}
```

## Error Handling

| HTTP Status | Meaning | Action |
|-------------|---------|--------|
| 200 | Success | Parse response |
| 400 | Invalid file format or parameters | Check file format and size |
| 413 | File too large (>25MB) | Split into smaller chunks |
| 429 | Rate limited | Retry with backoff |
| 500 | Server error | Retry with backoff |

## Best Practices

1. **Chunk size:** Use 20-24 MB chunks to stay safely under the 25 MB limit
2. **File format:** MP3 gives the best size/quality ratio for transcription
3. **Language hint:** Always provide `Language` if known - improves accuracy
4. **Prompt context:** Use `Prompt` for domain-specific vocabulary (proper nouns, acronyms, technical terms)
5. **Temperature:** Keep at 0 for transcription (deterministic/accurate)
6. **Retry strategy:** Use exponential backoff with jitter for 429/500 errors
7. **Ordering:** When transcribing chunks, process in order and concatenate with newlines
8. **Verbose format:** Use `Verbose` format to get duration and language detection metadata
