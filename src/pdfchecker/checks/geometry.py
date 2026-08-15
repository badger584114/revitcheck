"""Geometry check engine — PLANNING.md §5, Stage 3. §5a's drawn-vs-stated
dimensional consistency (`geometry.dimension_consistency`), a first
slice of §5b's structure reconstruction (`geometry.setout_reconstruction`
— bearing + dimension-chain pile setout reconstruction, see
extraction/setout_reconstruction.py's docstring for the real convention
this is calibrated against and why it doesn't just compare DXF model
geometry to the schedule), and a first slice of §5's proposed third
geometry source (`geometry.ifc_setout_consistency`, added 2026-08-12 —
cross-checks each sheet's reconstructed setout point against the
project's uploaded IFC model, via a schema-general pile-shape heuristic
rather than this firm's Revit naming; see this file's own docstring on
that rule, and extraction/ifc_source.py's docstring for the real IFC
findings it's built on) are built, plus `geometry.ifc_superstructure_
coverage` (added 2026-08-15 — the "non-pile superstructure" gap §5's IFC
subsection flagged as still open: two more schema-general shape
heuristics, `_is_thin_horizontal_plate`/`_is_elongated_beam`, for
deck-slab-like and abutment-beam-like elements. A coverage check only,
not a magnitude/position cross-check like the pile rule — see that
rule's own docstring for why a real numeric comparison isn't buildable
yet without guessing an unconfirmed correspondence).

Consumes `Sheet.dxf_sheet` (extraction/dxf_source.py's `DxfSheet`,
attached via `attach_dxf_sheets` — see that module's docstring for the
numeric-suffix sheet-correspondence join this relies on). A sheet with
no `dxf_sheet` attached (a drafting-only run, or simply no matching
DWG/DXF found for that sheet) is silently skipped, not an error — this
matches PLANNING.md §2's scope model: geometry Issues just don't appear
for a sheet without DXF data, the same way drafting rules don't fail
when a project has no client spec.

Scoped to `dim_type=0` (linear/rotated) dimensions with a numeric
override only — extraction/dxf_source.py's docstring (point 4) found
real DXF dimensions that don't fit either constraint: non-linear
dimtypes, and letter-tag overrides ("A", "B"...) keying into a separate
bar-mark/schedule table rather than a rounded buildable length. Both are
skipped, not guessed at — same "skip rather than guess" principle as the
cross-sheet reference graph and revision-cloud detection elsewhere in
this codebase.

`geometry.dimension_consistency`'s Issues carry a real page-space `bbox`
as of 2026-08-14, via `extraction/dxf_pdf_transform.py`'s per-viewport
DXF-space -> PDF-page-space transform (PLANNING.md §8 — see that
module's docstring for the real calibration this is built on).
`geometry.setout_reconstruction`/`geometry.ifc_setout_consistency` get
one too, added the same day via `_setout_point_bbox` below — built once
`extraction/setout_reconstruction.py`'s `SetoutReconstructionPoint`
started retaining each point's local DXF-space position (`local_point`,
see its own docstring). Scoped to the single-sheet case only: BR08's
real cross-sheet setout-precedence case (the schedule sheet and the DXF
geometry living on two different sheets) deliberately gets `bbox=None`
instead, since transforming through the *geometry* sheet's viewports
would produce a page-space point for a *different* page than the one
this Issue's `page_index` actually reports — see `_setout_point_bbox`'s
docstring for the full reasoning.

`geometry.setout_reconstruction` and `geometry.ifc_setout_consistency`
both resolve each schedule sheet's geometry source via `_resolve_geometry_
sheet` (added 2026-08-14, the real BR08 cross-sheet case — see
extraction/setout_reconstruction.py's docstring) before calling
`reconstruct_sheet` — usually the schedule sheet's own DXF, but a printed
cross-reference note can point at a different sheet entirely. Every Issue
either rule produces carries `geometry_sheet_no` in `suggested_fix` so
which sheet's dimension chain actually drove the result stays auditable
even when it isn't the schedule sheet's own.
"""

from __future__ import annotations

import math
import re
import weakref
from typing import Callable

from pdfchecker.checks.catalog import RuleConfig, register
from pdfchecker.checks.issue import Issue
from pdfchecker.extraction.dxf_pdf_transform import dimension_bbox, model_to_pdf_point
from pdfchecker.extraction.setout_reconstruction import find_geometry_sheet_no, reconstruct_sheet
from pdfchecker.ir import BBox, DimensionEntity, IfcElement, Project, Sheet

# extraction/dxf_source.py's _INSUNITS-resolved unit strings, converted
# to a millimeter multiplier — tolerances are specified in mm
# (PLANNING.md §5's rounding-grid convention is mm-native, and this
# sample's own dimstyle names encode "mm"), while a DimensionEntity's
# `measurement` is in the sheet's own real-world unit ($INSUNITS).
_MM_PER_UNIT = {
    "mm": 1.0,
    "cm": 10.0,
    "m": 1000.0,
    "km": 1_000_000.0,
    "in": 25.4,
    "ft": 304.8,
}

_LINEAR_DIM_TYPE = 0

