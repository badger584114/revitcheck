"""markup/pdf_markup.py — PLANNING.md §8. Synthetic cases for the
mechanics (tag order, box-vs-point-marker shape selection, the
bbox=None "counted but not drawn" rule), plus a real-sample assertion
against the full drafting-check real_issues fixture (conftest.py) —
proves this actually opens and annotates the real PDF end to end, not
just a synthetic stand-in.
"""

import sys
from pathlib import Path

import fitz

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from pdfchecker.checks.issue import Issue  # noqa: E402
from pdfchecker.ir import BBox, Project  # noqa: E402
from pdfchecker.markup.pdf_markup import render_markup  # noqa: E402


def _synthetic_pdf(tmp_path, n_pages=1, width=600.0, height=800.0) -> str:
    doc = fitz.open()
    for _ in range(n_pages):
        doc.new_page(width=width, height=height)
    path = str(tmp_path / "synthetic.pdf")
    doc.save(path)
    doc.close()
    return path


def _issue(rule_id, category, page_index, bbox, severity="medium", suggested_fix=None, description="x"):
    return Issue(
        rule_id=rule_id,
        category=category,
        sheet_no="S1",
        page_index=page_index,
        description=description,
        bbox=bbox,
        severity=severity,
        suggested_fix=suggested_fix,
    )


def test_box_drawn_for_a_real_region_bbox(tmp_path):
    path = _synthetic_pdf(tmp_path)
    project = Project(source_path=path, sheets=[])
    issue = _issue("spelling.en_gb", "spelling", 0, BBox(100, 100, 150, 115), suggested_fix={"word": "x", "corrected": "y"})
    out = str(tmp_path / "out.pdf")

    render_markup(project, [issue], out)

    doc = fitz.open(out)
    page = doc[0]  # kept alive explicitly — annots are unbound once their page is GC'd
    kinds = {a.type[1] for a in page.annots()}
    assert "Square" in kinds
    assert "Circle" not in kinds


def test_circle_drawn_for_a_point_bbox(tmp_path):
    path = _synthetic_pdf(tmp_path)
    project = Project(source_path=path, sheets=[])
    # A zero-area bbox — checks/geometry.py's _setout_point_bbox real shape
    # for a single reconstructed point (PR #13).
    issue = _issue(
        "geometry.setout_reconstruction", "geometry", 0, BBox(300, 300, 300, 300),
        severity="high", suggested_fix={"delta_mm": 12.3},
    )
    out = str(tmp_path / "out.pdf")

    render_markup(project, [issue], out)

    doc = fitz.open(out)
    page = doc[0]
    kinds = {a.type[1] for a in page.annots()}
    assert "Circle" in kinds
    assert "Square" not in kinds


def test_bbox_none_counted_but_not_drawn(tmp_path):
    path = _synthetic_pdf(tmp_path)
    project = Project(source_path=path, sheets=[])
    issue = _issue("revision.sequential_numbering", "revision", 0, None, suggested_fix={"missing": [3]})
    out = str(tmp_path / "out.pdf")

    report = render_markup(project, [issue], out)

    assert len(report) == 1
    assert report[0].rendered is False
    assert report[0].tag == "#001"
    assert report[0].note == "Rev: missing [3]"
    doc = fitz.open(out)
    assert list(doc[0].annots()) == []


def test_tags_ordered_by_page_then_severity():
    # Doesn't need a real PDF at all — this only exercises the ordering
    # logic, which happens before any page is touched.
    import tempfile

    with tempfile.TemporaryDirectory() as d:
        path = _synthetic_pdf(Path(d), n_pages=2)
        project = Project(source_path=path, sheets=[])
        issues = [
            _issue("title_block.required_fields_present", "title_block", 1, BBox(0, 0, 10, 10), severity="low"),
            _issue("title_block.required_fields_present", "title_block", 0, BBox(0, 0, 10, 10), severity="low"),
            _issue("title_block.required_fields_present", "title_block", 0, BBox(0, 0, 10, 10), severity="high"),
        ]
        report = render_markup(project, issues, str(Path(d) / "out.pdf"))
        # page 0's "high" first, then page 0's "low", then page 1's "low"
        assert [(r.page_index, r.severity) for r in report] == [(0, "high"), (0, "low"), (1, "low")]
        assert [r.tag for r in report] == ["#001", "#002", "#003"]


def test_note_text_includes_the_tag(tmp_path):
    path = _synthetic_pdf(tmp_path)
    project = Project(source_path=path, sheets=[])
    issue = _issue(
        "cross_sheet.reference_resolves", "cross_sheet", 0, BBox(50, 50, 60, 60),
        severity="high", suggested_fix={"ref": "Detail 4/2871024"},
    )
    out = str(tmp_path / "out.pdf")

    render_markup(project, [issue], out)

    doc = fitz.open(out)
    freetexts = [a for a in doc[0].annots() if a.type[1] == "FreeText"]
    assert len(freetexts) == 1
    assert freetexts[0].info.get("content") == "No match: Detail 4/2871024 #001"


# --- against the real sample -----------------------------------------------


def test_real_sample_markup_end_to_end(real_issues, project, tmp_path):
    # The real drafting-check issue set (conftest.py's session-scoped
    # fixture) run through the whole pipeline — proves this opens and
    # annotates the actual 37-page PDF, not just a synthetic stand-in.
    out = str(tmp_path / "marked_up.pdf")
    report = render_markup(project, real_issues, out)

    assert len(report) == len(real_issues)
    # Every tag is unique and sequential.
    assert [r.tag for r in report] == [f"#{i:03d}" for i in range(1, len(report) + 1)]

    rendered_pages = {r.page_index for r in report if r.rendered}
    doc = fitz.open(out)
    total_annots = sum(len(list(doc[i].annots())) for i in rendered_pages)
    # Two annotations per rendered issue (the marker + its note).
    assert total_annots == 2 * sum(1 for r in report if r.rendered)
