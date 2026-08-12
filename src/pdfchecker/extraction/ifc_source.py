"""IFC ingestion — PLANNING.md §5's proposed third geometry-check source
(raised by the user 2026-08-11, real samples added 2026-08-12). Reads an
IFC file via `ifcopenshell` (`pip install ifcopenshell` — confirmed
2026-08-12 to have a prebuilt wheel for this environment's old
x86_64-under-Rosetta Python 3.9.5, no build-from-source issue like the
`cryptography` one documented in CLAUDE.md).

Calibrated against the two real `.ifc` files now in `samples/` —
`samples/BR06/T2DPAA-T2D-C3S-BR-M3D-100302.ifc` and
`samples/BR08/T2DPAA-T2D-C3S-BR-M3D-100304.ifc`. Both IFC4. The user
flagged something important before this module was written, confirmed
directly against both files: **these two happen to come from the same
overall client project, so their metadata conventions match each other
— that is NOT evidence those conventions generalize to a different
client's Revit export.** Everything below is written to keep that
distinction explicit: what's IFC4-schema-standard (safe to build
general logic on) vs. what's this-client's-specific Revit-export
convention (real, calibrated, but must not be hardcoded as if every IFC
file works the same way).

**Schema-general (safe): element typing.** Every physical element is
read via `IfcElement` — IFC4's own schema base class, not a hardcoded
whitelist. Confirmed necessary, not just cautious: BR06 and BR08 (same
client, different structures) already have different class mixes —
BR06 is `IfcBeam`/`IfcSlab`/`IfcBuildingElementProxy`/
`IfcBuildingElementPart`/`IfcRailing` (157 elements), BR08 adds
`IfcMember`/`IfcWall`/`IfcRoof`/`IfcGeographicElement` and has vastly
more `IfcBuildingElementProxy` (1082 elements total) — so a fixed
"expected structural classes" list would already have missed real data
one sample over. `IfcOpeningElement` instances (voids/penetrations, not
built geometry — 20 on BR06, 145 on BR08) are excluded by schema
semantics (`IfcFeatureElement` subtype), not a project-specific filter.
`ifc_class` and `predefined_type` (e.g. `"IfcBeam"`/`"BEAM"`,
`"IfcMember"`/`"BRACE"`) are both IFC4 schema enumerations — portable
to any authoring tool. `GlobalId` is the schema-standard GUID.

**Firm-specific (real, but not to build matching logic around): Name/
ObjectType/Tag and most property sets.** Every element's `Name` is a
raw Revit "Family and Type: Tag" string, e.g. `"03_SFR_ACS_Abutment_CS:
ABUTMENT EAST_BR06_3:5328892"` — confirmed this firm's own family
library naming (prefixes like `T2D_`, `WGA22_`, `08_ADC_`, `DIT_`), not
part of the IFC schema and not guaranteed to look anything like this on
a different client's model. Property sets are a real mix of portable
IFC-standard ones (`Pset_BeamCommon`, `Pset_MemberCommon`,
`Qto_BeamBaseQuantities`, ...) and clearly firm-prefixed custom ones
(`T2D_Project`, `T2D_QTO`, `DIT_Location`, `DIT_Asset`) confirmed on
every element inspected — this module doesn't read property sets at
all yet for exactly that reason: no way yet to tell which properties on
a THIRD client's export would be the portable ones without another real
sample to check against, so it isn't worth guessing. `Name` is kept
only as `IfcElement.display_name` for human-readable labeling (Issue
payloads, audit trail) — never as a lookup/matching key.

**Schema-general (safe): geometry is always in real metres.**
`ifcopenshell.geom` with `USE_WORLD_COORDS` on and `CONVERT_BACK_UNITS`
left at its default (`False`) normalizes every element's geometry to
metres, confirmed directly — both real files declare a MILLIMETRE
length unit (`IfcSIUnit(*, .LENGTHUNIT., .MILLI., .METRE.)`), yet the
returned vertex coordinates already come out at real-metre scale (e.g.
~278,445 / ~6,130,700, not ~278,445,000). This is `ifcopenshell`'s own
general behavior, not something calibrated per-project — confirmed by
comparing `CONVERT_BACK_UNITS=True` (returns file-native mm, x1000
larger) against the default. `IfcModel.length_unit` still records the
file's own declared unit as read, informationally, in case a future
file's geometry needs the opposite handling.

**Firm-specific (real, must NOT be hardcoded as general): raw world
coordinates happen to equal true survey Easting/Northing here.**
Neither file carries IFC4's schema-standard georeferencing entities —
`IfcMapConversion`/`IfcProjectedCRS` both come back empty on both real
files, confirmed by direct query (`has_map_conversion` is `False` on
both, so far). Instead, `IfcSite`'s raw placement offset — read
directly off the underlying `IfcCartesianPoint`, bypassing the geometry
kernel — is `(278000000, 6129000000, 0)` in the file's declared
millimetre unit, i.e. **278,000m E / 6,129,000m N**: real-looking
MGA-zone-scale survey coordinates, not an arbitrary Revit internal
origin. Confirmed **identical** between BR06 and BR08 — consistent with
the user's point that this is one client project's shared Revit "Survey
Point" convention (the same real-world base point baked directly into
both models' placement chains), not something IFC guarantees for every
export. A different client's Revit export could just as easily use
`IfcMapConversion` properly, or use an arbitrary internal origin with
no real-world meaning at all — so this module does NOT assume "world
coordinates are already real Easting/Northing" as a general rule; it's
recorded as an unverified-but-plausible fact per project
(`has_map_conversion=False` is the signal a caller should treat this
as needing independent verification, e.g. against a title-block
lat/long or a known setout point, before trusting it for a real-world
cross-check).

`IfcSite.RefLatitude`/`RefLongitude` (IFC4 schema-standard, present on
every `IfcSite` regardless of author) is read as a portable, if
coarser, real-world anchor instead — `_dms_to_decimal` converts the
`(deg, min, sec, [microsec])` compound-angle tuple IFC stores this in
to decimal degrees, the same representation PDF title-block extraction
already uses for `sheet_latitude`/`sheet_longitude` (see
`extraction/titleblock.py`). Confirmed real but coarse: BR06's decoded
`RefLatitude` is -34.9572° vs. sheet 101051's real title-block
`sheet_latitude` of -34.94188° — about 1.7km apart, consistent with
this being one project-wide site reference, not per-sheet precision.

**Confirmed real gap: DXF model space and IFC world space are NOT the
same coordinate frame, despite sharing a Revit source model.** Sheet
101051's real `DIMENSION` witness points (`extraction/dxf_source.py`)
sit around DXF model-space (576, 1687) — nowhere near IFC's
real-Easting/Northing-scale world coordinates (~278445, ~6130700).
"Exported from the same Revit model" does not imply a free shared
coordinate frame between the DWG/DXF export and the IFC export — this
still needs its own resolution (comparable to PLANNING.md §8's
still-unbuilt DXF→PDF transform) before any DXF/PDF-vs-IFC geometric
cross-check rule can be written. Not attempted here.

**Correction, 2026-08-12: BR06's IFC model DOES have piles — an earlier
version of this docstring claimed otherwise, and that was wrong.** The
user caught it (they could see piles in Navisworks). The mistake: an
early exploration pass sampled only the first 8 of 45 real `IfcSlab`
elements and none of those happened to be piles. All 28 real piles are
there, each modeled as `IfcSlab` with `PredefinedType='BASESLAB'` —
Revit's own class choice for a cast-in-place pile on this export, not
an IFC-standard "pile" designation (there's no `IfcPile` in sight).
`Name` does say "CAST-IN-PLACE PILE" (firm-specific text, per this
module's don't-rely-on-Name stance above) — but confirmed a fully
schema-general alternative works too: **a bounding-box shape heuristic
(small footprint, tall — `max(dx, dy) < 2m` and `dz / max(dx, dy) > 3`)
applied across every element regardless of class, with no Name/
PredefinedType involved at all, finds exactly the same 28 elements as
the Name-text search, zero mismatches.** That's the schema-general path
the user originally asked for: geometry alone, not this firm's naming.
See `checks/geometry.py`'s `_is_slender_vertical` for where this
heuristic actually lives (a check-time interpretation, not baked into
ingestion — this module still returns every element's raw geometry).

**Confirmed real and strong: this file's raw world coordinates line up
with real Easting/Northing to sub-millimetre precision, not just
IfcSite's coarse ~1.7km-off reference point.** Matched all 28 real IFC
piles (by the shape heuristic above) against the real pile schedule's
28 rows (`extraction/setout_reconstruction.py`'s `parse_pile_schedule`,
sheet 2871051) by nearest horizontal distance: **mean delta 0.4mm, max
0.6mm.** Still not a general IFC guarantee — no `IfcMapConversion` on
this file (see above), so this is empirical confirmation of THIS
project's Revit "shared coordinates" convention holding at
element-level precision, not a schema promise a different client's
export would honor too.

**What this doesn't mean, per the user's explicit correction: it's
still wrong to compare DXF model-space geometry to IFC world-space
geometry directly.** A DWG/DXF export is a *sheet* export (paper-space
view of a crop/viewport), not the master model space — there is no
single fixed transform between a sheet's local drafting coordinates and
IFC world coordinates, and this module makes no attempt to find one.
The correct comparison (see `checks/geometry.py`'s
`geometry.ifc_setout_consistency`) is: reconstruct each point's
real-world position independently from the sheet's own printed inputs
(setout-point origin + bearing + dimension chain —
`extraction/setout_reconstruction.py`'s existing `reconstruct_sheet`,
which already outputs real Easting/Northing, not sheet-local
coordinates) and compare *that* against the nearest matching IFC
element — never the raw DXF witness-point coordinates against IFC
directly.

**Not built here, deliberately:** the check rule stays in
`checks/geometry.py`, not this module — this module only ingests raw
element geometry; identifying which elements are pile-like and
matching them to a sheet's reconstructed points is check-time
interpretation, kept out of ingestion the same way `dxf_source.py`
keeps dimension-tolerance logic out of `DimensionEntity` extraction.
"""

