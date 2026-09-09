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
/// new field added later doesn't invalidate existing files. That holds on
/// the way out as well as in - see <see cref="Dumps"/>, which until
/// 2026-09-09 wrote every field and so froze the defaults of the day.
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

    /// <summary>
    /// Writes only the settings this config genuinely differs from the
    /// built-in defaults on, plus the schema version.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Changed 2026-09-09, from a real run.</b> This method used to
    /// serialize every property, which made the doc comment above ("a
    /// project only records what it genuinely differs on") true of reads
    /// and false of writes: the starter config Capture Model wrote froze
    /// the entire default set as it stood at the moment of first capture,
    /// and <c>CaptureModelCommand</c> never overwrites an existing file.
    /// </para>
    /// <para>
    /// The consequence was invisible and general: <b>every recalibration
    /// this project makes is silently ignored on any model already
    /// captured.</b> Model 100302 was captured on 2026-09-07 and ran on
    /// 2026-09-09 still using that morning's
    /// <see cref="RuleConfig.PileChainCollinearityToleranceDegrees"/> of 60"
    /// and <see cref="RuleConfig.PileChainMinimumPiles"/> of 2 - so §20's
    /// retune to 0.2° and 3, made against that very model's own scatter,
    /// never applied to it. A 2-pile run still got a verdict and a 0.049°
    /// wander was still reported as a corner.
    /// </para>
    /// <para>
    /// Writing only real differences means an unremarked field tracks the
    /// compiled default and improves as the tool does. Pinning a value
    /// against future change stays possible and is now a deliberate act:
    /// name the field in the file by hand, and the read path honours it.
    /// </para>
    /// </remarks>
    public static string Dumps(RuleConfig config)
    {
        var node = JsonSerializer.SerializeToNode(config, Options)!.AsObject();
        var defaults = DefaultsNode();

        foreach (var name in node.Select(p => p.Key).ToList())
        {
            if (defaults.TryGetPropertyValue(name, out var fallback) && SameJson(node[name], fallback))
            {
                node.Remove(name);
            }
        }

        node["schema_version"] = SchemaVersion;
        return node.ToJsonString(Options);
    }

    /// <summary>
    /// One line per setting the file pins to something other than the
    /// current built-in default - what a run is actually using that it
    /// would not use otherwise.
    /// </summary>
    /// <remarks>
    /// Exists so a stale pin is visible in the run's own output rather than
    /// silent. A file written before <see cref="Dumps"/> started recording
    /// only differences pins every setting it was written with, including
    /// ones later recalibrated, and nothing in the type system, the tests
    /// or the build can see that - only the run can say it.
    /// </remarks>
    public static IReadOnlyList<string> DescribeOverrides(string json)
    {
        var node = JsonNode.Parse(json)?.AsObject();
        if (node is null)
        {
            return new List<string>();
        }

        var defaults = DefaultsNode();
        var described = new List<string>();

        foreach (var property in node)
        {
            if (property.Key == "schema_version")
            {
                continue;
            }

            if (!defaults.TryGetPropertyValue(property.Key, out var fallback))
            {
                described.Add($"{property.Key} = {Compact(property.Value)} (not a setting this build knows)");
                continue;
            }

            if (!SameJson(property.Value, fallback))
            {
                described.Add(
                    $"{property.Key} = {Compact(property.Value)} (built-in default is {Compact(fallback)})");
            }
        }

        return described;
    }

    private static JsonObject DefaultsNode() =>
        JsonSerializer.SerializeToNode(new RuleConfig(), Options)!.AsObject();

    private static bool SameJson(JsonNode? left, JsonNode? right) =>
        string.Equals(Compact(left), Compact(right), StringComparison.Ordinal);

    private static string Compact(JsonNode? node) => node?.ToJsonString() ?? "null";

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

    /// <summary>
    /// The config at <paramref name="path"/> together with the settings it
    /// pins away from the current built-in defaults - see
    /// <see cref="DescribeOverrides"/> for why a caller should report them.
    /// </summary>
    public static (RuleConfig Config, IReadOnlyList<string> Overrides) LoadWithOverrides(string path)
    {
        var json = File.ReadAllText(path);
        return (Loads(json), DescribeOverrides(json));
    }
}
