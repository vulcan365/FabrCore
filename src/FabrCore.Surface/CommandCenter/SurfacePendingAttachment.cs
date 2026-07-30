namespace FabrCore.Surface.CommandCenter;

public sealed class SurfacePendingAttachment
{
    public SurfacePendingAttachment(string name, long size, string? contentType)
    {
        Name = name;
        Size = size;
        ContentType = contentType;
    }

    public string Id { get; } = Guid.NewGuid().ToString("N");

    public string Name { get; }

    public long Size { get; }

    public string? ContentType { get; }

    public string? FileId { get; set; }

    public string? Error { get; set; }

    public bool IsUploading { get; set; } = true;

    public bool DeleteWhenUploaded { get; set; }

    public bool IsReady => !IsUploading && string.IsNullOrWhiteSpace(Error) && !string.IsNullOrWhiteSpace(FileId);
}