# AutoCAD/Revit inserts a trailing Unicode LEFT-TO-RIGHT MARK (U+200E) on
# at least some override text (confirmed on the real sample — see
# extraction/dxf_source.py's docstring) that looks invisible in a
# terminal/editor; strip it and other zero-width/bidi format characters
# before attempting to parse a number, or a genuinely valid override
# would fail to parse for a reason with no visible cause.
_FORMAT_CHARS_RE = re.compile(r"[\u200b-\u200f\u202a-\u202e]")


def _parse_stated_mm(text: str) -> float | None:
    """Parses a dimension's override text as a millimeter value, or
    returns `None` if it isn't a clean number — the letter-tag/schedule-
    key case (see module docstring), not a rounding override. Real
    overrides seen so far are bare numbers with no unit suffix (the
    dimstyle name itself already encodes the unit, e.g.
    `'Dimension_Standard_O__mm_'`) — no suffix-stripping attempted here;
    extend this if a future sample shows one."""

    cleaned = _FORMAT_CHARS_RE.sub("", text).strip()
    try:
        return float(cleaned)
    except ValueError:
        return None


def _tier(dim: DimensionEntity, config: RuleConfig) -> str:
    # Automatic promotion for any dimension that's an edge in the §5b
    # reconstruction graph isn't possible yet (§5b isn't built) — layer
    # membership is the only classification source for now.
    return "setout_critical" if dim.layer in config.setout_critical_layers else "default"


def _tolerance_mm(tier: str, config: RuleConfig) -> float:
    grid = (
        config.rounding_grid_setout_critical_mm
        if tier == "setout_critical"
        else config.rounding_grid_default_mm
    )
    return grid / 2 + config.measurement_epsilon_mm


@register("geometry.dimension_consistency")
def check_dimension_consistency(project: Project, config: RuleConfig) -> list[Issue]:
    issues = []
    for sheet in project.sheets:
        dxf_sheet = sheet.dxf_sheet
        if dxf_sheet is None:
            continue  # no DXF counterpart for this sheet — drafting-only run, or unmatched

        mm_per_unit = _MM_PER_UNIT.get(dxf_sheet.units)
        if mm_per_unit is None:
            continue  # unresolvable unit — don't guess a conversion factor

        for dim in dxf_sheet.dimensions:
            if dim.dim_type != _LINEAR_DIM_TYPE or dim.stated_text is None:
                continue
            stated_mm = _parse_stated_mm(dim.stated_text)
            if stated_mm is None:
                continue  # not a numeric override — e.g. a bar-mark/schedule-table letter tag

            drawn_mm = dim.measurement * mm_per_unit
            delta_mm = stated_mm - drawn_mm
            tier = _tier(dim, config)
            tolerance_mm = _tolerance_mm(tier, config)
            if abs(delta_mm) <= tolerance_mm:
                continue

            issues.append(
                Issue(
                    rule_id="geometry.dimension_consistency",
                    category="geometry",
                    sheet_no=sheet.sheet_no,
                    page_index=sheet.page_index,
                    description=(
                        f"Dimension stated as {stated_mm:g}mm but the drawn geometry measures "
                        f"{drawn_mm:.3f}mm ({delta_mm:+.3f}mm, tolerance ±{tolerance_mm:.3f}mm, {tier})"
                    ),
                    # extraction/dxf_pdf_transform.py (built 2026-08-14, PLANNING.md
                    # §8) — a page-space bbox via the per-viewport DXF-space ->
                    # PDF-page-space transform. None only if this dimension's
                    # points don't fall inside any of the sheet's viewport windows
                    # (skip rather than guess a wrong location) — the DXF-space
                    # location stays in suggested_fix either way, so nothing is
                    # silently lost when that happens.
                    bbox=dimension_bbox(dxf_sheet, dim, sheet.page_height),
                    severity="high",
                    suggested_fix={
                        "drawn_mm": round(drawn_mm, 3),
                        "stated_mm": stated_mm,
                        "delta_mm": round(delta_mm, 3),
                        "dxf_location": dim.dim_line_point.to_dict(),
                    },
                )
            )
    return issues


