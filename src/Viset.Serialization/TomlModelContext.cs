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
[TomlSerializable(typeof(MatrixTomlModel))]
[TomlSerializable(typeof(BrowserLockTomlModel))]
[TomlSerializable(typeof(OutputMarkerTomlModel))]
[TomlSerializable(typeof(OutputManifestTomlModel))]
internal partial class TomlModelContext : TomlSerializerContext;
