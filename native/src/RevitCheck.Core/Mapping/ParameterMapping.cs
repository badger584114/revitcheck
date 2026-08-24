using System.Text.Json.Serialization;

namespace RevitCheck.Core.Mapping;

/// <summary>
/// The "export file taken once and saved" - built once from a real capture
/// and a real CSV's headers, confirmed by a human, then reused. Empty
/// <see cref="Fields"/> by default, entirely project-specific - the same "no
/// assumed client convention" discipline as the Python side's
/// <c>RuleConfig.setout_critical_type_names</c>. Adding a new canonical
/// field later is one new entry here - no schema bump, no re-capture (the
/// capture's raw ElementMetadata.Parameters already has everything), no
/// adapter change.
/// </summary>
public sealed class ParameterMapping
{
    /// <summary>Free-text provenance/caveat notes - same convention as config/firm_glossary.json's own "_note" array.</summary>
    [JsonPropertyName("_note")]
    public List<string> Note { get; init; } = new();

    public int SchemaVersion { get; init; } = 1;

    /// <summary>The Revit parameter name used to join elements to CSV rows.</summary>
    public required string KeyParameterName { get; init; }

    /// <summary>The CSV column holding the same key. Defaults to KeyParameterName if unset - a CSV header and a Revit parameter name are unlikely to match byte-for-byte.</summary>
    public string? KeyCsvColumn { get; init; }

    /// <summary>
    /// The name of a Revit view whose visible elements define the sweep -
    /// found necessary from a real Revit-machine run 2026-08-24: the
    /// category scope alone (Floors, Generic Models, Structural
    /// Connections/Foundations/Framing) matched far more of the model than
    /// the intended trackable set, because those categories exist all over
    /// a real project, not just on the tracked assets. The user's existing
    /// tools already solve this the same way: a curated view (their example
    /// - "NavisworksExport") with its own visibility/filter overrides
    /// showing exactly the elements meant to be interrogated. Null means
    /// "sweep the whole document within the category scope" - the original,
    /// now-known-too-broad behaviour - kept as the default rather than
    /// removed, since not every future client/project will necessarily
    /// have an equivalent curated view.
    /// </summary>
    public string? ScopeViewName { get; init; }

    public Dictionary<string, FieldMapping> Fields { get; init; } = new();

    [JsonIgnore]
    public string ResolvedKeyCsvColumn => KeyCsvColumn ?? KeyParameterName;

    public void Validate()
    {
        foreach (var entry in Fields)
        {
            entry.Value.Validate(entry.Key);
        }
    }
}