# geometry.setout_reconstruction and geometry.ifc_setout_consistency both
# need the same per-sheet extraction/setout_reconstruction.py
# reconstruct_sheet() result. A normal run has both rules enabled
# together (RuleConfig.enabled_rule_ids defaults to every registered
# rule, checks/catalog.py), which was recomputing the whole schedule-
# parse + dimension-chain-walk pipeline twice per sheet with the second
# pass's result discarded — fixed 2026-08-12 with the cache below.
#
# `Sheet` is a plain (non-frozen) dataclass, so it's unhashable — can't
# key a dict or WeakKeyDictionary on it directly (confirmed: `hash(sheet)`
# raises `TypeError`). Keyed on `id(sheet)` instead, with a `weakref.ref`
# finalizer that proactively removes the entry the moment that `Sheet` is
# garbage-collected — this is what actually makes an id-keyed cache safe:
# without it, a freed sheet's id could later be reused by an unrelated
# object and silently return someone else's cached reconstruction.
# Nested per-`id(config)` since reconstruction genuinely depends on
# config's tolerance/matching parameters, not just the sheet.
#
# `_reconstruction_refs` exists only to keep each `weakref.ref` itself
# alive — a real bug caught while testing this fix: a bare
# `weakref.ref(sheet, callback)` with no Python-level reference to the
# returned `ref` object gets garbage-collected right away (nothing else
# points to it), and once the `ref` object itself is gone its callback
# can never fire, silently defeating the whole cleanup mechanism. Keeping
# it in this dict, popped by the same callback that pops the cache entry,
# ties its lifetime to the cache entry it's cleaning up.
#
# Cache key is `(id(schedule_sheet), id(geometry_sheet))`, not just
# `id(sheet)` — added 2026-08-14 for the real BR08 cross-sheet case
# (extraction/setout_reconstruction.py's docstring): the schedule and
# geometry can now be two different `Sheet`s, and two different geometry
# sources for the same schedule sheet must not collide in the cache. The
# single-sheet case (BR06) naturally becomes `(id(sheet), id(sheet))` —
# no behavior change there. Both ids in the pair need their own weakref
# guard, since either sheet could independently be freed.
_reconstruction_cache: dict[tuple[int, int], dict[int, list]] = {}
_reconstruction_refs: dict[int, weakref.ref] = {}


def _cached_reconstruct_sheet(
    sheet: Sheet, config: RuleConfig, geometry_sheet: Sheet | None = None
) -> list:
    geom = geometry_sheet or sheet
    key = (id(sheet), id(geom))

    def _register(obj: Sheet) -> None:
        obj_id = id(obj)
        if obj_id in _reconstruction_refs:
            return

        def _cleanup(_ref, freed_id=obj_id):
            _reconstruction_refs.pop(freed_id, None)
            for stale_key in [k for k in _reconstruction_cache if freed_id in k]:
                _reconstruction_cache.pop(stale_key, None)

        _reconstruction_refs[obj_id] = weakref.ref(obj, _cleanup)

    if key not in _reconstruction_cache:
        _register(sheet)
        if geom is not sheet:
            _register(geom)
        _reconstruction_cache[key] = {}
    by_config = _reconstruction_cache[key]
    config_key = id(config)
    if config_key not in by_config:
        by_config[config_key] = reconstruct_sheet(sheet, config, geom if geom is not sheet else None)
    return by_config[config_key]


def _resolve_geometry_sheet(schedule_sheet: Sheet, project: Project) -> Sheet:
    """Schedule sheet -> the `Sheet` whose DXF geometry should actually be
    used for reconstruction: the cross-referenced sheet named in a real
    setout-precedence note (extraction/setout_reconstruction.py's
    `find_geometry_sheet_no` — the real BR08 2873042->2873041 case, see
    that module's docstring), if one exists, resolves via
    `project.sheet_by_no`, and itself carries DXF data — else falls back
    to `schedule_sheet` itself. Coverage, not silence: a schedule sheet
    with no note, or a note pointing at a sheet missing from the project
    or without DXF, still gets a best-effort attempt against its own DXF
    (which may be `None`, same as before this function existed) rather
    than being skipped outright before that's even checked."""

    target_no = find_geometry_sheet_no(schedule_sheet.raw_text)
    if target_no is not None:
        target = project.sheet_by_no(target_no)
        if target is not None and target.dxf_sheet is not None:
            return target
    return schedule_sheet


def _setout_point_bbox(sheet: Sheet, geometry_sheet: Sheet, point) -> BBox | None:
    """A `SetoutReconstructionPoint`'s page-space bbox — `extraction/
    dxf_pdf_transform.py`'s transform (PLANNING.md §8) applied to
    `point.local_point` (added 2026-08-14 alongside this function, the
    real "follow-up" flagged when that transform was first wired into
    `geometry.dimension_consistency`).

    Only when `geometry_sheet is sheet`: in the real BR08 cross-sheet
    case, `point.local_point` is in a *different* sheet's DXF model
    space, transformed through *that* sheet's own viewports/paper space
    — but this Issue's `page_index` reports the schedule sheet, not the
    geometry sheet, so the resulting page-space point would land on the
    wrong page. Skipping rather than guessing a mismatched location is
    the same convention used everywhere else in this codebase; a real
    fix would need `Issue` to carry a secondary location, which doesn't
    exist yet. `None` also for an `"unmatched_pile"` point (no
    `local_point` at all — never found in the DXF to begin with) or a
    point whose local position doesn't fall inside any viewport."""

    if point.local_point is None or geometry_sheet is not sheet or geometry_sheet.dxf_sheet is None:
        return None
    predicted = model_to_pdf_point(geometry_sheet.dxf_sheet, point.local_point, sheet.page_height)
    if predicted is None:
        return None
    x, y = predicted
    return BBox(x0=x, y0=y, x1=x, y1=y)


# Human-readable notes for the non-"reconstructed" statuses
# extraction/setout_reconstruction.py's `reconstruct_sheet` reports —
# every schedule point ends up with *some* status (never silently
# dropped, per §5b step 6), so each needs a message even when it isn't a
# geometric mismatch.
_STATUS_DESCRIPTIONS = {
    "unmatched_pile": "in the schedule but not identified in the DXF drawing",
    "no_bearing": "no readable bearing found for its setout chain",
    "no_origin": "its setout chain could not be anchored to a known-coordinate setout point",
}


