using RevitCheck.Core.Ir;

namespace RevitCheck.Core.Checks;

/// <summary>
/// Classifies a dimension reference/dimension's provenance - direct port of
/// <c>checks/dimensions.py</c>'s <c>classify_reference</c>/<c>classify_dimension</c>.
/// </summary>
public static class DimensionClassification
{
    /// <summary>Datums are model elements other model geometry is positioned against. Dimensioning to a grid or level is good practice, not a risk: move the grid and the dimension follows.</summary>
    public static readonly HashSet<string> DatumClasses = new()
    {
        "Grid", "MultiSegmentGrid", "Level", "ReferencePlane", "DatumPlane",
    };

    /// <summary>An imported CAD file is neither view-specific nor model geometry, but a dimension anchored to it is anchored to a static snapshot of someone else's file - same failure mode as detail linework, different mechanism.</summary>
    public static readonly HashSet<string> DraftedClasses = new() { "ImportInstance" };

    /// <summary>
    /// Classify one dimension endpoint. Order matters: ViewSpecific is
    /// checked first and beats everything else, because it's Revit's own
    /// record of "this element belongs to a single view" - the API-level
    /// invariant, not a naming convention. Logic built on domain invariants
    /// held across clients (Flinders); logic built on client conventions
    /// (a CAD-layer-name proxy for the same idea) didn't.
    /// </summary>
    public static Provenance ClassifyReference(ReferenceInfo reference)
    {
        if (!reference.Resolved || reference.ElementId <= 0)
        {
            return Provenance.Unknown;
        }

        if (reference.ViewSpecific == true)
        {
            return Provenance.Drafted;
        }

        if (reference.ClassName is not null && DraftedClasses.Contains(reference.ClassName))
        {
            return Provenance.Drafted;
        }

        if (reference.ClassName is not null && DatumClasses.Contains(reference.ClassName))
        {
            return Provenance.Datum;
        }

        return Provenance.Model;
    }

    /// <summary>
    /// Roll a dimension's endpoints up into a single verdict. A dimension
    /// with no resolvable references at all is Unknown rather than assumed
    /// innocent - CLAUDE.md's "report a coverage indicator, don't fail
    /// silently".
    /// </summary>
    public static Provenance ClassifyDimension(DimensionInfo dimension)
    {
        var found = new HashSet<Provenance>(dimension.References.Select(ClassifyReference));
        if (found.Count == 0)
        {
            return Provenance.Unknown;
        }

        var known = new HashSet<Provenance>(found);
        known.Remove(Provenance.Unknown);
        if (known.Count == 0)
        {
            return Provenance.Unknown;
        }

        var live = new HashSet<Provenance>(known);
        live.IntersectWith(new[] { Provenance.Model, Provenance.Datum });

        if (known.Contains(Provenance.Drafted))
        {
            return live.Count > 0 ? Provenance.Mixed : Provenance.Drafted;
        }

        if (known.Count == 1 && known.Contains(Provenance.Datum))
        {
            return Provenance.Datum;
        }

        return Provenance.Model;
    }
}
