namespace FabrCore.Surface.CommandCenter;

public sealed record SurfaceUploadedFile(
    string FileId,
    string FileName,
    string? ContentType);