@register("geometry.setout_reconstruction")
def check_setout_reconstruction(project: Project, config: RuleConfig) -> list[Issue]:
    issues = []
    for sheet in project.sheets:
        geometry_sheet = _resolve_geometry_sheet(sheet, project)
        if geometry_sheet.dxf_sheet is None:
            continue  # no DXF counterpart for this sheet, directly or via a cross-reference

        cross_sheet_note = (
            f" (geometry reconstructed from sheet {geometry_sheet.sheet_no}'s dimension chain, "
            f"per its setout-precedence note)"
            if geometry_sheet.sheet_no != sheet.sheet_no
            else ""
        )

        for point in _cached_reconstruct_sheet(sheet, config, geometry_sheet):
            if point.status != "reconstructed":
                issues.append(
                    Issue(
                        rule_id="geometry.setout_reconstruction",
                        category="geometry",
                        sheet_no=sheet.sheet_no,
                        page_index=sheet.page_index,
                        description=(
                            f"Setout point {point.point_id}: "
                            f"{_STATUS_DESCRIPTIONS[point.status]}{cross_sheet_note}"
                        ),
                        bbox=_setout_point_bbox(sheet, geometry_sheet, point),
                        severity="low",
                        suggested_fix={
                            "status": point.status,
                            "geometry_sheet_no": point.geometry_sheet_no,
                        },
                    )
                )
                continue

            delta_e = point.derived.easting - point.stated.easting
            delta_n = point.derived.northing - point.stated.northing
            delta_mm = (delta_e**2 + delta_n**2) ** 0.5 * 1000
            if delta_mm <= config.survey_tolerance_mm:
                continue

            issues.append(
                Issue(
                    rule_id="geometry.setout_reconstruction",
                    category="geometry",
                    sheet_no=sheet.sheet_no,
                    page_index=sheet.page_index,
                    description=(
                        f"Setout point {point.point_id}: schedule states E {point.stated.easting:.3f} "
                        f"N {point.stated.northing:.3f}, but reconstructing from the sheet's setout "
                        f"point + bearing + dimension chain gives E {point.derived.easting:.3f} "
                        f"N {point.derived.northing:.3f} ({delta_mm:.1f}mm, tolerance "
                        f"{config.survey_tolerance_mm:.1f}mm) — check the manually-entered bearing/"
                        f"dimension overrides and whether the schedule was regenerated after the "
                        f"last drawing change{cross_sheet_note}"
                    ),
                    bbox=_setout_point_bbox(sheet, geometry_sheet, point),
                    severity="high",
                    suggested_fix={
                        "stated": point.stated.to_dict(),
                        "derived": point.derived.to_dict(),
                        "delta_mm": round(delta_mm, 1),
                        "geometry_sheet_no": point.geometry_sheet_no,
                    },
                )
            )
    return issues


def _is_slender_vertical(element: IfcElement, config: RuleConfig) -> bool:
    """A schema-general shape heuristic — small horizontal footprint,
    tall — that identifies pile-like elements from world-space geometry
    alone, no `Name`/`PredefinedType` involved. Deliberately built this
    way after an early mistake: an earlier pass on this project assumed
    BR06's IFC model had no piles at all, because it only sampled a few
    `IfcSlab` elements and missed the real ones (the user caught this,
    they could see the piles in Navisworks). All 28 real piles on that
    sample are `IfcSlab`/`PredefinedType='BASESLAB'` with "PILE" in
    their Revit `Name` — but confirmed this geometry-only check finds
    exactly the same 28 elements, zero mismatches against the Name-text
    search, with no reliance on this firm's class/naming choice for
    piles. See extraction/ifc_source.py's docstring for the full
    account, including the real pile geometry this was calibrated
    against (0.75m x 0.75m footprint, 10.55m tall)."""

    dx = element.bbox_max.x - element.bbox_min.x
    dy = element.bbox_max.y - element.bbox_min.y
    dz = element.bbox_max.z - element.bbox_min.z
    footprint = max(dx, dy)
    if footprint <= 0 or footprint >= config.ifc_pile_footprint_max_m:
        return False
    return dz / footprint >= config.ifc_pile_aspect_ratio_min


