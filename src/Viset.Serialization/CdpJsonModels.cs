using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Viset.Serialization;

public static class CdpJsonModels
{
    public static byte[] SerializeCommand(long id, string method, CdpEmptyParameters parameters) =>
        SerializeCommand(id, method, parameters, CdpJsonContext.Default.CdpEmptyParameters);

    public static byte[] SerializeCommand(
        long id,
        string method,
        CdpNavigateParameters parameters
    ) => SerializeCommand(id, method, parameters, CdpJsonContext.Default.CdpNavigateParameters);

    public static byte[] SerializeCommand(
        long id,
        string method,
        CdpEvaluateParameters parameters
    ) => SerializeCommand(id, method, parameters, CdpJsonContext.Default.CdpEvaluateParameters);

    public static byte[] SerializeCommand(
        long id,
        string method,
        CdpDeviceMetricsParameters parameters
    ) =>
        SerializeCommand(id, method, parameters, CdpJsonContext.Default.CdpDeviceMetricsParameters);

    public static byte[] SerializeCommand(
        long id,
        string method,
        CdpTouchEmulationParameters parameters
    ) =>
        SerializeCommand(
            id,
            method,
            parameters,
            CdpJsonContext.Default.CdpTouchEmulationParameters
        );

    public static byte[] SerializeCommand(
        long id,
        string method,
        CdpDispatchTouchEventParameters parameters
    ) =>
        SerializeCommand(
            id,
            method,
            parameters,
            CdpJsonContext.Default.CdpDispatchTouchEventParameters
        );

    public static byte[] SerializeCommand(
        long id,
        string method,
        CdpBackgroundParameters parameters
    ) => SerializeCommand(id, method, parameters, CdpJsonContext.Default.CdpBackgroundParameters);

    public static byte[] SerializeCommand(
        long id,
        string method,
        CdpScreenshotParameters parameters
    ) => SerializeCommand(id, method, parameters, CdpJsonContext.Default.CdpScreenshotParameters);

    public static byte[] SerializeCommand(
        long id,
        string method,
        CdpScreencastParameters parameters
    ) => SerializeCommand(id, method, parameters, CdpJsonContext.Default.CdpScreencastParameters);

    public static byte[] SerializeCommand(
        long id,
        string method,
        CdpScreencastFrameAckParameters parameters
    ) =>
        SerializeCommand(
            id,
            method,
            parameters,
            CdpJsonContext.Default.CdpScreencastFrameAckParameters
        );

    public static CdpIncomingMessageModel DeserializeIncoming(byte[] utf8Json) =>
        JsonSerializer.Deserialize(utf8Json, CdpJsonContext.Default.CdpIncomingMessageModel)
        ?? throw new JsonException("CDP returned an empty message.");

    public static List<CdpTargetModel> DeserializeTargets(string json) =>
        JsonSerializer.Deserialize(json, CdpJsonContext.Default.ListCdpTargetModel)
        ?? throw new JsonException("The DevTools target list was empty.");

    public static CdpNavigateResultModel DeserializeNavigateResult(JsonElement element) =>
        element.Deserialize(CdpJsonContext.Default.CdpNavigateResultModel)
        ?? throw new JsonException("Page.navigate returned no result.");

    public static CdpEvaluateResultModel DeserializeEvaluateResult(JsonElement element) =>
        element.Deserialize(CdpJsonContext.Default.CdpEvaluateResultModel)
        ?? throw new JsonException("Runtime.evaluate returned no result.");

    public static CdpScreenshotResultModel DeserializeScreenshotResult(JsonElement element) =>
        element.Deserialize(CdpJsonContext.Default.CdpScreenshotResultModel)
        ?? throw new JsonException("Page.captureScreenshot returned no result.");

    public static CdpScreencastFrameModel DeserializeScreencastFrame(JsonElement element) =>
        element.Deserialize(CdpJsonContext.Default.CdpScreencastFrameModel)
        ?? throw new JsonException("Page.screencastFrame returned no parameters.");

    private static byte[] SerializeCommand<TParameters>(
        long id,
        string method,
        TParameters parameters,
        JsonTypeInfo<TParameters> parameterTypeInfo
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        var command = new CdpCommandModel
        {
            Id = id,
            Method = method,
            Parameters = JsonSerializer.SerializeToElement(parameters, parameterTypeInfo),
        };

        return JsonSerializer.SerializeToUtf8Bytes(command, CdpJsonContext.Default.CdpCommandModel);
    }
}

