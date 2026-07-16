using Tomlyn;
using Tomlyn.Serialization;

namespace Viset.Serialization;

public static class OutputTomlModels
{
    public static string SerializeMarker(OutputMarkerTomlModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return TomlSerializer.Serialize(model, TomlModelContext.Default.OutputMarkerTomlModel);
    }

    public static OutputMarkerTomlModel DeserializeMarker(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return TomlSerializer.Deserialize(source, TomlModelContext.Default.OutputMarkerTomlModel)
            ?? throw new InvalidOperationException("Tomlyn returned no output marker model.");
    }

    public static string SerializeManifest(OutputManifestTomlModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return TomlSerializer.Serialize(model, TomlModelContext.Default.OutputManifestTomlModel);
    }

    public static OutputManifestTomlModel DeserializeManifest(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return TomlSerializer.Deserialize(source, TomlModelContext.Default.OutputManifestTomlModel)
            ?? throw new InvalidOperationException("Tomlyn returned no output manifest model.");
    }
}

public sealed class OutputMarkerTomlModel
{
    [TomlRequired]
    public long? Version { get; set; }

    [TomlRequired]
    public string Owner { get; set; } = null!;

    [TomlRequired]
    public string Manifest { get; set; } = null!;
}

public sealed class OutputManifestTomlModel
{
    [TomlRequired]
    public long? Version { get; set; }

    [TomlRequired]
    public string Owner { get; set; } = null!;

    [TomlRequired]
    public OutputToolTomlModel Tool { get; set; } = null!;

    [TomlRequired]
    public OutputBrowserTomlModel Browser { get; set; } = null!;

    public List<OutputFileTomlModel> Files { get; set; } = [];
}

public sealed class OutputToolTomlModel
{
    [TomlRequired]
    public string Name { get; set; } = null!;

    [TomlRequired]
    public string Version { get; set; } = null!;
}

public sealed class OutputBrowserTomlModel
{
    [TomlRequired]
    public string Version { get; set; } = null!;

    [TomlRequired]
    public string Source { get; set; } = null!;
}

public sealed class OutputFileTomlModel
{
    [TomlRequired]
    public string DefinitionId { get; set; } = null!;

    [TomlRequired]
    public string LogicalName { get; set; } = null!;

    [TomlRequired]
    public string Path { get; set; } = null!;

    [TomlRequired]
    public string Kind { get; set; } = null!;

    [TomlRequired]
    public string Sha256 { get; set; } = null!;

    public List<long> FrameTicksMs { get; set; } = [];
}
