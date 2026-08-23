using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RevitCheck.Core.Mapping;

/// <summary>
/// JSON save/load for <see cref="ParameterMapping"/> - same discipline as
/// <c>CaptureSerializer</c>: a stamped <c>schema_version</c>, refuse rather
/// than misread a newer file, forward-compatible reads. Validates the
/// loaded mapping (e.g. every numeric field has a tolerance) before handing
/// it back, so a broken mapping file fails at load time, not mid-run on
/// whichever element happens to hit the missing tolerance first.
/// </summary>
public static class ParameterMappingSerializer
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static string Dumps(ParameterMapping mapping)
    {
        var node = JsonSerializer.SerializeToNode(mapping, Options)!.AsObject();
        node["schema_version"] = SchemaVersion;
        return node.ToJsonString(Options);
    }

    public static ParameterMapping Loads(string json)
    {
        var node = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("Mapping JSON did not parse to an object.");

        var version = node["schema_version"]?.GetValue<int>() ?? 0;
        if (version > SchemaVersion)
        {
            throw new InvalidOperationException(
                $"Mapping schema_version {version} is newer than this build supports " +
                $"({SchemaVersion}); refusing to misread it.");
        }

        node.Remove("schema_version");
        var mapping = node.Deserialize<ParameterMapping>(Options)
            ?? throw new InvalidOperationException("Mapping JSON did not deserialize to a ParameterMapping.");

        mapping.Validate();
        return mapping;
    }

    public static string Save(ParameterMapping mapping, string path)
    {
        File.WriteAllText(path, Dumps(mapping));
        return path;
    }

    public static ParameterMapping Load(string path) => Loads(File.ReadAllText(path));
}
