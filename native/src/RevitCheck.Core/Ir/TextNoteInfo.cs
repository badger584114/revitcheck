namespace RevitCheck.Core.Ir;

/// <summary>
/// A captured Revit <c>TextNote</c> - added 2026-08-26 specifically for
/// bearing calls on a pile setout drawing (confirmed real format
/// <c>165° 07' 01"</c>, PLANNING.md §14). Raw facts only, matching the
/// rest of the IR's "extract facts, judge nothing" split - this type
/// doesn't know it's a bearing note, it's just a text note with a
/// position; <c>Checks.BearingText</c> is what decides whether its
/// <see cref="RawText"/> parses as one.
/// </summary>
public sealed class TextNoteInfo
{
    public required long ElementId { get; init; }

    public required long ViewId { get; init; }

    public required string RawText { get; init; }

    /// <summary>Local project coordinates (mm) - see ElementMetadata.LocalPoint's remarks on why local, not survey-adjusted, for the proximity-matching role this plays.</summary>
    public Point3D? LocalPoint { get; init; }
}
