namespace RevitCheck.Core.Ir;

/// <summary>
/// A capture of a Revit model, reduced to what the checks need - one file,
/// growing. Started as the minimal seed metadata reconciliation needed
/// (<see cref="Elements"/> only); <see cref="Sheets"/>/<see cref="Views"/>/
/// <see cref="Dimensions"/> were added for the dimension-checks port,
/// additively - no schema-version bump, since an older or metadata-only
/// capture loads fine with these as empty lists, and a dimension-focused
/// capture loads fine with an empty <see cref="Elements"/>.
/// </summary>
/// <remarks>
/// <see cref="ExtractionErrors"/> and <see cref="ExcludedWorksets"/> exist
/// from day one, mirroring <c>ir.py</c>'s <c>RevitModel</c>, so a shrunken
/// capture never looks identical to a clean one - see
/// <c>CaptureCoverageCheck</c>.
/// </remarks>
public sealed class RevitModel
{
    public string DocTitle { get; init; } = "";

    public string? RevitVersion { get; init; }

    public string? CapturedAt { get; init; }

    public List<SheetInfo> Sheets { get; init; } = new();

    public List<ViewInfo> Views { get; init; } = new();

    public List<DimensionInfo> Dimensions { get; init; } = new();

    public List<ElementMetadata> Elements { get; init; } = new();

    /// <summary>Captured ViewSchedules - added for the pile model-vs-schedule check (PLANNING.md §14, 2026-08-26). Additive like Sheets/Views/Dimensions before it: an older capture loads fine with this empty.</summary>
    public List<ScheduleInfo> Schedules { get; init; } = new();

    /// <summary>Captured TextNotes - added for the pile chain bearing check (PLANNING.md §14, 2026-08-26). Additive, same pattern as Schedules.</summary>
    public List<TextNoteInfo> TextNotes { get; init; } = new();

    /// <summary>Per-element extraction failures, isolated rather than raised - one bad element cannot abort a capture.</summary>
    public List<string> ExtractionErrors { get; init; } = new();

    /// <summary>Worksets excluded from this capture by user choice at capture time.</summary>
    public List<string> ExcludedWorksets { get; init; } = new();

    // Built once on first use - a RevitModel is assembled whole (by the
    // adapter, or by a capture load) and never mutated afterwards, so
    // there's nothing for these caches to go stale against. Mirrors
    // ir.py's RevitModel._view_index/_sheet_index lazy-cache pattern.
    private Dictionary<long, ViewInfo>? _viewIndex;
    private Dictionary<long, SheetInfo>? _sheetIndex;
    private Dictionary<long, ElementMetadata>? _elementIndex;
    private Dictionary<long, List<DimensionInfo>>? _dimensionsByViewCache;

    public ViewInfo? ViewById(long? viewId)
    {
        if (viewId is null)
        {
            return null;
        }

        if (_viewIndex is null)
        {
            _viewIndex = new Dictionary<long, ViewInfo>();
            foreach (var view in Views)
            {
                _viewIndex[view.ElementId] = view; // last one wins on a duplicate id, matching the Python dict comprehension this mirrors
            }
        }

        return _viewIndex.TryGetValue(viewId.Value, out var found) ? found : null;
    }

    public SheetInfo? SheetById(long? sheetId)
    {
        if (sheetId is null)
        {
            return null;
        }

        if (_sheetIndex is null)
        {
            _sheetIndex = new Dictionary<long, SheetInfo>();
            foreach (var sheet in Sheets)
            {
                _sheetIndex[sheet.ElementId] = sheet;
            }
        }

        return _sheetIndex.TryGetValue(sheetId.Value, out var found) ? found : null;
    }

    public ElementMetadata? ElementById(long? elementId)
    {
        if (elementId is null)
        {
            return null;
        }

        if (_elementIndex is null)
        {
            _elementIndex = new Dictionary<long, ElementMetadata>();
            foreach (var element in Elements)
            {
                _elementIndex[element.ElementId] = element;
            }
        }

        return _elementIndex.TryGetValue(elementId.Value, out var found) ? found : null;
    }

    /// <summary>
    /// Dimensions grouped by their owning view, in one pass. Rules that
    /// roll up per view use this rather than filtering the dimension list
    /// once per view - on a real set that's a few hundred views against a
    /// few thousand dimensions, and the naive form is the product of the two.
    /// </summary>
    public IReadOnlyDictionary<long, List<DimensionInfo>> DimensionsByView()
    {
        if (_dimensionsByViewCache is null)
        {
            var grouped = new Dictionary<long, List<DimensionInfo>>();
            foreach (var dim in Dimensions)
            {
                if (!grouped.TryGetValue(dim.ViewId, out var list))
                {
                    list = new List<DimensionInfo>();
                    grouped[dim.ViewId] = list;
                }

                list.Add(dim);
            }

            _dimensionsByViewCache = grouped;
        }

        return _dimensionsByViewCache;
    }
}