from __future__ import annotations

import ifcopenshell
import ifcopenshell.geom as ifc_geom

from pdfchecker.ir import IfcElement, IfcModel, Point3D

# Physical elements only — IfcFeatureElement subtypes (openings,
# projections) aren't built geometry, they're voids/additions applied
# to a real element's shape. Schema-based exclusion (IFC4's own class
# hierarchy), not a project-specific filter — confirmed real on both
# samples: 20 IfcOpeningElement on BR06, 145 on BR08, neither wanted.
_EXCLUDED_CLASSES = {"IfcOpeningElement", "IfcVirtualElement"}

_geom_settings = ifc_geom.settings()
_geom_settings.set(_geom_settings.USE_WORLD_COORDS, True)
# CONVERT_BACK_UNITS deliberately left at its default (False) — see
# this module's docstring: that default is what normalizes geometry to
# real metres regardless of the file's declared length unit.


def _dms_to_decimal(value) -> float | None:
    """Converts IFC's `IfcCompoundPlaneAngleMeasure` — a `(degrees,
    minutes, seconds, [microseconds])` tuple, sign carried on every
    component — to decimal degrees. `None` in, `None` out (an `IfcSite`
    with no `RefLatitude`/`RefLongitude` set at all is real and
    legitimate, not an extraction failure)."""

    if not value:
        return None
    sign = -1.0 if value[0] < 0 else 1.0
    deg, minute, sec = abs(value[0]), abs(value[1]), abs(value[2])
    microsec = abs(value[3]) if len(value) > 3 else 0
    return sign * (deg + minute / 60 + sec / 3600 + microsec / (3600 * 1_000_000))


