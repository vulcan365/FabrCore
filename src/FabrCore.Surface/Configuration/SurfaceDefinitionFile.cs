namespace FabrCore.Surface.Configuration;

public sealed class SurfaceDefinitionFile
{
    public List<SurfaceDefinition> Surfaces { get; set; } = [];

    public List<SurfaceDefinition> Definitions { get; set; } = [];

    public IEnumerable<SurfaceDefinition> GetDefinitions()
        => Surfaces.Count > 0 ? Surfaces : Definitions;
}
