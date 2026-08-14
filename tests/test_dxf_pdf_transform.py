"""DXF model-space -> PDF page-space transform (extraction/
dxf_pdf_transform.py) — PLANNING.md §8. Same split as every other
extraction module here: synthetic viewport/point cases for each
mechanism in isolation, plus real-sample assertions (sheet 2871051 /
samples/BR06/dxf/101051, next to its real PDF page) that cross-check the
transform against real, independently-placed content — see that
module's docstring for the real calibration this is built on and why
pile labels are the ground truth used below.
"""

import sys
from pathlib import Path

import fitz
import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from pdfchecker.extraction.dxf_pdf_transform import (  # noqa: E402
    dimension_bbox,
    find_viewport_for_point,
    model_to_paper,
    model_to_pdf_point,
    paper_to_pdf,
)
from pdfchecker.extraction.dxf_source import ingest_dxf  # noqa: E402
from pdfchecker.ir import DimensionEntity, Point3D, ViewportEntity  # noqa: E402

SAMPLES_DIR = Path(__file__).resolve().parent.parent / "samples" / "BR06"
DXF_PATH = str(SAMPLES_DIR / "dxf" / "T2DPAA-T2D-C3S-BR-DRG-101051_0.dxf")
PDF_PATH = str(SAMPLES_DIR / "T2DPAA-T2D-C3S-BR-DRG-101000.pdf")
PDF_PAGE_INDEX = 14  # sheet 2871051, same page this module's docstring calibrates against


def _pt(x: float, y: float) -> Point3D:
    return Point3D(x=x, y=y, z=0.0)


def _vp(
    ps_center=(0.0, 0.0), ps_width=10.0, ps_height=10.0, view_center=(0.0, 0.0), view_height=10.0, twist=0.0, id_=1
) -> ViewportEntity:
    return ViewportEntity(
        id=id_,
        ps_center=_pt(*ps_center),
        ps_width=ps_width,
        ps_height=ps_height,
        view_center_point=_pt(*view_center),
        view_height=view_height,
        view_twist_angle=twist,
    )


# --- find_viewport_for_point --------------------------------------------------


def test_finds_the_containing_viewport():
    vp = _vp(view_center=(100.0, 200.0), view_height=20.0, ps_width=10.0, ps_height=10.0)
    assert find_viewport_for_point([vp], _pt(105.0, 205.0)) is vp


def test_none_when_point_outside_every_viewport():
    vp = _vp(view_center=(100.0, 200.0), view_height=20.0)
    assert find_viewport_for_point([vp], _pt(1000.0, 1000.0)) is None


def test_tie_break_prefers_smallest_window():
    # Real content and "passthrough" viewports occupy disjoint coordinate
    # ranges on the sample (module docstring point 2) — this tie-break is
    # a documented fallback, not exercised by real data, but should still
    # do something sensible: prefer the tighter, more specific match.
    big = _vp(view_center=(0.0, 0.0), view_height=1000.0, ps_width=10.0, ps_height=10.0, id_=1)
    small = _vp(view_center=(0.0, 0.0), view_height=10.0, ps_width=10.0, ps_height=10.0, id_=2)
    assert find_viewport_for_point([big, small], _pt(0.0, 0.0)) is small


# --- model_to_paper -------------------------------------------------------------


def test_model_to_paper_scale_and_translate():
    # scale = ps_height / view_height = 0.5 / 50 = 0.01; point is 10 model
    # units right of view_center -> 0.1 paper units right of ps_center.
    vp = _vp(ps_center=(1.0, 2.0), ps_width=0.5, ps_height=0.5, view_center=(100.0, 200.0), view_height=50.0)
    paper_x, paper_y = model_to_paper(vp, _pt(110.0, 200.0))
    assert paper_x == pytest.approx(1.1)
    assert paper_y == pytest.approx(2.0)


def test_model_to_paper_applies_view_twist():
    # 90 degree twist: a point directly "above" view_center in model
    # space should land directly "right of" ps_center in paper space —
    # not confirmed against real data (every real view_twist_angle seen
    # is 0.0, module docstring), but the rotation math should still be
    # internally consistent.
    vp = _vp(ps_center=(0.0, 0.0), ps_width=1.0, ps_height=1.0, view_center=(0.0, 0.0), view_height=2.0, twist=90.0)
    paper_x, paper_y = model_to_paper(vp, _pt(0.0, 1.0))
    assert paper_x == pytest.approx(0.5, abs=1e-9)
    assert paper_y == pytest.approx(0.0, abs=1e-9)


# --- paper_to_pdf ----------------------------------------------------------------


def test_paper_to_pdf_fixed_scale_and_y_flip():
    # module docstring point 1: a fixed 72/25.4 pt/mm scale, no offset —
    # confirmed against the real sample's paper size vs. PDF page size.
    pdf_x, pdf_y = paper_to_pdf(0.1, 0.2, page_height_pt=1000.0)
    assert pdf_x == pytest.approx(0.1 * 1000 * 72 / 25.4)
    assert pdf_y == pytest.approx(1000.0 - 0.2 * 1000 * 72 / 25.4)


# --- model_to_pdf_point / dimension_bbox (synthetic) ----------------------------


