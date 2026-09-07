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
    /// <summary>
    /// The categories to sweep. An empty
    /// <see cref="RuleConfig.PileCollectionCategoryNames"/> means every
    /// model category the document defines - <c>AllModelCategories</c> true
    /// and <c>Categories</c> null, for the adapter's own
    /// <c>allModelCategories</c> mode.
    /// </summary>
    public static (List<BuiltInCategory>? Categories, bool AllModelCategories, List<string> Unresolved) Resolve(RuleConfig config)
    {
        var unresolved = new List<string>();

        if (config.PileCollectionCategoryNames.Count == 0)
        {
            return (null, true, unresolved);
        }

        var categories = new List<BuiltInCategory>();
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

        // Every configured name was junk. Sweeping everything is the safe
        // direction: the alternative is examining a narrow guess nobody
        // asked for, which is exactly the silent-scope failure this whole
        // mechanism exists to prevent.
        if (categories.Count == 0)
        {
            unresolved.Add("no configured category resolved - swept every model category instead");
            return (null, true, unresolved);
        }

        return (categories, false, unresolved);
    }

    /// <summary>A line for the run's own summary, or empty when everything resolved.</summary>
    public static string Note(List<string> unresolved) =>
        unresolved.Count == 0
            ? ""
            : "\n\nCategory config problem(s): " + string.Join("; ", unresolved);
}
