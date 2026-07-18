using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Serialization;

namespace Viset.Serialization;

public static class CaptureTomlModels
{
    public static CaptureTomlModel Deserialize(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var model =
            TomlSerializer.Deserialize(source, TomlModelContext.Default.CaptureTomlModel)
            ?? throw new InvalidOperationException("Tomlyn returned no capture v1 model.");

        RejectUnmapped(model.Unmapped, "capture");

        foreach (var (name, device) in model.Devices)
        {
            var devicePath = $"devices.{name}";
            RejectUnmapped(device.Unmapped, devicePath);
            RejectUnmapped(device.Viewport.Unmapped, $"{devicePath}.viewport");

            if (device.Frame is not null)
            {
                RejectUnmapped(device.Frame.Unmapped, $"{devicePath}.frame");
            }
        }

        if (model.Webp is not null)
        {
            RejectUnmapped(model.Webp.Unmapped, "webp");
        }

        return model;
    }

    private static void RejectUnmapped(TomlTable values, string path)
    {
        if (values.Count == 0)
        {
            return;
        }

        var name = values.Keys.First();
        throw new InvalidOperationException($"Unknown TOML property '{path}.{name}'.");
    }
}

public sealed class CaptureTomlModel
{
    [TomlRequired]
    public long? Version { get; set; }

    public string OutputRoot { get; set; } = string.Empty;

    [TomlRequired]
    public string Output { get; set; } = null!;

    public string Frame { get; set; } = string.Empty;

    public long? FramesPerSecond { get; set; }

    public WebpTomlModel? Webp { get; set; }

    public List<string> BrowserArguments { get; set; } = [];

    [TomlRequired]
    public Dictionary<string, DeviceTomlModel> Devices { get; set; } = new(StringComparer.Ordinal);

    public TomlTable Matrix { get; set; } = [];

    public TomlTable Data { get; set; } = [];

    [TomlExtensionData]
    public TomlTable Unmapped { get; set; } = [];
}

public sealed class WebpTomlModel
{
    public string Source { get; set; } = string.Empty;

    public long? SourceQuality { get; set; }

    public string Encoder { get; set; } = string.Empty;

    public string Pipeline { get; set; } = string.Empty;

    public string Mode { get; set; } = string.Empty;

    public long? Method { get; set; }

    public double? Quality { get; set; }

    [TomlExtensionData]
    public TomlTable Unmapped { get; set; } = [];
}

public sealed class DeviceTomlModel
{
    public bool? Mobile { get; set; }

    public bool? Touch { get; set; }

    public double? DeviceScale { get; set; }

    [TomlRequired]
    public DimensionsTomlModel Viewport { get; set; } = null!;

    public DimensionsTomlModel? Frame { get; set; }

    [TomlExtensionData]
    public TomlTable Unmapped { get; set; } = [];
}

public sealed class DimensionsTomlModel
{
    [TomlRequired]
    public long? Width { get; set; }

    [TomlRequired]
    public long? Height { get; set; }

    [TomlExtensionData]
    public TomlTable Unmapped { get; set; } = [];
}
