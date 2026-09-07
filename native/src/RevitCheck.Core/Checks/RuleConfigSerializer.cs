using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RevitCheck.Core.Json;

namespace RevitCheck.Core.Checks;

/// <summary>
/// JSON save/load for <see cref="RuleConfig"/> - the same discipline as
/// <see cref="Mapping.ParameterMappingSerializer"/>: a stamped
/// <c>schema_version</c>, refuse rather than misread a newer file,
/// forward-compatible reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists, added 2026-09-07.</b> Every command built after
/// metadata reconciliation constructed <c>new RuleConfig()</c> and used the
/// compiled defaults, so this project's own stated rule - "tolerances must
/// be configurable (RuleConfig), never hardcoded constants" - held only in
/// the type system. In practice a category name, a schedule column heading
/// or a tolerance could not be changed for a new model without rebuilding
/// and redeploying the add-in, which is why the same correction ("this
/// could be a Generic Model, not Structural Framing") had to be made
/// separately in each check instead of propagating once.
/// </para>
/// <para>
/// Every field is optional in the file: a missing one keeps the compiled
/// default, so a project only records what it genuinely differs on and a
/// new field added later doesn't invalidate existing files.
/// </para>
/// </remarks>
public static class RuleConfigSerializer
{
    public const int SchemaVersion = 1;

    /// <summary>The conventional file name suffix, alongside the model - see <c>RuleConfigSource</c> in the Addin for the lookup.</summary>
    public const string FileSuffix = ".revitcheck.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = SnakeCaseLowerNamingPolicy.Instance,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(SnakeCaseLowerNamingPolicy.Instance) },
    };

    public static string Dumps(RuleConfig config)
    {
        var node = JsonSerializer.SerializeToNode(config, Options)!.AsObject();
        node["schema_version"] = SchemaVersion;
        return node.ToJsonString(Options);
    }

    public static RuleConfig Loads(string json)
    {
        var node = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("Rule config JSON did not parse to an object.");

        var version = node["schema_version"]?.GetValue<int>() ?? 0;
        if (version > SchemaVersion)
        {
            throw new InvalidOperationException(
                $"Rule config schema_version {version} is newer than this build supports " +
                $"({SchemaVersion}); refusing to misread it.");
        }

        node.Remove("schema_version");
        return node.Deserialize<RuleConfig>(Options)
            ?? throw new InvalidOperationException("Rule config JSON did not deserialize to a RuleConfig.");
    }

    public static string Save(RuleConfig config, string path)
    {
        File.WriteAllText(path, Dumps(config));
        return path;
    }

    public static RuleConfig Load(string path) => Loads(File.ReadAllText(path));
}
