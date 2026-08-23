using System.Text;
using RevitCheck.Core.Ir;

namespace RevitCheck.Core.Checks;

/// <summary>Wording helpers shared by both dimension rules - direct port of <c>checks/dimensions.py</c>'s private formatting functions.</summary>
public static class DimensionDescriptions
{
    /// <summary>Revit's ViewType name as something readable in a sentence: "DraftingView" -&gt; "drafting", "FloorPlan" -&gt; "floor plan".</summary>
    public static string ViewTypeLabel(string viewType)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in viewType)
        {
            if (char.IsUpper(ch) && current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
                current.Append(ch);
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        if (words.Count > 1 && words[words.Count - 1] == "View")
        {
            words.RemoveAt(words.Count - 1);
        }

        return string.Join(" ", words.Select(w => w.ToLowerInvariant()));
    }

    public static string DescribeView(ViewInfo? view)
    {
        if (view is null)
        {
            return "an unknown view";
        }

        var label = $"{ViewTypeLabel(view.ViewType)} view '{view.Name}'";
        if (!string.IsNullOrEmpty(view.SheetNo))
        {
            label += $" (sheet {view.SheetNo})";
        }

        return label;
    }

    public static string DraftedSeverity(ViewInfo? view, RuleConfig config) =>
        ViewScoping.IsUnlinkedDraftingView(view) ? config.DraftedInDraftingViewSeverity : config.DraftedInModelViewSeverity;

    /// <summary>A Revit dimension chain is one element with many segments, so the element id alone doesn't say which number is wrong. Pointing at the element still selects the right thing; this says where to look once it's selected.</summary>
    public static string SegmentLabel(DimensionInfo dimension, int index) =>
        dimension.Segments.Count <= 1 ? "Dimension" : $"Segment {index + 1} of {dimension.Segments.Count} in a dimension chain";
}
