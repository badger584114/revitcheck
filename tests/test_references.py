"""Cross-sheet reference graph extraction (extraction/references.py) —
PLANNING.md §3/§4. Real-sample coverage lives in test_ingest_sample.py
(shares the session-scoped `project` fixture); this file is synthetic,
hand-built Sheets exercising each resolution-algorithm branch precisely,
same split as test_checks.py uses for the rule catalog.
"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from pdfchecker.extraction.references import build_references  # noqa: E402
from pdfchecker.ir import BBox, Project, Sheet, TextWord, TitleBlock  # noqa: E402


def _sheet(sheet_no: str, words: list[TextWord]) -> Sheet:
    return Sheet(
        page_index=0,
        page_width=2384.0,
        page_height=1684.0,
        title_block=TitleBlock(fields={"sheet_no": sheet_no}),
        revision_schedule=[],
        tables=[],
        words=words,
        paths=[],
        raw_text="",
    )


def _word(text: str, x0: float, y0: float, w: float = 10.0, h: float = 12.0) -> TextWord:
    return TextWord(text=text, bbox=BBox(x0, y0, x0 + w, y0 + h))


def test_callout_resolves_to_matching_title_on_another_sheet():
    plan = _sheet(
        "1000001",
        [
            _word("3", 100, 100),
            _word("1000002", 95, 116, w=45),
        ],
    )
    section = _sheet(
        "1000002",
        [
            _word("SECTION", 200, 50, w=55),
            _word("3", 259, 50),
        ],
    )
    project = Project(source_path="synthetic", sheets=[plan, section])
    refs = build_references(project)

    assert len(refs) == 1
    ref = refs[0]
    assert ref.resolved
    assert ref.ref_type == "section"
    assert ref.tag == "3"
    assert ref.source_sheet_no == "1000001"
    assert ref.target_sheet_no == "1000002"
    assert ref.confidence == 1.0


def test_callout_to_nonexistent_sheet_unresolved_high_confidence_gap():
    plan = _sheet(
        "1000003",
        [
            _word("9", 100, 100),
            _word("9999999", 95, 116, w=45),
        ],
    )
    project = Project(source_path="synthetic", sheets=[plan])
    refs = build_references(project)

    assert len(refs) == 1
    assert refs[0].resolved is False
    assert refs[0].confidence == 0.0  # referenced sheet doesn't exist anywhere in the set


def test_callout_to_real_sheet_with_no_matching_title_unresolved_lower_confidence():
    plan = _sheet(
        "1000004",
        [
            _word("5", 100, 100),
            _word("1000005", 95, 116, w=45),
        ],
    )
    other = _sheet("1000005", [_word("SOME OTHER TEXT", 10, 10, w=80)])
    project = Project(source_path="synthetic", sheets=[plan, other])
    refs = build_references(project)

    assert len(refs) == 1
    assert refs[0].resolved is False
    assert refs[0].confidence == 0.3  # sheet is real, just no matching tag found on it


def test_view_own_identifying_marker_is_not_a_reference():
    # "DETAIL A" title, with a marker beside it stacked above its own
    # sheet number — the view's own self-tag (module docstring), not a
    # callout needing resolution.
    sheet = _sheet(
        "1000006",
        [
            _word("DETAIL", 300, 200, w=45),
            _word("A", 349, 200),
            _word("1000006", 344, 216, w=45),
        ],
    )
    project = Project(source_path="synthetic", sheets=[sheet])
    refs = build_references(project)
    assert refs == []


def test_general_note_reference_not_mistaken_for_a_title():
    # "REFER SECTION 1" — a body-text cross-reference sentence, not this
    # sheet's own view title (PLANNING.md §4's deferred general-note-
    # reference category); a real callout elsewhere pointing at tag '1'
    # on this sheet must NOT resolve against it.
    notes_sheet = _sheet(
        "1000007",
        [
            _word("REFER", 50, 50, w=40),
            _word("SECTION", 95, 50, w=55),
            _word("1", 155, 50),
        ],
    )
    callout_sheet = _sheet(
        "1000008",
        [
            _word("1", 400, 400),
            _word("1000007", 395, 416, w=45),
        ],
    )
    project = Project(source_path="synthetic", sheets=[notes_sheet, callout_sheet])
    refs = build_references(project)

    assert len(refs) == 1
    assert refs[0].resolved is False
    assert refs[0].confidence == 0.3  # sheet 1000007 is real, but carries no genuine title


def test_sheet_reference_inside_a_general_note_sentence_not_read_as_a_marker():
    # "... SHEET No. 1000009 TO 1000010 ..." — a general-notes
    # cross-reference sentence; the sheet numbers in it must not be
    # treated as marker sheet-number words just because a stray tag-
    # shaped word happens to sit nearby.
    sheet = _sheet(
        "1000011",
        [
            _word("No.", 50, 50, w=20),
            _word("1000009", 75, 50, w=45),
            _word("2", 70, 66),
        ],
    )
    project = Project(source_path="synthetic", sheets=[sheet])
    refs = build_references(project)
    assert refs == []
