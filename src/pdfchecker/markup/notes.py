"""Terse markup notes — PLANNING.md §8's `Label: payload` table. Every
markup on a sheet carries one line built here, not the Issue's full
`description` (§8: "A sheet with twenty issues is unreadable if every
one gets a full-sentence description").

One builder per real `rule_id`/variant seen in the actual rule catalog
(`checks/*.py`) — not a generic "format the description" fallback, per
§8's "Every rule in the catalog defines its own note template alongside
its check logic." Kept centralized here rather than inside each check
module for now (the rule catalog has no per-rule note-template slot yet)
— a real, deliberate simplification for this first markup-export slice,
not an oversight; moving each builder next to its own rule is
straightforward later if the catalog's shape grows a slot for it.

A few existing rules needed a small `suggested_fix` addition (the actual
data a terse note needs) alongside this module — `title_block.py`'s
missing-field name, `spelling.py`'s flagged word, `revisions.py`'s
duplicate/missing revision-number lists — none carried before because
nothing consumed `suggested_fix` for anything but the full report until
now.
"""

from __future__ import annotations

from pdfchecker.checks.issue import Issue


def _title_block_note(issue: Issue) -> str:
    field = (issue.suggested_fix or {}).get("field", "field")
    return f"Missing: {field}"


def _spelling_note(issue: Issue) -> str:
    fix = issue.suggested_fix or {}
    corrected = fix.get("corrected")
    if corrected:
        return f"Spelling: {corrected}"
    # No confident suggestion found (checks/spelling.py: sc.correction()
    # can return None) — still name the flagged word rather than a bare
    # "Spelling:" label with nothing after it.
    return f"Spelling: {fix.get('word', '?')}?"


def _cross_sheet_note(issue: Issue) -> str:
    ref = (issue.suggested_fix or {}).get("ref")
    return f"No match: {ref}" if ref else "No match"


def _revision_note(issue: Issue) -> str:
    fix = issue.suggested_fix or {}
    if "dupes" in fix:
        return f"Rev: duplicate {fix['dupes']}"
    if "missing" in fix:
        return f"Rev: missing {fix['missing']}"
    if "cloud_tag" in fix:
        # The exact fixed phrase PLANNING.md §8's table itself uses as
        # this rule's example — the cloud's own tag is already legible
        # right next to the cloud on the sheet, so it isn't repeated here.
        return "Rev: cloud has no table row"
    if "title_block_amend_no" in fix:
        return f"Rev: table {fix['schedule_latest_rev_id']} ≠ title block {fix['title_block_amend_no']}"
    # revision.cloud_matches_schedule's other variant (no tag readable at
    # all near the cloud) carries no suggested_fix — nothing to build a
    # more specific note from.
    return "Rev: unreadable tag"


def _dimension_consistency_note(issue: Issue) -> str:
    fix = issue.suggested_fix or {}
    drawn_mm, stated_mm = fix.get("drawn_mm"), fix.get("stated_mm")
    if drawn_mm is None or stated_mm is None:
        return "Drawn ≠ dim"
    return f"Drawn {drawn_mm:g}mm ≠ dim {stated_mm:g}mm"


def _setout_reconstruction_note(issue: Issue) -> str:
    fix = issue.suggested_fix or {}
    if "delta_mm" in fix:
        return f"Setout Δ{fix['delta_mm']:g}mm"
    # The non-reconstructed statuses (extraction/setout_reconstruction.py's
    # SetoutReconstructionPoint.status) — nothing to measure a delta
    # against, but still worth naming which coverage gap this is.
    status = fix.get("status", "unresolved")
    return f"Setout: {status.replace('_', ' ')}"


def _ifc_setout_consistency_note(issue: Issue) -> str:
    fix = issue.suggested_fix or {}
    if "delta_mm" in fix:
        # Distinguished from geometry.setout_reconstruction's own "Setout
        # Δ" note (an "(IFC)" suffix) — both can legitimately appear
        # on the same sheet for the same pile, comparing it against two
        # different independent sources (the schedule vs. the 3D model).
        return f"Setout Δ{fix['delta_mm']:g}mm (IFC)"
    if "nearest_distance_m" in fix:
        return "Setout: no nearby IFC pile"
    # The whole-sheet "no pile-shaped elements in this model at all"
    # coverage Issue — not about one point, so no per-point value to show.
    return "Setout: IFC model has no piles"


def _ifc_superstructure_coverage_note(issue: Issue) -> str:
    fix = issue.suggested_fix or {}
    # shape_category is the long, human-readable form ("deck/slab-shaped
    # (large flat plate)") — checks/geometry.py's _SUPERSTRUCTURE_SHAPES
    # labels; drop the parenthetical for a terse note, same "label plus
    # the minimum info needed" rule as every other builder here.
    category = fix.get("shape_category", "element").split(" (")[0]
    return f"IFC: no {category}"


_NOTE_BUILDERS = {
    "title_block.required_fields_present": _title_block_note,
    "spelling.en_gb": _spelling_note,
    "cross_sheet.reference_resolves": _cross_sheet_note,
    "revision.schedule_matches_title_block": _revision_note,
    "revision.sequential_numbering": _revision_note,
    "revision.cloud_matches_schedule": _revision_note,
    "geometry.dimension_consistency": _dimension_consistency_note,
    "geometry.setout_reconstruction": _setout_reconstruction_note,
    "geometry.ifc_setout_consistency": _ifc_setout_consistency_note,
    "geometry.ifc_superstructure_coverage": _ifc_superstructure_coverage_note,
}


def markup_note(issue: Issue) -> str:
    """The terse `Label: payload` line for one Issue (PLANNING.md §8) —
    without the reference tag, which is assigned per markup set, not per
    Issue (`pdf_markup.py` appends it). Falls back to a generic
    `<Category>: see report` for any `rule_id` this module doesn't have a
    specific builder for yet — never raises, never silently produces an
    empty note, per this codebase's "report a coverage indicator, don't
    fail silently" convention applied to markup text itself."""

    builder = _NOTE_BUILDERS.get(issue.rule_id)
    if builder is not None:
        return builder(issue)
    return f"{issue.category.replace('_', ' ').title()}: see report"
