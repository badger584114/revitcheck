"""Geometry check engine — PLANNING.md §5, Stage 3. §5a's drawn-vs-stated
dimensional consistency (`geometry.dimension_consistency`) and a first
slice of §5b's structure reconstruction (`geometry.setout_reconstruction`
— bearing + dimension-chain pile setout reconstruction, see
extraction/setout_reconstruction.py's docstring for the real convention
this is calibrated against and why it doesn't just compare DXF model
geometry to the schedule) are built.

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

import re

from pdfchecker.checks.catalog import RuleConfig, register
from pdfchecker.checks.issue import Issue
from pdfchecker.extraction.setout_reconstruction import reconstruct_sheet
from pdfchecker.ir import DimensionEntity, Project

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

        for point in reconstruct_sheet(sheet, config):
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
