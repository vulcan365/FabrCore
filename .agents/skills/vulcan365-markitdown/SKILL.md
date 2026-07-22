# Vulcan365 MarkItDown API

Convert documents to Markdown using the Vulcan365 MarkItDown service at `https://markdown.vulcan365.ai`.

Use when building document-to-markdown conversion, file parsing pipelines, or extracting text from PDFs/images/Office documents. Triggers on: "markitdown", "convert to markdown", "document to markdown", "extract text from PDF", "parse document", "document intelligence", "convert-doc-intel", "markdown.vulcan365.ai", "file to markdown", or any document-to-text conversion using the Vulcan365 service.

## Base URL

```
https://markdown.vulcan365.ai
```

## Endpoints

### GET / — Status & Info

Returns API info and supported formats.

```csharp
var response = await httpClient.GetAsync("https://markdown.vulcan365.ai/");
// Returns: service name, supported formats array, max file size (MB), endpoints
```

### POST /convert — Basic Conversion

Convert a file to Markdown using MarkItDown.

- **Content-Type:** `multipart/form-data`
- **Parameter:** `file` (required) — the file to convert

```csharp
using var content = new MultipartFormDataContent();
content.Add(new ByteArrayContent(fileBytes), "file", fileName);
var response = await httpClient.PostAsync("https://markdown.vulcan365.ai/convert", content);
var markdown = await response.Content.ReadAsStringAsync();
```

**Responses:** 200 (markdown text), 400 (invalid/unsupported), 413 (too large), 500 (failed)

### POST /convert-doc-intel — Document Intelligence Conversion

Convert a document to Markdown using Azure Document Intelligence with natural reading order. Best for complex layouts, tables, and structured documents.

- **Content-Type:** `multipart/form-data`
- **Parameter:** `file` (required)
- **Supported formats:** PDF, HTML, JPG, PNG, BMP, TIFF, TIF, HEIF, DOCX, XLSX, PPTX, TXT, MD

```csharp
using var content = new MultipartFormDataContent();
content.Add(new ByteArrayContent(fileBytes), "file", fileName);
var response = await httpClient.PostAsync("https://markdown.vulcan365.ai/convert-doc-intel", content);
var markdown = await response.Content.ReadAsStringAsync();
```

**Responses:** 200 (markdown text), 400 (invalid), 500 (failed), 503 (service unconfigured)

### POST /convert-audio — Audio Transcription

Convert audio files to text/markdown using Azure Speech Service.

- **Content-Type:** `multipart/form-data`
- **Parameter:** `file` (required)
- **Supported formats:** WAV, MP3, OGG, FLAC, WMA, AAC, WebM, AMR, M4A
- Files exceeding fast transcription limits (300MB/2hr) automatically fall back to batch transcription.

```csharp
using var content = new MultipartFormDataContent();
content.Add(new ByteArrayContent(audioBytes), "file", fileName);
var response = await httpClient.PostAsync("https://markdown.vulcan365.ai/convert-audio", content);
var markdown = await response.Content.ReadAsStringAsync();
```

**Responses:** 200 (markdown text), 400 (invalid), 500 (failed), 503 (service unconfigured)

### POST /convert-url — URL Conversion

Fetch a URL and convert its content to Markdown.

- **Content-Type:** `application/json`
- **Body:** `{ "url": "https://example.com/page" }`

```csharp
var payload = new { url = "https://example.com/page" };
var response = await httpClient.PostAsJsonAsync("https://markdown.vulcan365.ai/convert-url", payload);
var markdown = await response.Content.ReadAsStringAsync();
```

**Responses:** 200 (markdown text), 400 (invalid), 500 (failed)

### GET /health — Health Check

```csharp
var response = await httpClient.GetAsync("https://markdown.vulcan365.ai/health");
// Returns: { service, status: "healthy", timestamp }
```

## Usage Pattern in FabrCore Agents

```csharp
private async Task<string> ConvertToMarkdownAsync(byte[] fileBytes, string fileName)
{
    var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
    var client = httpClientFactory.CreateClient();

    using var content = new MultipartFormDataContent();
    content.Add(new ByteArrayContent(fileBytes), "file", fileName);

    var response = await client.PostAsync(
        "https://markdown.vulcan365.ai/convert-doc-intel", content);
    response.EnsureSuccessStatusCode();

    return await response.Content.ReadAsStringAsync();
}
```

## Notes

- All conversion endpoints return `text/markdown` content type on success
- The `/convert-doc-intel` endpoint provides the best results for structured documents (tables, forms, complex layouts)
- Use `/convert` for simpler documents or when Azure Document Intelligence is not needed
- File size limits apply — check the root endpoint for current max file size
