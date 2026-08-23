namespace RevitCheck.Core.Ir;

/// <summary>
/// A capture of a Revit model, scoped to what metadata reconciliation needs.
/// </summary>
/// <remarks>
/// This is a deliberately minimal seed - it does not port the Python IR's
/// Sheets/Views/Dimensions (that is PLANNING.md §12's separate, larger
/// porting effort, out of scope here). <see cref="ExtractionErrors"/> and
/// <see cref="ExcludedWorksets"/> exist from day one, mirroring
/// <c>ir.py</c>'s <c>RevitModel</c>, so a shrunken capture never looks
/// identical to a clean one - see <c>CaptureCoverageCheck</c>.
/// </remarks>
public sealed class RevitModel
{
    public string DocTitle { get; init; } = "";

    public string? RevitVersion { get; init; }

    public string? CapturedAt { get; init; }

    public List<ElementMetadata> Elements { get; init; } = new();

    /// <summary>Per-element extraction failures, isolated rather than raised - one bad element cannot abort a capture.</summary>
    public List<string> ExtractionErrors { get; init; } = new();

    /// <summary>Worksets excluded from this capture by user choice at capture time.</summary>
    public List<string> ExcludedWorksets { get; init; } = new();
}
