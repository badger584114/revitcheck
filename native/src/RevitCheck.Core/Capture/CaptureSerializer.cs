using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Json;

namespace RevitCheck.Core.Capture;

/// <summary>
/// JSON save/load for <see cref="RevitModel"/> - the C# counterpart of
/// Python's <c>capture.py</c>. Same discipline: a <c>schema_version</c> is
/// stamped into every write, and a load refuses (throws) rather than
/// misreads a capture written by a newer, incompatible build. A missing or
/// older <c>schema_version</c> is accepted - forward-compatible reads,
/// mirroring Python's <c>.get(..., default)</c> pattern; an added field with
/// a sensible default does not need a version bump, only a field whose
/// *meaning* changed does.
/// </summary>
public static class CaptureSerializer
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = SnakeCaseLowerNamingPolicy.Instance,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(SnakeCaseLowerNamingPolicy.Instance) },
    };

    public static string Dumps(RevitModel model)
    {
        var node = JsonSerializer.SerializeToNode(model, Options)!.AsObject();
        node["schema_version"] = SchemaVersion;
        return node.ToJsonString(Options);
    }

    public static RevitModel Loads(string json)
    {
        var node = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("Capture JSON did not parse to an object.");

        var version = node["schema_version"]?.GetValue<int>() ?? 0;
        if (version > SchemaVersion)
        {
            throw new InvalidOperationException(
                $"Capture schema_version {version} is newer than this build supports " +
                $"({SchemaVersion}); refusing to misread it.");
        }

        node.Remove("schema_version");
        return node.Deserialize<RevitModel>(Options)
            ?? throw new InvalidOperationException("Capture JSON did not deserialize to a RevitModel.");
    }

    public static string Save(RevitModel model, string path)
    {
        File.WriteAllText(path, Dumps(model));
        return path;
    }

    public static RevitModel Load(string path) => Loads(File.ReadAllText(path));
}
