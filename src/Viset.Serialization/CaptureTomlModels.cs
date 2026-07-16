using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;

namespace Viset.Serialization;

public static class CaptureTomlModels
{
    public static CaptureTomlModel Deserialize(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return TomlSerializer.Deserialize(source, TomlModelContext.Default.CaptureTomlModel)
            ?? throw new InvalidOperationException("Tomlyn returned no capture v1 model.");
    }
}

public sealed class CaptureTomlModel
{
    [TomlRequired]
    public long? Version { get; set; }

    public string OutputRoot { get; set; } = string.Empty;

    [TomlRequired]
    public string Output { get; set; } = null!;

    public string Device { get; set; } = string.Empty;

    public string Frame { get; set; } = string.Empty;

    public long? FramesPerSecond { get; set; }

    public List<string> BrowserArguments { get; set; } = [];

    [TomlRequired]
    public Dictionary<string, DeviceTomlModel> Devices { get; set; } = new(StringComparer.Ordinal);

    public TomlTable Matrix { get; set; } = [];

    public TomlTable Data { get; set; } = [];
}

public sealed class DeviceTomlModel
{
    public bool? Mobile { get; set; }

    public bool? Touch { get; set; }

    public double? DeviceScale { get; set; }

    [TomlRequired]
    public DimensionsTomlModel Viewport { get; set; } = null!;

    public DimensionsTomlModel? Frame { get; set; }
}

public sealed class DimensionsTomlModel
{
    [TomlRequired]
    public long? Width { get; set; }

    [TomlRequired]
    public long? Height { get; set; }
}
