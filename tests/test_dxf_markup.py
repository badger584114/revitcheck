"""markup/dxf_markup.py — PLANNING.md §8's DXF/DWG redline export.
Synthetic cases for the mechanics (shared tag order with pdf_markup.py,
box-vs-point-marker shape selection, the various "nothing to draw"
cases), plus a real-sample assertion against sheet 2871051's real DXF —
proves this actually opens and annotates a real DWG-converted file end
to end, not just a synthetic ezdxf.new() stand-in.
"""

import sys
from pathlib import Path

import ezdxf
import fitz

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from pdfchecker.checks.issue import Issue  # noqa: E402
from pdfchecker.extraction.dxf_source import ingest_dxf  # noqa: E402
from pdfchecker.ir import BBox, Project, Sheet, TitleBlock  # noqa: E402
from pdfchecker.markup.dxf_markup import render_dxf_markup  # noqa: E402

SAMPLE_PDF = str(
    Path(__file__).resolve().parent.parent / "samples" / "BR06" / "T2DPAA-T2D-C3S-BR-DRG-101000.pdf"
)
SAMPLE_DXF = str(
    Path(__file__).resolve().parent.parent
    / "samples"
    / "BR06"
    / "dxf"
    / "T2DPAA-T2D-C3S-BR-DRG-101051_0.dxf"
)


def _synthetic_dxf(tmp_path, name="synthetic.dxf") -> str:
    # ezdxf.new() already creates a default paper-space "Layout1".
    doc = ezdxf.new("R2018", setup=True)
    path = str(tmp_path / name)
    doc.saveas(path)
    return path


def _sheet(page_index: int, dxf_path: str | None, page_height=800.0, sheet_no="S1") -> Sheet:
    return Sheet(
        page_index=page_index,
        page_width=600.0,
        page_height=page_height,
        title_block=TitleBlock(fields={"sheet_no": sheet_no}),
        revision_schedule=[],
        tables=[],
        words=[],
        paths=[],
        raw_text="",
        dxf_sheet=ingest_dxf(dxf_path) if dxf_path else None,
    )


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


def _redline_entities(doc):
    layout = doc.layouts.get("Layout1")
    return [e for e in layout if e.dxf.layer == "Z-QC-REDLINE"]


def test_box_drawn_for_a_real_region_bbox(tmp_path):
    dxf_path = _synthetic_dxf(tmp_path)
    sheet = _sheet(0, dxf_path)
    project = Project(source_path="x.pdf", sheets=[sheet])
    issue = _issue("spelling.en_gb", "spelling", 0, BBox(100, 100, 150, 115), suggested_fix={"word": "x", "corrected": "y"})

    report = render_dxf_markup(project, [issue], str(tmp_path / "out"))

    assert len(report) == 1
    assert report[0].rendered is True
    out = ezdxf.readfile(report[0].output_path)
    kinds = {e.dxftype() for e in _redline_entities(out)}
    assert "LWPOLYLINE" in kinds
    assert "CIRCLE" not in kinds
    assert "MULTILEADER" in kinds


def test_circle_drawn_for_a_point_bbox(tmp_path):
    dxf_path = _synthetic_dxf(tmp_path)
    sheet = _sheet(0, dxf_path)
    project = Project(source_path="x.pdf", sheets=[sheet])
    # A zero-area bbox — same "point marker" convention as pdf_markup.py.
    issue = _issue(
        "geometry.setout_reconstruction", "geometry", 0, BBox(300, 300, 300, 300),
        severity="high", suggested_fix={"delta_mm": 12.3},
    )

    report = render_dxf_markup(project, [issue], str(tmp_path / "out"))

    out = ezdxf.readfile(report[0].output_path)
    kinds = {e.dxftype() for e in _redline_entities(out)}
    assert "CIRCLE" in kinds
    assert "LWPOLYLINE" not in kinds


def test_bbox_none_counted_but_not_rendered(tmp_path):
    dxf_path = _synthetic_dxf(tmp_path)
    sheet = _sheet(0, dxf_path)
    project = Project(source_path="x.pdf", sheets=[sheet])
    issue = _issue("revision.sequential_numbering", "revision", 0, None, suggested_fix={"missing": [3]})

    report = render_dxf_markup(project, [issue], str(tmp_path / "out"))

    assert len(report) == 1
    assert report[0].rendered is False
    assert report[0].output_path is None
    assert report[0].tag == "#001"
    assert report[0].note == "Rev: missing [3]"
    # No file at all — nothing was ever drawn on this sheet.
    assert list(Path(tmp_path / "out").glob("*.dxf")) == []


def test_sheet_with_no_dxf_counterpart_counted_but_not_rendered(tmp_path):
    # A real case per CLAUDE.md: some PDF sheets (index/locality/general
    # notes) have no matching DWG at all.
    sheet = _sheet(0, dxf_path=None)
    project = Project(source_path="x.pdf", sheets=[sheet])
    issue = _issue("spelling.en_gb", "spelling", 0, BBox(0, 0, 10, 10), suggested_fix={"word": "x"})

    report = render_dxf_markup(project, [issue], str(tmp_path / "out"))

    assert report[0].rendered is False
    assert report[0].output_path is None