public sealed class CdpCommandModel
{
    public long Id { get; set; }

    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    public JsonElement Parameters { get; set; }
}

public sealed class CdpIncomingMessageModel
{
    public long? Id { get; set; }

    public JsonElement Result { get; set; }

    public CdpErrorModel? Error { get; set; }

    public string? Method { get; set; }

    [JsonPropertyName("params")]
    public JsonElement Parameters { get; set; }
}

public sealed class CdpErrorModel
{
    public int Code { get; set; }

    public string Message { get; set; } = string.Empty;

    public JsonElement Data { get; set; }
}

public sealed class CdpTargetModel
{
    public string Type { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string WebSocketDebuggerUrl { get; set; } = string.Empty;
}

public sealed class CdpEmptyParameters;

public sealed class CdpNavigateParameters
{
    public string Url { get; set; } = string.Empty;
}

public sealed class CdpEvaluateParameters
{
    public string Expression { get; set; } = string.Empty;

    public bool AwaitPromise { get; set; } = true;

    public bool ReturnByValue { get; set; } = true;

    public bool UserGesture { get; set; } = true;
}

public sealed class CdpDeviceMetricsParameters
{
    public int Width { get; set; }

    public int Height { get; set; }

    public double DeviceScaleFactor { get; set; }

    public bool Mobile { get; set; }
}

public sealed class CdpTouchEmulationParameters
{
    public bool Enabled { get; set; }

    public int MaxTouchPoints { get; set; }
}

public sealed class CdpDispatchTouchEventParameters
{
    public string Type { get; set; } = string.Empty;

    public List<CdpTouchPointModel> TouchPoints { get; set; } = [];
}

public sealed class CdpTouchPointModel
{
    public double X { get; set; }

    public double Y { get; set; }

    public double RadiusX { get; set; } = 1.0;

    public double RadiusY { get; set; } = 1.0;

    public double Force { get; set; } = 1.0;
}

public sealed class CdpBackgroundParameters
{
    public CdpColorModel Color { get; set; } = new();
}

public sealed class CdpColorModel
{
    public int R { get; set; }

    public int G { get; set; }

    public int B { get; set; }

    public double A { get; set; }
}

public sealed class CdpScreenshotParameters
{
    public string Format { get; set; } = "png";

    public bool FromSurface { get; set; } = true;

    public bool CaptureBeyondViewport { get; set; }

    public bool OptimizeForSpeed { get; set; } = true;
}

public sealed class CdpScreencastParameters
{
    public string Format { get; set; } = "png";

    public int? MaxWidth { get; set; }

    public int? MaxHeight { get; set; }

    public int EveryNthFrame { get; set; } = 1;
}

public sealed class CdpScreencastFrameAckParameters
{
    public int SessionId { get; set; }
}

public sealed class CdpNavigateResultModel
{
    public string FrameId { get; set; } = string.Empty;

    public string? ErrorText { get; set; }
}

public sealed class CdpEvaluateResultModel
{
    public CdpRemoteObjectModel Result { get; set; } = new();

    public CdpExceptionDetailsModel? ExceptionDetails { get; set; }
}

public sealed class CdpRemoteObjectModel
{
    public string Type { get; set; } = string.Empty;

    public string? Subtype { get; set; }

    public JsonElement Value { get; set; }

    public string? Description { get; set; }
}

public sealed class CdpExceptionDetailsModel
{
    public string Text { get; set; } = string.Empty;

    public int LineNumber { get; set; }

    public int ColumnNumber { get; set; }

    public CdpRemoteObjectModel? Exception { get; set; }
}

public sealed class CdpScreenshotResultModel
{
    public string Data { get; set; } = string.Empty;
}

public sealed class CdpScreencastFrameModel
{
    public string Data { get; set; } = string.Empty;

    public CdpScreencastFrameMetadataModel Metadata { get; set; } = new();

    public int SessionId { get; set; }
}

public sealed class CdpScreencastFrameMetadataModel
{
    public double Timestamp { get; set; }

    public double DeviceWidth { get; set; }

    public double DeviceHeight { get; set; }

    public double PageScaleFactor { get; set; }

    public double OffsetTop { get; set; }

    public double ScrollOffsetX { get; set; }

    public double ScrollOffsetY { get; set; }
}
