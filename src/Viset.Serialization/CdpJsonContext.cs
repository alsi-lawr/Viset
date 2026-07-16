using System.Text.Json.Serialization;

namespace Viset.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
[JsonSerializable(typeof(CdpCommandModel))]
[JsonSerializable(typeof(CdpIncomingMessageModel))]
[JsonSerializable(typeof(List<CdpTargetModel>))]
[JsonSerializable(typeof(CdpEmptyParameters))]
[JsonSerializable(typeof(CdpNavigateParameters))]
[JsonSerializable(typeof(CdpEvaluateParameters))]
[JsonSerializable(typeof(CdpDeviceMetricsParameters))]
[JsonSerializable(typeof(CdpTouchEmulationParameters))]
[JsonSerializable(typeof(CdpDispatchTouchEventParameters))]
[JsonSerializable(typeof(CdpBackgroundParameters))]
[JsonSerializable(typeof(CdpScreenshotParameters))]
[JsonSerializable(typeof(CdpNavigateResultModel))]
[JsonSerializable(typeof(CdpEvaluateResultModel))]
[JsonSerializable(typeof(CdpScreenshotResultModel))]
internal partial class CdpJsonContext : JsonSerializerContext;