def test_issue_page_index_with_no_matching_sheet_counted_but_not_rendered(tmp_path):
    project = Project(source_path="x.pdf", sheets=[])
    issue = _issue("spelling.en_gb", "spelling", 0, BBox(0, 0, 10, 10), suggested_fix={"word": "x"})

    report = render_dxf_markup(project, [issue], str(tmp_path / "out"))

    assert report[0].rendered is False


def test_shared_tag_order_matches_pdf_markup(tmp_path):
    # markup/tags.py's assign_tags is the shared contract — this only
    # exercises DXF's own use of it (page, then severity, then rule_id),
    # matching pdf_markup.py's test_tags_ordered_by_page_then_severity.
    dxf0 = _synthetic_dxf(tmp_path, "sheet0.dxf")
    dxf1 = _synthetic_dxf(tmp_path, "sheet1.dxf")
    sheets = [_sheet(0, dxf0, sheet_no="S0"), _sheet(1, dxf1, sheet_no="S1")]
    project = Project(source_path="x.pdf", sheets=sheets)
    issues = [
        _issue("title_block.required_fields_present", "title_block", 1, BBox(0, 0, 10, 10), severity="low"),
        _issue("title_block.required_fields_present", "title_block", 0, BBox(0, 0, 10, 10), severity="low"),
        _issue("title_block.required_fields_present", "title_block", 0, BBox(0, 0, 10, 10), severity="high"),
    ]

    report = render_dxf_markup(project, issues, str(tmp_path / "out"))

    assert [(r.page_index, r.severity) for r in report] == [(0, "high"), (0, "low"), (1, "low")]
    assert [r.tag for r in report] == ["#001", "#002", "#003"]


def test_note_includes_tag_in_multileader_content(tmp_path):
    dxf_path = _synthetic_dxf(tmp_path)
    sheet = _sheet(0, dxf_path)
    project = Project(source_path="x.pdf", sheets=[sheet])
    issue = _issue(
        "cross_sheet.reference_resolves", "cross_sheet", 0, BBox(50, 50, 60, 60),
        severity="high", suggested_fix={"ref": "Detail 4/2871024"},
    )

    report = render_dxf_markup(project, [issue], str(tmp_path / "out"))

    out = ezdxf.readfile(report[0].output_path)
    mleaders = [e for e in _redline_entities(out) if e.dxftype() == "MULTILEADER"]
    assert len(mleaders) == 1
    assert mleaders[0].context.mtext.default_content == "No match: Detail 4/2871024 #001"


def test_multiple_issues_on_one_sheet_share_one_output_file(tmp_path):
    dxf_path = _synthetic_dxf(tmp_path)
    sheet = _sheet(0, dxf_path)
    project = Project(source_path="x.pdf", sheets=[sheet])
    issues = [
        _issue("spelling.en_gb", "spelling", 0, BBox(0, 0, 10, 10), suggested_fix={"word": "a"}),
        _issue("spelling.en_gb", "spelling", 0, BBox(20, 20, 30, 30), suggested_fix={"word": "b"}),
    ]

    report = render_dxf_markup(project, issues, str(tmp_path / "out"))

    assert report[0].output_path == report[1].output_path
    out = ezdxf.readfile(report[0].output_path)
    assert len(_redline_entities(out)) == 4  # 2 boxes + 2 multileaders


# --- against the real sample -----------------------------------------------


def test_real_sample_dxf_markup_end_to_end(tmp_path):
    with fitz.open(SAMPLE_PDF) as pdf:
        real_page_height = pdf[14].rect.height

    sheet = _sheet(14, SAMPLE_DXF, page_height=real_page_height, sheet_no="2871051")
    project = Project(source_path=SAMPLE_PDF, sheets=[sheet])
    issues = [
        _issue(
            "geometry.dimension_consistency", "geometry", 14, BBox(100, 200, 140, 210),
            severity="high", suggested_fix={"drawn_mm": 3.42, "stated_mm": 3.40},
        ),
        _issue(
            "geometry.setout_reconstruction", "geometry", 14, BBox(300, 400, 300, 400),
            severity="high", suggested_fix={"delta_mm": 12.3},
        ),
    ]

    report = render_dxf_markup(project, issues, str(tmp_path / "out"))

    assert len(report) == 2
    assert all(r.rendered for r in report)
    assert report[0].output_path == report[1].output_path

    out = ezdxf.readfile(report[0].output_path)
    entities = _redline_entities(out)
    kinds = [e.dxftype() for e in entities]
    assert kinds.count("MULTILEADER") == 2
    assert kinds.count("LWPOLYLINE") == 1
    assert kinds.count("CIRCLE") == 1
    # The real sheet's own pre-existing content survives untouched
    # alongside the new layer — this opened and re-saved a real 1430+
    # entity file, not a stripped-down copy.
    layout = out.layouts.get(out.layouts.get("Layout1").name)
    assert len(list(layout)) > 1430
