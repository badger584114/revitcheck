namespace RevitCheck.Core.Ir;

/// <summary>
/// Raw, per-element facts captured from a Revit model for metadata
/// reconciliation - nothing in this type judges anything (no comparison, no
/// classification), matching the adapter-boundary rule the whole IR follows.
/// </summary>
/// <remarks>
/// <see cref="Parameters"/> holds every parameter the adapter could read on
/// this element - not just the ones a mapping file currently maps. That is
/// deliberate: it is what lets a mapping file grow a new canonical field
/// later without a new Revit-machine capture, since the raw data is already
/// there. See native/README (or PLANNING.md) for why this shape was chosen.
///
/// A nested sub-component (e.g. a fixing bracket nested inside a concrete
/// panel family) is captured as its own independent <see cref="ElementMetadata"/>,
/// with its own <see cref="KeyValue"/> and its own field mapping resolved via
/// its own <see cref="FamilyName"/>/<see cref="Category"/> - never inherited
/// from the host. <see cref="HostElementId"/> exists purely for message
/// context (e.g. "bracket (host: Panel P-12)") and does not participate in
/// the reconciliation join.
/// </remarks>
public sealed class ElementMetadata
{
    public long ElementId { get; init; }

    public string? UniqueId { get; init; }

    public string? Category { get; init; }

    /// <summary>Stable, language-independent BuiltInCategory enum value - mirrors ReferenceInfo.builtin_category in ir.py.</summary>
    public long? BuiltInCategory { get; init; }

    public string? FamilyName { get; init; }

    public string? TypeName { get; init; }

    /// <summary>
    /// Denormalized convenience copy of this element's value for whichever
    /// parameter the active mapping names as the key (same convenience
    /// pattern as ViewInfo.sheet_unique_id in ir.py). The authoritative
    /// source is always <see cref="Parameters"/> - a mapping-file rebuild
    /// that renames the key parameter does not require a re-capture, since
    /// the reconciliation check re-resolves the key from
    /// <see cref="Parameters"/> at run time. This field only saves that
    /// relookup for the common case.
    /// </summary>
    public string? KeyValue { get; init; }

    /// <summary>
    /// ElementId of the host element, when this element is a nested
    /// sub-component of another captured element. Null for a top-level
    /// element. Context only - see remarks above.
    /// </summary>
    public long? HostElementId { get; init; }

    public Dictionary<string, ParameterValue> Parameters { get; init; } = new();
}
