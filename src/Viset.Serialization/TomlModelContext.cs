using System.Text.Json.Serialization;
using Tomlyn;
using Tomlyn.Serialization;

namespace Viset.Serialization;

[TomlSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PreferredObjectCreationHandling = JsonObjectCreationHandling.Replace,
    DuplicateKeyHandling = TomlDuplicateKeyHandling.Error,
    MaxDepth = 64
)]
[TomlSerializable(typeof(CaptureTomlModel))]
[TomlSerializable(typeof(BrowserLockTomlModel))]
internal partial class TomlModelContext : TomlSerializerContext;
