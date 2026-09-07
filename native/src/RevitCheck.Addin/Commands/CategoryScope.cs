using Autodesk.Revit.DB;
using RevitCheck.Core.Checks;

namespace RevitCheck.Addin.Commands;

/// <summary>
/// Resolves <see cref="RuleConfig.PileCollectionCategoryNames"/> into real
/// <see cref="BuiltInCategory"/> values for the adapter's collection sweep.
/// </summary>
/// <remarks>
/// The config carries enum <em>names</em> rather than numeric values so the
/// per-model file stays readable and editable by a person, which is the
/// whole point of it existing. A name that doesn't resolve is reported back
/// to the caller for the run's own output rather than dropped - a silently
/// ignored category would reproduce exactly the failure this whole
/// mechanism exists to prevent: a check that examines nothing and looks
/// like it examined everything.
/// </remarks>
internal static class CategoryScope
{
    public static (List<BuiltInCategory> Categories, List<string> Unresolved) Resolve(RuleConfig config)
    {
        var categories = new List<BuiltInCategory>();
        var unresolved = new List<string>();

        foreach (var name in config.PileCollectionCategoryNames)
        {
            if (Enum.TryParse<BuiltInCategory>(name, ignoreCase: true, out var parsed) &&
                Enum.IsDefined(typeof(BuiltInCategory), parsed))
            {
                categories.Add(parsed);
            }
            else
            {
                unresolved.Add(name);
            }
        }

        // Never hand the adapter an empty list: that would silently fall
        // back to its own DefaultCategories set, which is a different and
        // much wider sweep than anything this config asked for.
        if (categories.Count == 0)
        {
            categories.Add(BuiltInCategory.OST_StructuralFoundation);
            unresolved.Add(
                "no configured category resolved - fell back to OST_StructuralFoundation");
        }

        return (categories, unresolved);
    }

    /// <summary>A line for the run's own summary, or empty when everything resolved.</summary>
    public static string Note(List<string> unresolved) =>
        unresolved.Count == 0
            ? ""
            : "\n\nCategory config problem(s): " + string.Join("; ", unresolved);
}