def _is_thin_horizontal_plate(element: IfcElement, config: RuleConfig) -> bool:
    """A schema-general shape heuristic for deck-slab-like elements —
    large horizontal footprint, thin — the geometric *opposite* of
    `_is_slender_vertical`'s pile heuristic above. PLANNING.md §5's IFC
    subsection names this as the real open gap: "a check for the
    non-pile superstructure (deck, abutment beams) ... a different
    shape signature ... would be needed."

    Calibrated 2026-08-15 against BR06's real IFC model, the same one
    the pile heuristic uses. The deck slab itself doesn't carry direct
    geometry on this export — `IfcSlab`/`FLOOR` elements named "Deck
    Slab"/"Deck Asphalt" have `Representation=None` and decompose
    (`IsDecomposedBy`) into child `IfcBuildingElementPart` elements
    (Revit's per-pour-stage split) that carry the real geometry — six of
    them, footprint (max horizontal extent) 7.56-23.07m, `dz/footprint`
    0.0106-0.0402 (dx stays close to a ~10m deck width across every
    pour; dy is the pour's along-bridge-span length, which varies).
    Confirmed clear of two other real shapes on the same model: pile
    caps (~1.1m footprint, ratio ~1.0-1.09 — footprint alone already
    excludes them) and abutment beams/headstocks (~10.5-13.3m footprint,
    ratio 0.100-0.124 — see `_is_elongated_beam` below; the gap between
    0.0402 and 0.100 is where `ifc_deck_aspect_ratio_max`'s default
    (0.06) sits, with margin on both sides)."""

    dx = element.bbox_max.x - element.bbox_min.x
    dy = element.bbox_max.y - element.bbox_min.y
    dz = element.bbox_max.z - element.bbox_min.z
    footprint = max(dx, dy)
    if footprint < config.ifc_deck_footprint_min_m:
        return False
    return dz / footprint <= config.ifc_deck_aspect_ratio_max


def _is_elongated_beam(element: IfcElement, config: RuleConfig) -> bool:
    """A schema-general shape heuristic for abutment-beam/headstock-like
    elements — large footprint like a deck slab, but noticeably thicker
    relative to that footprint. Calibrated 2026-08-15 against the same
    BR06 model as `_is_thin_horizontal_plate`: sampled 4 of the model's
    68 real `IfcBeam` elements (all named `"...ABUTMENT EAST/WEST_BR06_
    <n>"`, the same Revit family across the sample — a same-population
    sample, not a search for a rare subtype, unlike the pile-search
    under-sampling mistake `extraction/ifc_source.py`'s docstring
    records), all landing at footprint 10.51-13.32m, ratio 0.100-0.124 —
    consistent enough with each other, and clear of deck slabs' 0.0106-
    0.0402, to treat as a real, distinct shape band rather than
    overlap. Not re-confirmed against BR08."""

    dx = element.bbox_max.x - element.bbox_min.x
    dy = element.bbox_max.y - element.bbox_min.y
    dz = element.bbox_max.z - element.bbox_min.z
    footprint = max(dx, dy)
    if footprint < config.ifc_beam_footprint_min_m:
        return False
    ratio = dz / footprint
    return config.ifc_beam_aspect_ratio_min <= ratio <= config.ifc_beam_aspect_ratio_max


def _horizontal_centroid(element: IfcElement) -> tuple[float, float]:
    return (
        (element.bbox_min.x + element.bbox_max.x) / 2,
        (element.bbox_min.y + element.bbox_max.y) / 2,
    )


def _ifc_issue(
    sheet: Sheet, description: str, severity: str, suggested_fix: dict, bbox: BBox | None = None
) -> Issue:
    """Shared Issue-construction for `check_ifc_setout_consistency`'s
    outcome branches below — they'd otherwise copy-paste the same
    rule_id/category/sheet_no/page_index quintet four times. `bbox`
    defaults to `None` for the one caller with no specific point to
    locate (the whole-sheet "no pile-shaped elements at all" coverage
    Issue) — every per-point caller passes `_setout_point_bbox`'s result
    explicitly instead of relying on the default."""

    return Issue(
        rule_id="geometry.ifc_setout_consistency",
        category="geometry",
        sheet_no=sheet.sheet_no,
        page_index=sheet.page_index,
        description=description,
        bbox=bbox,
        severity=severity,
        suggested_fix=suggested_fix,
    )


