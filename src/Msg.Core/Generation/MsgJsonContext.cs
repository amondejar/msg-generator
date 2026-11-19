using System.Text.Json.Serialization;
using Msg.Core.Model;

namespace Msg.Core.Generation;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ServiceDefinition))]
internal sealed partial class MsgJsonContext : JsonSerializerContext;
