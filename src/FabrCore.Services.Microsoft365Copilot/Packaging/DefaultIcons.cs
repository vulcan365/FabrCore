namespace FabrCore.Services.Microsoft365Copilot;

/// <summary>
/// Embedded placeholder icons used when the host does not supply its own, so a valid app package
/// can be produced with zero extra assets. Replace via
/// <see cref="CopilotManifestOptions.ColorIconPath"/> / <see cref="CopilotManifestOptions.OutlineIconPath"/>
/// before publishing to production.
/// </summary>
internal static class DefaultIcons
{
    /// <summary>192x192 RGBA PNG — indigo gradient with a light diamond.</summary>
    public static byte[] Color => Convert.FromBase64String(ColorBase64);

    /// <summary>32x32 RGBA PNG — white diamond on transparent background.</summary>
    public static byte[] Outline => Convert.FromBase64String(OutlineBase64);

    private const string ColorBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAMAAAADACAYAAABS3GwHAAAEnklEQVR42u3TyZYUVRSF4ftmtqh0BRQotthggwr2vb6eqCCo6KMwdK3wTBKV1KrKJrLixv4G31kxvrH/dvri9wOkah4BAYAAIDCAM3UglQAQAAgAEgM4WwdSCYDwAPbrA0IJgOwA9upAqgrg5gCpBIAAIDaAc3UglQAQAAgAEgM4f+HmAKkEgABAAJAYwIU6kEoApAfwwwCpBEB2APt1IFXbP18fEEoACABiA7hYB1IJAAGAACAxgEt1IJUAEADEBvDsuR8HSCUABMDxuX//T+8ggNzxL3iPYwrguTrs3j/Hv+Bddk8AExm/CAQQP34RCCB+/CLYcQCX6zC+Vca/4N3G1y7v1QejWmf8DyLwfqMSwITHLwIBxI9fBCMH8PzeTwPbt83xL3jX7RNAJ+MXgQDixy8CAcSPXwRbDuCFOmxul+Nf8O6bE0Cn4xeBAOLHL4JtBHC2PljLFMb/IAL/Yy3txTqsbkrjX/BfVieAmYxfBAKIH78IBBA/fhGsGMBLdThcT+Nf8N8OVwHcGjhYj+P/OwL/7yACmPH4RSCA+PGL4JAAXj5za2DZnMa/4L8uE0DI+EUggPjxi0AA8eMXwUMBvFKHrPEv+O8CiB2/CAQQP34RhAdg/CJoV+okMvpliTtoxk9yBO3K6dtDEiM/QgRBe2iv1klh3EeXsolm/CRH0Iyf5Aia8ZMcQXutzlwZ7/bMdSPN+EmOoBk/yRE04yc5gvb6qdvDXBjn7sxlM834SY6gGT/JETTjJzmC9sapn4deGd909LqhZvwkR9CMn+QImvGTHEG7WqcXxtWPXjbVjJ/kCNrVk/UxccbUcQQT31YXAYjA+EcL4M06vTCqfvSyqa4CEIHxxwcgAuOPD0AExr+1AN46eWfoldFNR68b6joAERh/fAAiMP74AERg/GsH8PYzd4a5MMrdmctmZhWACIw/PgARGH98ACIw/iMH8E6duTLa7ZnrRmYdgAiMPz4AERh/fAAiMP7/DeBanRRGfXQpm2jXnr47JDHuI4w/aA9xAYjA+P8VwLt1Ehn7ssQdxAYgAuOPD0AE2eMXgAji/317rw6ZEfjvAoiNwP8WQGwE/rMAYiPwfx8K4P2n7g4sm+P4/ddlAgiJwP8UQGwE/uOBAfwycLC+x+//HaRdr8Phehy//3Y4Acw0Av9LALER+E8CiI3A/1kxgBt1WN0Ux++/rK7dOFEfrGVS4/c/1iKAGUTgPwggNgLvv2EAH9Rhc8cxfu++OQF0GoH3FkBsBN5ZALEReN8tB/DhiV8Htm+M8XvX7RNAJxF4TwHERuAdxwzgyfpgVBuN3/uNqn1Uh/GtM37vNj4BTDQC7yWA2Ai8kwBiI/A+Ow7g4zrs3n+N37vsngAmEoH3EEBsBN5BAHA8AXzyxG8DpBIAAoDYAD6tA6kEgABAAJAYwGd1IJUAEADkBvB4fUCo9nkdSCUA0gO4N0AqAZAdwBd1IJUAEAAIABID+PKxewOkEgACAAFAYgBf1YFUAkAAEBzA7wOkal/XgVQCIDyAR+sDQgmA7AC+qQOpBIAAQACQGMC3dSCVABAAxAbw3SN/DJDqL7mheO587nvbAAAAAElFTkSuQmCC";

    private const string OutlineBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAAYElEQVR42u2WQQ4AIAjD/P+npxevJkZxxdAPdCEKa60osqOBVT6xyp+H0AKrPDyENrDKr4fQAVb5cQhdxCrfDqFAckwA8QYQvwCxBxCbEHELENcQ0QcQjQjRCRGtuPiCDooKIBk341brAAAAAElFTkSuQmCC";
}