@register("geometry.ifc_setout_consistency")
def check_ifc_setout_consistency(project: Project, config: RuleConfig) -> list[Issue]:
    """Cross-checks each sheet's independently-*reconstructed* setout
    point (extraction/setout_reconstruction.py's `reconstruct_sheet` —
    derived from the sheet's own printed setout-point origin + bearing +
    dimension chain, already in real Easting/Northing) against the
    nearest available pile-like element in the project's uploaded IFC
    model.

    Deliberately NOT a DXF-vs-IFC coordinate-space comparison — a DWG/DXF
    export is one sheet's paper-space view, not model space, so there is
    no single fixed transform between a sheet's local drafting
    coordinates and the IFC's world coordinates for this (or generally
    any) project to rely on (confirmed by the user; see
    extraction/ifc_source.py's docstring). Comparing the already-
    reconstructed real-world point instead sidesteps that entirely: both
    sides of this comparison are already expressed in real Easting/
    Northing, independently arrived at (one from the sheet's printed
    bearing/dimension inputs, the other from the coordinated 3D model),
    which is also a stronger check than comparing to the *schedule* —
    the schedule is itself a separate generated artifact that can go
    stale (see `geometry.setout_reconstruction`'s docstring), while a
    live IFC model reflects the current coordinated design.

    Matching is **one-to-one across the whole project**, not each
    reconstructed point matched to its own independent "nearest" —
    fixed 2026-08-12 after real review: the original version let two
    different points both claim the same IFC element, or a point snap to
    a neighboring structure's pile, since nothing removed a matched
    element from the pool. Greedy nearest-pair-first assignment (below)
    fixes double-claiming; it does NOT fix "matched the wrong structure's
    pile" in general, because there's no confirmed IFC-side signal to
    scope by. `extraction/setout_reconstruction.py`'s DXF-side matching
    solves that same class of ambiguity by reading nearby sheet text
    (`"ABUTMENT A"`/`"ABUTMENT B"`) near the schedule's own `LOCATION`
    column — but this firm's real IFC pile `Name` text
    (`"...CAST-IN-PLACE PILE - 0750:<tag>"`) carries no comparable
    structure/location information to key off (confirmed on the only
    real IFC sample calibrated against). Real pile spacing on that
    sample (~8m between abutments) is comfortably outside the default
    `ifc_match_max_distance_m` (2.0m), so this gap isn't live there —
    flagged as a known limitation for a tighter-spaced project, not
    solved.

    **An IFC-side location signal was found 2026-08-15, but not wired in
    here yet.** No IFC pile carries structure/location text, but it
    doesn't need to — real geography works instead, and needs no reading
    of a page's north arrow, since both sides of the comparison already
    live in real Easting/Northing: a reconstructed LOCATION group's mean
    position (from `extraction/setout_reconstruction.py`'s own
    `parse_pile_locations`) and any IFC element's own world-space
    centroid are directly comparable. Confirmed decisively on sheet
    2871051/BR06 (see `check_ifc_superstructure_coverage`'s docstring for
    the real figures — sub-0.1m agreement identifying `"ABUTMENT A"` as
    the model's `"...WEST..."` group and `"ABUTMENT B"` as `"...EAST..."`,
    against a ~7.9m abutment separation). Schema-general — works off
    position alone, not either side's naming convention — so it would
    scope one-to-one assignment by nearest-LOCATION-group rather than
    project-wide nearest-element, closing this gap for a future
    tighter-spaced project. Left unbuilt for now since neither real
    sample makes the gap live; worth doing before trusting this rule on
    a project with closely-spaced structures.

    Only points with `status == "reconstructed"` are checked — a point
    that couldn't be reconstructed at all is already `geometry.
    setout_reconstruction`'s concern, not repeated here. A sheet with no
    IFC model attached (`project.ifc_model is None` — a drafting-only
    run, or no IFC uploaded) is silently skipped, same "Issues just
    don't appear" scope model as `dxf_sheet is None` above — but an IFC
    model that *is* attached and simply has no pile-shaped elements at
    all is NOT silently skipped (see the empty-`candidate_positions`
    branch below): those are different situations an engineer needs to
    tell apart, per CLAUDE.md's "partial reconstruction should report a
    confidence/coverage indicator rather than failing silently.\""""

    if project.ifc_model is None:
        return []

    candidates = [e for e in project.ifc_model.elements if _is_slender_vertical(e, config)]
    candidate_positions = [(_horizontal_centroid(e), e) for e in candidates]

    sheet_points: list[tuple[Sheet, Sheet, object]] = []
    for sheet in project.sheets:
        geometry_sheet = _resolve_geometry_sheet(sheet, project)
        if geometry_sheet.dxf_sheet is None:
            continue
        for point in _cached_reconstruct_sheet(sheet, config, geometry_sheet):
            if point.status == "reconstructed":
                sheet_points.append((sheet, geometry_sheet, point))

    if not sheet_points:
        return []

    if not candidate_positions:
        # Real, not hypothetical: BR06's own IFC model needed sampling all
        # 45 IfcSlab elements to find its 28 piles (see
        # extraction/ifc_source.py's docstring for that mistake) — a
        # project whose piles this shape heuristic genuinely can't find
        # shouldn't read in the output as "checked, all clean." One Issue
        # per sheet that had something to check, not per point. `Sheet` is
        # unhashable (a plain, non-frozen dataclass — see
        # _cached_reconstruct_sheet's comment above), so this counts by
        # page_index, not by using `Sheet` as a dict key directly.
        counts: dict[int, tuple[Sheet, int]] = {}
        for sheet, _, _ in sheet_points:
            prior = counts.get(sheet.page_index)
            counts[sheet.page_index] = (sheet, (prior[1] if prior else 0) + 1)
        return [
            _ifc_issue(
                sheet,
                (
                    f"The attached IFC model has {len(project.ifc_model.elements)} elements but "
                    f"none match the pile-shape heuristic (footprint < "
                    f"{config.ifc_pile_footprint_max_m}m, height/footprint > "
                    f"{config.ifc_pile_aspect_ratio_min}) — this check found nothing to cross-check "
                    f"this sheet's {count} reconstructed setout point(s) against, not a confirmation "
                    f"that they match"
                ),
                "low",
                {
                    "ifc_element_count": len(project.ifc_model.elements),
                    "candidate_count": 0,
                    "geometry_sheet_no": _resolve_geometry_sheet(sheet, project).sheet_no,
                },
            )
            for sheet, count in counts.values()
        ]

    # Nearest-available one-to-one assignment: every (point, candidate)
    # pair, closest first, each point/element claimed at most once. Not
    # globally distance-optimal (a true min-cost assignment would be), but
    # simple, auditable, and — the actual bug this replaces — never lets
    # two points share one IFC element.
    pairs = []
    for sheet, _, point in sheet_points:
        px, py = point.derived.easting, point.derived.northing
        for (cx, cy), elem in candidate_positions:
            pairs.append((math.hypot(cx - px, cy - py), sheet, point, elem))
    pairs.sort(key=lambda p: p[0])

    claimed_points: set[tuple[int, str]] = set()
    claimed_elems: set[str] = set()
    assignment: dict[tuple[int, str], tuple[float, IfcElement]] = {}
    for dist, sheet, point, elem in pairs:
        key = (sheet.page_index, point.point_id)
        if key in claimed_points or elem.global_id in claimed_elems:
            continue
        claimed_points.add(key)
        claimed_elems.add(elem.global_id)
        assignment[key] = (dist, elem)

    issues = []
    for sheet, geometry_sheet, point in sheet_points:
        key = (sheet.page_index, point.point_id)
        px, py = point.derived.easting, point.derived.northing
        bbox = _setout_point_bbox(sheet, geometry_sheet, point)

        result = assignment.get(key)
        if result is None:
            # Every candidate this point could reach was claimed by a
            # closer point first — fewer pile-shaped IFC elements than
            # reconstructable schedule points, a distinct real case from
            # "no candidates in the whole model" above.
            nearest_dist = min(math.hypot(cx - px, cy - py) for (cx, cy), _ in candidate_positions)
            issues.append(
                _ifc_issue(
                    sheet,
                    (
                        f"Setout point {point.point_id}: every pile-like IFC element was already "
                        f"matched to a different, closer setout point — this project has fewer "
                        f"pile-shaped IFC elements than reconstructable schedule points"
                    ),
                    "low",
                    {
                        "derived": point.derived.to_dict(),
                        "nearest_distance_m": round(nearest_dist, 2),
                        "geometry_sheet_no": point.geometry_sheet_no,
                    },
                    bbox,
                )
            )
            continue

        dist, elem = result
        if dist > config.ifc_match_max_distance_m:
            issues.append(
                _ifc_issue(
                    sheet,
                    (
                        f"Setout point {point.point_id}: reconstructed position has no matching "
                        f"pile-like element in the IFC model within "
                        f"{config.ifc_match_max_distance_m:.1f}m (nearest available is "
                        f"{dist:.1f}m away) — may be unmodeled, or modeled with a different shape "
                        f"this heuristic doesn't recognize"
                    ),
                    "low",
                    {
                        "derived": point.derived.to_dict(),
                        "nearest_distance_m": round(dist, 2),
                        "geometry_sheet_no": point.geometry_sheet_no,
                    },
                    bbox,
                )
            )
            continue

        delta_mm = dist * 1000
        if delta_mm <= config.ifc_setout_tolerance_mm:
            continue

        issues.append(
            _ifc_issue(
                sheet,
                (
                    f"Setout point {point.point_id}: reconstructing from the sheet's setout point + "
                    f"bearing + dimension chain gives E {px:.3f} N {py:.3f}, but the nearest available "
                    f"pile-like element in the IFC model ({elem.global_id}) sits {delta_mm:.1f}mm away "
                    f"(tolerance {config.ifc_setout_tolerance_mm:.1f}mm) — check whether the issued "
                    f"drawing and the coordinated 3D model have diverged"
                ),
                "high",
                {
                    "derived": point.derived.to_dict(),
                    "ifc_global_id": elem.global_id,
                    "ifc_display_name": elem.display_name,
                    "delta_mm": round(delta_mm, 1),
                    "geometry_sheet_no": point.geometry_sheet_no,
                },
                bbox,
            )
        )
    return issues