def _bbox(verts: list[float]) -> tuple[Point3D, Point3D]:
    xs, ys, zs = verts[0::3], verts[1::3], verts[2::3]
    return (
        Point3D(x=min(xs), y=min(ys), z=min(zs)),
        Point3D(x=max(xs), y=max(ys), z=max(zs)),
    )


def extract_elements(f) -> list[IfcElement]:
    """Every physical `IfcElement` with real geometry. Elements with no
    `Representation` (confirmed real — a handful on both samples, e.g.
    a Revit floor type with no geometry override) are skipped, not
    guessed at — same "skip rather than misread" convention as
    `extraction/dxf_source.py`'s dim_type=0-but-non-numeric-override
    case."""

    elements = []
    for e in f.by_type("IfcElement"):
        if e.is_a() in _EXCLUDED_CLASSES:
            continue
        if e.Representation is None:
            continue
        try:
            shape = ifc_geom.create_shape(_geom_settings, e)
        except RuntimeError:
            continue
        bbox_min, bbox_max = _bbox(shape.geometry.verts)
        elements.append(
            IfcElement(
                global_id=e.GlobalId,
                ifc_class=e.is_a(),
                predefined_type=getattr(e, "PredefinedType", None),
                display_name=e.Name,
                bbox_min=bbox_min,
                bbox_max=bbox_max,
            )
        )
    return elements


def ingest_ifc(path: str) -> IfcModel:
    f = ifcopenshell.open(path)

    length_unit = "unknown"
    projects = f.by_type("IfcProject")
    if projects and projects[0].UnitsInContext:
        for u in projects[0].UnitsInContext.Units:
            # Only IfcSIUnit declares .UnitType — IfcConversionBasedUnit
            # (e.g. degrees) and IfcDerivedUnit don't, so guard the
            # attribute access rather than assume every unit is an SI one.
            if u.is_a("IfcSIUnit") and u.UnitType == "LENGTHUNIT":
                length_unit = f"{u.Prefix}.{u.Name}" if u.Prefix else u.Name

    has_map_conversion = bool(f.by_type("IfcMapConversion"))

    site_ref_lat = site_ref_long = None
    sites = f.by_type("IfcSite")
    if sites:
        site_ref_lat = _dms_to_decimal(sites[0].RefLatitude)
        site_ref_long = _dms_to_decimal(sites[0].RefLongitude)

    return IfcModel(
        source_path=path,
        schema=f.schema,
        length_unit=length_unit,
        has_map_conversion=has_map_conversion,
        site_ref_lat=site_ref_lat,
        site_ref_long=site_ref_long,
        elements=extract_elements(f),
    )


def attach_ifc_model(project, ifc_model: IfcModel) -> None:
    """Sets `Project.ifc_model` — project-level, unlike
    `dxf_source.attach_dxf_sheets`'s per-sheet numeric-suffix join, since
    one IFC export covers the whole model, not one sheet. No matching
    logic to get wrong here; kept as its own function anyway, for the
    same reason `attach_dxf_sheets` is: so a caller doesn't reach into
    `Project` internals directly, and so this stays the one place that
    changes if `Project.ifc_model` ever needs to become a list."""

    project.ifc_model = ifc_model
