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
findings it's built on) are built.

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
"""

from __future__ import annotations

import math
import re
import weakref

from pdfchecker.checks.catalog import RuleConfig, register
from pdfchecker.checks.issue import Issue
from pdfchecker.extraction.setout_reconstruction import reconstruct_sheet
from pdfchecker.ir import DimensionEntity, IfcElement, Project, Sheet

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
                    # No page-space bbox yet — the DXF model-space -> PDF-page-space
                    # transform isn't built (PLANNING.md §8: needs a per-viewport
                    # mapping, not one per sheet). The DXF-space location is still
                    # reported below, just not translated to a page location —
                    # better than a wrong bbox, per this codebase's "confidence,
                    # not silent" convention.
                    bbox=None,
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
_reconstruction_cache: dict[int, dict[int, list]] = {}
_reconstruction_refs: dict[int, weakref.ref] = {}


def _cached_reconstruct_sheet(sheet: Sheet, config: RuleConfig) -> list:
    sheet_id = id(sheet)
    if sheet_id not in _reconstruction_cache:
        def _cleanup(_ref, sid=sheet_id):
            _reconstruction_cache.pop(sid, None)
            _reconstruction_refs.pop(sid, None)

        _reconstruction_refs[sheet_id] = weakref.ref(sheet, _cleanup)
        _reconstruction_cache[sheet_id] = {}
    by_config = _reconstruction_cache[sheet_id]
    config_key = id(config)
    if config_key not in by_config:
        by_config[config_key] = reconstruct_sheet(sheet, config)
    return by_config[config_key]


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
        if sheet.dxf_sheet is None:
            continue  # no DXF counterpart for this sheet

        for point in _cached_reconstruct_sheet(sheet, config):
            if point.status != "reconstructed":
                issues.append(
                    Issue(
                        rule_id="geometry.setout_reconstruction",
                        category="geometry",
                        sheet_no=sheet.sheet_no,
                        page_index=sheet.page_index,
                        description=(
                            f"Setout point {point.point_id}: "
                            f"{_STATUS_DESCRIPTIONS[point.status]}"
                        ),
                        bbox=None,
                        severity="low",
                        suggested_fix={"status": point.status},
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
                        f"last drawing change"
                    ),
                    bbox=None,
                    severity="high",
                    suggested_fix={
                        "stated": point.stated.to_dict(),
                        "derived": point.derived.to_dict(),
                        "delta_mm": round(delta_mm, 1),
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


def _horizontal_centroid(element: IfcElement) -> tuple[float, float]:
    return (
        (element.bbox_min.x + element.bbox_max.x) / 2,
        (element.bbox_min.y + element.bbox_max.y) / 2,
    )


def _ifc_issue(sheet: Sheet, description: str, severity: str, suggested_fix: dict) -> Issue:
    """Shared Issue-construction for `check_ifc_setout_consistency`'s
    outcome branches below — they'd otherwise copy-paste the same
    rule_id/category/sheet_no/page_index/bbox=None quintet three times."""

    return Issue(
        rule_id="geometry.ifc_setout_consistency",
        category="geometry",
        sheet_no=sheet.sheet_no,
        page_index=sheet.page_index,
        description=description,
        bbox=None,
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

    sheet_points: list[tuple[Sheet, object]] = []
    for sheet in project.sheets:
        if sheet.dxf_sheet is None:
            continue
        for point in _cached_reconstruct_sheet(sheet, config):
            if point.status == "reconstructed":
                sheet_points.append((sheet, point))

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
        for sheet, _ in sheet_points:
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
                {"ifc_element_count": len(project.ifc_model.elements), "candidate_count": 0},
            )
            for sheet, count in counts.values()
        ]

    # Nearest-available one-to-one assignment: every (point, candidate)
    # pair, closest first, each point/element claimed at most once. Not
    # globally distance-optimal (a true min-cost assignment would be), but
    # simple, auditable, and — the actual bug this replaces — never lets
    # two points share one IFC element.
    pairs = []
    for sheet, point in sheet_points:
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
    for sheet, point in sheet_points:
        key = (sheet.page_index, point.point_id)
        px, py = point.derived.easting, point.derived.northing

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
                    {"derived": point.derived.to_dict(), "nearest_distance_m": round(nearest_dist, 2)},
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
                    {"derived": point.derived.to_dict(), "nearest_distance_m": round(dist, 2)},
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
                },
            )
        )
    return issues
