"""Revision-cloud detection (extraction/revision_clouds.py) — PLANNING.md
§4's third revision cross-check, unblocked once
samples/T2DPAA-T2D-C3S-BR-DRG-101000_1.pdf gave real cloud/triangle
geometry to calibrate against (see that module's docstring). Same split
as test_references.py/test_ingest_sample.py: synthetic PathEntity/TextWord
objects here exercise the clustering algorithm's branches precisely;
real-sample assertions (the `amended_project` fixture, conftest.py) confirm
it holds up against the actual sample it was calibrated on.
"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from pdfchecker.extraction.revision_clouds import detect_revision_clouds  # noqa: E402
from pdfchecker.ir import BBox, PathEntity, TextWord  # noqa: E402

_RED = (1.0, 0.0, 0.0)


def _arc(x0: float, y0: float, w: float = 4.0, h: float = 4.0) -> PathEntity:
    return PathEntity(bbox=BBox(x0, y0, x0 + w, y0 + h), kind="s", stroke_width=0.6, color=_RED, curved=True)


def _line(x0: float, y0: float, x1: float, y1: float) -> PathEntity:
    return PathEntity(
        bbox=BBox(min(x0, x1), min(y0, y1), max(x0, x1), max(y0, y1)),
        kind="s",
        stroke_width=0.6,
        color=_RED,
        curved=False,
    )


def _cloud_arcs(x0: float, y0: float, n: int = 8) -> list[PathEntity]:
    # A chain of small touching arcs — mirrors how the real sample's
    # scallops are each a separate small object rather than one path.
    return [_arc(x0 + i * 4.0, y0) for i in range(n)]


def _triangle_lines(x0: float, y0: float, size: float = 20.0) -> list[PathEntity]:
    # Two sides + a baseline — same 3-object shape as the real sample's
    # tag triangle (module docstring point 3).
    return [
        _line(x0, y0 + size, x0 + size / 2, y0),
        _line(x0 + size / 2, y0, x0 + size, y0 + size),
        _line(x0, y0 + size, x0 + size, y0 + size),
    ]


def _word(text: str, x0: float, y0: float, w: float = 6.0, h: float = 10.0) -> TextWord:
    return TextWord(text=text, bbox=BBox(x0, y0, x0 + w, y0 + h))


def test_cloud_with_adjacent_triangle_resolves_tag():
    paths = _cloud_arcs(100, 100) + _triangle_lines(135, 95, size=20)
    words = [_word("1", 141, 100, w=6, h=8)]  # inside the triangle bbox
    clouds = detect_revision_clouds(words, paths)
    assert len(clouds) == 1
    assert clouds[0].tag == "1"
    assert clouds[0].arc_count == 8
    assert clouds[0].triangle_bbox is not None


def test_too_few_arcs_not_treated_as_cloud():
    # Below _MIN_ARC_COUNT — mirrors the real sample's confirmed false
    # positive (a single red curved object elsewhere on the page that
    # wasn't a cloud).
    paths = _cloud_arcs(100, 100, n=3)
    assert detect_revision_clouds([], paths) == []


def test_non_red_curves_ignored():
    black_arcs = [
        PathEntity(bbox=BBox(100 + i * 4.0, 100, 104 + i * 4.0, 104), kind="s", stroke_width=0.6, color=(0.0, 0.0, 0.0), curved=True)
        for i in range(8)
    ]
    assert detect_revision_clouds([], black_arcs) == []


def test_four_line_box_not_mistaken_for_triangle():
    # The real sample has 4-line red rectangle highlight boxes elsewhere
    # on the same sheets (around a callout sheet number) — confirms the
    # exactly-3-lines rule keeps those from being read as a tag triangle,
    # even with a plausible tag digit sitting right there.
    box = [
        _line(135, 95, 155, 95),
        _line(155, 95, 155, 115),
        _line(155, 115, 135, 115),
        _line(135, 115, 135, 95),
    ]
    paths = _cloud_arcs(100, 100) + box
    words = [_word("1", 141, 100, w=6, h=8)]
    clouds = detect_revision_clouds(words, paths)
    assert len(clouds) == 1
    assert clouds[0].tag is None  # no valid triangle found nearby
    assert clouds[0].triangle_bbox is None


def test_triangle_too_far_from_cloud_not_paired():
    paths = _cloud_arcs(100, 100) + _triangle_lines(1000, 1000, size=20)
    words = [_word("1", 1006, 1005, w=6, h=8)]
    clouds = detect_revision_clouds(words, paths)
    assert len(clouds) == 1
    assert clouds[0].tag is None
    assert clouds[0].triangle_bbox is None


# --- against the real sample ---------------------------------------------


def test_no_clouds_on_clean_sample(project):
    # SAMPLE (conftest.py) is the "AMEND No. 0 everywhere" set — nothing
    # to detect.
    all_clouds = [c for s in project.sheets for c in s.revision_clouds]
    assert all_clouds == []


def test_clouds_detected_on_amended_sheets(amended_project):
    # Sheets 2871032/2871033/2871051/2871056 carry AMEND No. 1 with real
    # drawn revision clouds — spot-checked visually (page 13's render)
    # before writing the detector.
    for sheet_no in ["2871032", "2871033", "2871051", "2871056"]:
        sheet = amended_project.sheet_by_no(sheet_no)
        assert sheet is not None
        assert sheet.revision_clouds, f"expected at least one detected cloud on sheet {sheet_no}"
        assert all(c.tag == "1" for c in sheet.revision_clouds), (
            f"every cloud on sheet {sheet_no} should tag the sheet's own AMEND No. 1"
        )
