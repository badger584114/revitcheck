using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RevitCheck.Core.Json;

namespace RevitCheck.Core.Reporting;

/// <summary>
/// JSON save/load for <see cref="CheckingSession"/> - what makes a session
/// survive a Revit restart (PLANNING.md §16, "the session must survive a
/// Revit restart" - a confirmed real requirement, not a nice-to-have).
/// Mirrors <c>Capture/CaptureSerializer.cs</c>'s exact pattern rather than
/// inventing a second serialization approach: same <c>schema_version</c>
/// stamp, same <see cref="SnakeCaseLowerNamingPolicy"/>, same
/// forward-compatible-load discipline (a missing/older
/// <c>schema_version</c> is accepted; a newer one is refused rather than
/// misread).
/// </summary>
public static class CheckingSessionSerializer
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = SnakeCaseLowerNamingPolicy.Instance,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(SnakeCaseLowerNamingPolicy.Instance) },
    };

    public static string Dumps(CheckingSession session)
    {
        var node = JsonSerializer.SerializeToNode(session, Options)!.AsObject();
        node["schema_version"] = SchemaVersion;
        return node.ToJsonString(Options);
    }

    public static CheckingSession Loads(string json)
    {
        var node = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("Checking-session JSON did not parse to an object.");

        var version = node["schema_version"]?.GetValue<int>() ?? 0;
        if (version > SchemaVersion)
        {
            throw new InvalidOperationException(
                $"Checking-session schema_version {version} is newer than this build supports " +
                $"({SchemaVersion}); refusing to misread it.");
        }

        node.Remove("schema_version");
        return node.Deserialize<CheckingSession>(Options)
            ?? throw new InvalidOperationException("Checking-session JSON did not deserialize to a CheckingSession.");
    }

    public static string Save(CheckingSession session, string path)
    {
        File.WriteAllText(path, Dumps(session));
        return path;
    }

    public static CheckingSession Load(string path) => Loads(File.ReadAllText(path));
}