# §5's IFC subsection "non-pile superstructure" label -> shape predicate.
# A tuple, not a dict, so the report order below is stable and matches the
# order this module's docstring/PLANNING.md discusses them in.
_SUPERSTRUCTURE_SHAPES: tuple[tuple[str, Callable[[IfcElement, RuleConfig], bool]], ...] = (
    ("deck/slab-shaped (large flat plate)", _is_thin_horizontal_plate),
    ("abutment-beam-shaped (large elongated member)", _is_elongated_beam),
)


@register("geometry.ifc_superstructure_coverage")
def check_ifc_superstructure_coverage(project: Project, config: RuleConfig) -> list[Issue]:
    """PLANNING.md §5's real, previously-open "non-pile superstructure"
    gap: an IFC-based check for structure that has neither a
    `DIMENSION` override to compare (§5a) nor a setout table to
    reconstruct against (§5b) — deck slabs, abutment beams. `geometry.
    ifc_setout_consistency` can't reach these, since it only checks
    already-*reconstructed* setout points, and reconstruction only
    exists for schedule-tabulated points (piles on the real samples so
    far).

    **Deliberately a coverage check, not a magnitude/position
    cross-check like the pile rule.** That would need an independently-
    derived real-world value to compare IFC geometry against, the same
    role the setout table + bearing/dimension-chain reconstruction plays
    for piles — and nothing like that has been confirmed for deck/beam
    elements yet:

    - The obvious candidate — abutment-to-abutment span from `geometry.
      setout_reconstruction`'s reconstructed pile positions, vs. deck
      slab length — was considered and rejected: a pile centerline span
      isn't the same real-world distance as a deck length (abutment
      backwall setback, bearing offsets), by an amount this codebase has
      no confirmed figure for, so any tolerance around that comparison
      would itself be a guess, not a calibrated check.
    - The real IFC data adds a second reason to distrust an easy
      DXF<->IFC *name* match here: the schedule's `LOCATION` column says
      `"ABUTMENT A"`/`"ABUTMENT B"` (see `extraction/
      setout_reconstruction.py`), but this project's real IFC abutment
      beam elements are named `"...ABUTMENT EAST/WEST_BR06_<n>"` — A/B
      vs. EAST/WEST, with no shared vocabulary between the two naming
      conventions. **Resolved 2026-08-15, not by name-matching but by
      geography** — the user pointed out every sheet carries a north
      arrow, so a bridge's abutments can always be oriented to real
      compass directions; turns out this project doesn't even need a
      north arrow read off the page to use that, since both sides of the
      comparison are *already* real-world coordinates: `geometry.
      setout_reconstruction`'s reconstructed points, grouped by the
      schedule's own `LOCATION` column, have a real mean Easting per
      abutment (confirmed: `"ABUTMENT A"` 278437.35mE, `"ABUTMENT B"`
      278445.23mE on sheet 2871051), and the real IFC abutment-beam
      elements' own world-space centroid gives the same figure directly
      (confirmed: `"...EAST..."` beams average 278445.22mE, `"...
      WEST..."` beams average 278437.34mE) — sub-0.1m agreement, decisive
      against the ~7.9m separation between the two abutments. So `A =
      WEST` and `B = EAST` on this real project, derived, not asserted —
      and the mechanism generalizes beyond names entirely: any IFC
      element's real-world Easting can be compared to a reconstructed
      LOCATION group's mean Easting, schema-general, no reliance on
      either side's naming convention holding on a different project.
      This closes the naming-mismatch half of the blocker (and, more
      valuably, gives `check_ifc_setout_consistency`'s own documented
      "no confirmed IFC-side location signal to scope by" gap a real
      answer, not just this rule) — not yet wired into either rule's
      matching logic, since neither real sample's pile/beam spacing
      makes the gap live today; see `check_ifc_setout_consistency`'s
      docstring for that gap's current status. The *other* half of this
      rule's own blocker — no independently-derived real-world value to
      compare deck/beam *magnitude* against — is untouched by this and
      stays open.
    - Matching a specific DXF `DIMENSION` on a general-arrangement/
      elevation sheet to "the" overall deck length, with no schedule or
      label to anchor on, is exactly the kind of spatial-proximity
      guessing PLANNING.md §1 already found unreliable for PDF dimension
      reconstruction — not attempted here without first exploring real
      sheet data to see if anything better than proximity exists.

    So this rule reports only what's actually confirmed: whether the
    attached IFC model has *any* element matching each shape category in
    `_SUPERSTRUCTURE_SHAPES` at all. Zero matches for a category is
    reported as one low-severity, project-wide Issue (not a per-sheet or
    per-point one — there's no natural sheet to pin a whole-model finding
    to, so it's attached to the project's first sheet with a description
    that says so explicitly) — same "confidence, not silent" principle
    as `check_ifc_setout_consistency`'s own "zero pile-shaped elements"
    branch, which this mirrors. A real limitation worth being explicit
    about: a project that genuinely has no deck (e.g. a retaining wall)
    would also get the "no deck-shaped element found" Issue — this rule
    has no way yet to tell "absent because unmodeled/wrong shape" apart
    from "absent because this structure never had one", same class of
    gap the pile-coverage branch already accepts.

    Finding *something* in a category isn't itself reported as an Issue
    — silence there means "found, nothing more to say yet", not "clean";
    it's exactly as far as a confirmed check can go until the magnitude-
    comparison gap above is closed."""

    if project.ifc_model is None or not project.sheets:
        return []

    anchor_sheet = project.sheets[0]
    issues = []
    for label, predicate in _SUPERSTRUCTURE_SHAPES:
        count = sum(1 for e in project.ifc_model.elements if predicate(e, config))
        if count > 0:
            continue
        issues.append(
            Issue(
                rule_id="geometry.ifc_superstructure_coverage",
                category="geometry",
                sheet_no=anchor_sheet.sheet_no,
                page_index=anchor_sheet.page_index,
                description=(
                    f"Project-wide finding (not specific to this sheet): the attached IFC model "
                    f"({len(project.ifc_model.elements)} elements) has no {label} element — either "
                    "genuinely absent from this structure, unmodeled, or shaped differently than this "
                    "heuristic expects (see geometry.py's _is_thin_horizontal_plate/_is_elongated_beam "
                    "docstrings for the real figures this is calibrated against)"
                ),
                severity="low",
                suggested_fix={"shape_category": label, "ifc_element_count": len(project.ifc_model.elements)},
            )
        )
    return issues
