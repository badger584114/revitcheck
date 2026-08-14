"""markup/notes.py — PLANNING.md §8's per-rule terse `Label: payload`
notes. One test per real rule_id/variant in the actual catalog
(checks/*.py), built from the exact suggested_fix shape each rule really
produces (see notes.py's own docstring for why each needed a small
addition) — not synthetic shapes invented for this test file.
"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from pdfchecker.checks.issue import Issue  # noqa: E402
from pdfchecker.markup.notes import markup_note  # noqa: E402


def _issue(rule_id: str, category: str, suggested_fix=None) -> Issue:
    return Issue(
        rule_id=rule_id,
        category=category,
        sheet_no="2871051",
        page_index=0,
        description="irrelevant to markup_note",
        suggested_fix=suggested_fix,
    )


def test_title_block_missing_field():
    issue = _issue("title_block.required_fields_present", "title_block", {"field": "amend_no"})
    assert markup_note(issue) == "Missing: amend_no"


def test_spelling_with_correction():
    issue = _issue("spelling.en_gb", "spelling", {"word": "color", "corrected": "colour"})
    assert markup_note(issue) == "Spelling: colour"


def test_spelling_no_correction_found():
    issue = _issue("spelling.en_gb", "spelling", {"word": "xkcdz", "corrected": None})
    assert markup_note(issue) == "Spelling: xkcdz?"


def test_cross_sheet_no_match():
    issue = _issue("cross_sheet.reference_resolves", "cross_sheet", {"ref": "Detail 4/2871024"})
    assert markup_note(issue) == "No match: Detail 4/2871024"


def test_revision_duplicate_numbers():
    issue = _issue("revision.sequential_numbering", "revision", {"dupes": [2]})
    assert markup_note(issue) == "Rev: duplicate [2]"


def test_revision_missing_numbers():
    issue = _issue("revision.sequential_numbering", "revision", {"missing": [3]})
    assert markup_note(issue) == "Rev: missing [3]"


def test_revision_cloud_tag_mismatch():
    issue = _issue(
        "revision.cloud_matches_schedule", "revision", {"cloud_tag": "5", "schedule_rev_ids": ["0", "1"]}
    )
    assert markup_note(issue) == "Rev: cloud has no table row"


def test_revision_cloud_no_tag_readable():
    # This real variant (checks/revisions.py) carries no suggested_fix at
    # all — the fallback inside _revision_note, not the generic
    # unregistered-rule_id fallback.
    issue = _issue("revision.cloud_matches_schedule", "revision", None)
    assert markup_note(issue) == "Rev: unreadable tag"


def test_revision_schedule_vs_title_block():
    issue = _issue(
        "revision.schedule_matches_title_block",
        "revision",
        {"title_block_amend_no": "1", "schedule_latest_rev_id": "2"},
    )
    assert markup_note(issue) == "Rev: table 2 ≠ title block 1"


def test_dimension_consistency():
    issue = _issue(
        "geometry.dimension_consistency", "geometry", {"drawn_mm": 3420.0, "stated_mm": 3400.0, "delta_mm": -20.0}
    )
    assert markup_note(issue) == "Drawn 3420mm ≠ dim 3400mm"


def test_setout_reconstruction_delta():
    issue = _issue("geometry.setout_reconstruction", "geometry", {"delta_mm": 45.2})
    assert markup_note(issue) == "Setout Δ45.2mm"


def test_setout_reconstruction_no_bearing():
    issue = _issue("geometry.setout_reconstruction", "geometry", {"status": "no_bearing"})
    assert markup_note(issue) == "Setout: no bearing"


def test_ifc_setout_consistency_delta():
    issue = _issue(
        "geometry.ifc_setout_consistency", "geometry",
        {"delta_mm": 45.2, "ifc_global_id": "abc123", "ifc_display_name": "Pile"},
    )
    assert markup_note(issue) == "Setout Δ45.2mm (IFC)"


def test_ifc_setout_consistency_no_nearby_pile():
    issue = _issue("geometry.ifc_setout_consistency", "geometry", {"nearest_distance_m": 3.4})
    assert markup_note(issue) == "Setout: no nearby IFC pile"


def test_ifc_setout_consistency_no_piles_in_model():
    issue = _issue("geometry.ifc_setout_consistency", "geometry", {"ifc_element_count": 45, "candidate_count": 0})
    assert markup_note(issue) == "Setout: IFC model has no piles"


def test_unregistered_rule_id_falls_back_to_category():
    issue = _issue("some.future_rule", "spec", {"whatever": 1})
    assert markup_note(issue) == "Spec: see report"