def test_model_to_pdf_point_none_without_a_containing_viewport():
    assert model_to_pdf_point(_dxf_sheet_stub([]), _pt(0.0, 0.0), 1000.0) is None


def _dxf_sheet_stub(viewports):
    from pdfchecker.ir import DxfSheet

    return DxfSheet(source_path="x.dxf", dimensions=[], viewports=viewports, units="m")


def _dim(p1: Point3D, p2: Point3D, p3=None) -> DimensionEntity:
    return DimensionEntity(
        measurement=1.0,
        stated_text=None,
        dim_line_point=p3 or p1,
        ext_line1_origin=p1,
        ext_line2_origin=p2,
        dimstyle="Dimension_Standard_O__mm_",
        layer="D-ENHA-TEXT-DIMS",
        dim_type=0,
    )


def test_dimension_bbox_spans_all_three_points():
    vp = _vp(ps_center=(0.0, 0.0), ps_width=100.0, ps_height=100.0, view_center=(0.0, 0.0), view_height=100.0)
    dim = _dim(_pt(-10.0, 0.0), _pt(10.0, 0.0), _pt(0.0, 5.0))
    dxf_sheet = _dxf_sheet_stub([vp])
    bbox = dimension_bbox(dxf_sheet, dim, page_height_pt=1000.0)
    assert bbox is not None
    # scale is 1:1 here (ps_height == view_height); x spans -10..10 model
    # units -> -10..10 paper-space meters -> pt via the fixed 1000*72/25.4
    # m->pt scale (paper space is confirmed in meters, module docstring).
    pt_per_m = 1000 * 72 / 25.4
    assert bbox.x0 == pytest.approx(-10.0 * pt_per_m)
    assert bbox.x1 == pytest.approx(10.0 * pt_per_m)


def test_dimension_bbox_none_when_no_point_falls_in_any_viewport():
    dim = _dim(_pt(-10.0, 0.0), _pt(10.0, 0.0))
    dxf_sheet = _dxf_sheet_stub([])
    assert dimension_bbox(dxf_sheet, dim, page_height_pt=1000.0) is None


def test_dimension_bbox_partial_resolution_still_returns_a_box():
    # Only the dim-line point falls inside the (small) viewport window —
    # the two witness points sit outside it. A partial box is still a
    # real, if tighter, location, not nothing (module docstring).
    vp = _vp(ps_center=(0.0, 0.0), ps_width=1.0, ps_height=1.0, view_center=(0.0, 5.0), view_height=1.0)
    dim = _dim(_pt(-1000.0, 0.0), _pt(1000.0, 0.0), _pt(0.0, 5.0))
    dxf_sheet = _dxf_sheet_stub([vp])
    bbox = dimension_bbox(dxf_sheet, dim, page_height_pt=1000.0)
    assert bbox is not None
    assert bbox.x0 == bbox.x1  # only one point resolved -> a degenerate (point) box, not a wide/wrong one


# --- against the real sample -----------------------------------------------------


def test_real_dimensions_all_resolve_a_bbox_within_page_bounds():
    dxf = ingest_dxf(DXF_PATH)
    with fitz.open(PDF_PATH) as doc:
        page_rect = doc[PDF_PAGE_INDEX].rect

    assert len(dxf.dimensions) == 30  # same real count test_dxf_source.py confirms
    for dim in dxf.dimensions:
        bbox = dimension_bbox(dxf, dim, page_rect.height)
        assert bbox is not None, "every real dimension on this sheet sits inside its plan-view viewport"
        assert 0 <= bbox.x0 <= page_rect.width
        assert 0 <= bbox.x1 <= page_rect.width
        assert 0 <= bbox.y0 <= page_rect.height
        assert 0 <= bbox.y1 <= page_rect.height


def test_real_pile_labels_land_within_a_few_points_of_their_real_pdf_word():
    # The real end-to-end confirmation this module's docstring describes:
    # every PIL2343xx pile label's DXF model-space TEXT position, run
    # through model_to_pdf_point, should land right next to that same
    # label's real, independently-placed PDF word on the actual page —
    # not just "somewhere on the page."
    dxf = ingest_dxf(DXF_PATH)
    with fitz.open(PDF_PATH) as doc:
        page = doc[PDF_PAGE_INDEX]
        page_height_pt = page.rect.height
        words = page.get_text("words")

    checked = 0
    for t in dxf.texts:
        if not t.text.strip().startswith("PIL2343"):
            continue
        predicted = model_to_pdf_point(dxf, t.insert, page_height_pt)
        assert predicted is not None
        matches = [w for w in words if w[4].strip() == t.text.strip()]
        assert matches, f"expected a real PDF word matching {t.text!r}"
        nearest = min(
            matches, key=lambda w: (w[0] - predicted[0]) ** 2 + (w[1] - predicted[1]) ** 2
        )
        dist = ((nearest[0] - predicted[0]) ** 2 + (nearest[1] - predicted[1]) ** 2) ** 0.5
        assert dist < 5.0, f"{t.text!r}: predicted {predicted}, nearest real word at {nearest[:2]}, {dist:.2f}pt off"
        checked += 1
    assert checked >= 20  # real count on this sheet is 27 — a loose floor, not the exact figure
