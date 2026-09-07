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

    /// <summary>
    /// The direction the note's own text runs, in MODEL coordinates, as an
    /// azimuth-style angle (0 = +Y, 90 = +X) - null when the adapter
    /// couldn't read it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Added 2026-09-07 from a real pile-layout drawing. A bearing call is
    /// drawn <em>parallel to the line it describes</em> - the two
    /// "175° 08' 40"" calls on that sheet are rotated to run vertically
    /// alongside the vertical pile lines, while "90° 35' 22"" and
    /// "85° 08' 40"" sit horizontally beside their own horizontal runs. That
    /// makes rotation the signal that says which line a call belongs to,
    /// and distance alone provably is not: bearing calls sit at the ends of
    /// lines, lines meet at their ends, and a three-pile spur sharing its
    /// end pile with a main line had the main line's call printed directly
    /// over that shared pile.
    /// </para>
    /// <para>
    /// <b>Model coordinates, deliberately.</b> A run's bearing is computed
    /// from ProjectPosition (survey space), which can be rotated relative
    /// to the model if project north differs from true north - so a note's
    /// model-space rotation is not comparable to a survey-space bearing.
    /// Rotation is therefore matched against the run's own model-space
    /// direction (from <c>ElementMetadata.LocalPoint</c>), while the
    /// bearing <em>value</em> is still compared in survey space. Geometry
    /// picks the note; content is what gets checked.
    /// </para>
    /// </remarks>
    public double? DirectionDegrees { get; init; }
}
