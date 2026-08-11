"""extraction/setout_reconstruction.py — PLANNING.md §5b's first built
slice: bearing + dimension-chain pile setout reconstruction. Same split
as test_geometry.py: synthetic cases for each mechanism in isolation,
plus a real-sample assertion (sheet 2871051 / samples/dxf/101051) that
exercises the whole pipeline end to end — see that module's docstring for
the real convention this is calibrated against.
"""

import math
import sys
from pathlib import Path

import pdfplumber

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from pdfchecker.checks.catalog import RuleConfig  # noqa: E402
from pdfchecker.extraction.dxf_source import ingest_dxf  # noqa: E402
from pdfchecker.extraction.tables import extract_tables  # noqa: E402
from pdfchecker.extraction.setout_reconstruction import (  # noqa: E402
    _parse_bearing_dms,
    find_bearing_for_chain,
    find_control_points,
    find_dimension_chains,
    find_location_for_chain,
    match_chain_points_to_piles,
    parse_pile_locations,
    parse_pile_schedule,
    reconstruct_sheet,
    walk_chain,
)
from pdfchecker.ir import (  # noqa: E402
    BBox,
    DimensionEntity,
    DxfInsert,
    DxfSheet,
    DxfText,
    Point3D,
    Sheet,
    Table,
    TitleBlock,
)

SAMPLES_DIR = Path(__file__).resolve().parent.parent / "samples"
SAMPLE_DXF = str(SAMPLES_DIR / "dxf" / "T2DPAA-T2D-C3S-BR-DRG-101051_0.dxf")
SAMPLE_PDF = str(SAMPLES_DIR / "T2DPAA-T2D-C3S-BR-DRG-101000.pdf")


def _dim(p1: Point3D, p2: Point3D, measurement: float, stated_text=None) -> DimensionEntity:
    return DimensionEntity(
        measurement=measurement,
        stated_text=stated_text,
        dim_line_point=p1,
        ext_line1_origin=p1,
        ext_line2_origin=p2,
        dimstyle="Dimension_Standard_O__mm_",
        layer="D-ENHA-TEXT-DIMS",
        dim_type=0,
    )


def _pt(x: float, y: float) -> Point3D:
    return Point3D(x=x, y=y, z=0.0)


# --- find_dimension_chains --------------------------------------------------


def test_dimensions_sharing_a_witness_point_form_one_chain():
    dims = [
        _dim(_pt(0, 0), _pt(0, 10), 10.0),
        _dim(_pt(0, 10), _pt(0, 20), 10.0),  # shares (0,10) with the first
    ]
    chains = find_dimension_chains(dims, link_tolerance_m=0.01)
    assert len(chains) == 1
    assert len(chains[0]) == 2


def test_unrelated_dimensions_stay_separate_chains():
    dims = [
        _dim(_pt(0, 0), _pt(0, 10), 10.0),
        _dim(_pt(500, 500), _pt(500, 510), 10.0),  # nowhere near the first
    ]
    chains = find_dimension_chains(dims, link_tolerance_m=0.01)
    assert len(chains) == 2


def test_link_tolerance_respected():
    # 0.02m apart — outside a 0.01m tolerance, so these should NOT link.
    dims = [
        _dim(_pt(0, 0), _pt(0, 10), 10.0),
        _dim(_pt(0, 10.02), _pt(0, 20), 10.0),
    ]
    assert len(find_dimension_chains(dims, link_tolerance_m=0.01)) == 2
    assert len(find_dimension_chains(dims, link_tolerance_m=0.05)) == 1


# --- bearing DMS parsing ----------------------------------------------------


def test_parse_bearing_dms():
    assert _parse_bearing_dms('175° 08\' 40"') == 175 + 8 / 60 + 40 / 3600


def test_parse_bearing_dms_rejects_placeholder():
    # The real placeholder text found on the sample.
    assert _parse_bearing_dms('BEARING x° xx\' xx"') is None


def test_find_bearing_for_chain_picks_nearest():
    chain_points = [_pt(0, 0), _pt(0, 10)]
    texts = [
        DxfText(text='175° 08\' 40"', insert=_pt(0, 5), layer="X"),  # near the chain
        DxfText(text='90° 00\' 00"', insert=_pt(500, 500), layer="X"),  # far away
    ]
    assert find_bearing_for_chain(chain_points, texts, max_pair_distance_m=30.0) == _parse_bearing_dms(
        '175° 08\' 40"'
    )


def test_find_bearing_for_chain_none_beyond_max_distance():
    chain_points = [_pt(0, 0)]
    texts = [DxfText(text='175° 08\' 40"', insert=_pt(1000, 1000), layer="X")]
    assert find_bearing_for_chain(chain_points, texts, max_pair_distance_m=5.0) is None


# --- walk_chain --------------------------------------------------------------


def test_walk_chain_bidirectional_from_mid_chain_origin():
    # Mirrors the real sample's shape: origin anchors partway along the
    # chain (not at an end), bearing runs "up" the local y-axis, so nodes
    # on either side of the origin must get opposite-signed distances.
    nodes = [_pt(0, 0), _pt(0, 10), _pt(0, 20), _pt(0, 30)]
    dims = [
        _dim(nodes[0], nodes[1], 10.0),
        _dim(nodes[1], nodes[2], 10.0),
        _dim(nodes[2], nodes[3], 10.0),
    ]
    origin_local = _pt(0, 10.0)  # exactly node[1]
    from pdfchecker.ir import SetoutPoint

    origin_real = SetoutPoint(point_id="", easting=1000.0, northing=2000.0)
    walk = walk_chain(dims, origin_local, origin_real, bearing_deg=0.0, units="m")
    assert walk is not None
    by_pos = {(r.local_point.x, r.local_point.y): r for r in walk}
    # bearing 0deg -> (dE, dN) = (0, 1): purely northward
    assert by_pos[(0, 10)].derived.northing == 2000.0  # the anchor itself
    assert by_pos[(0, 0)].derived.northing == 1990.0  # 10m "behind" the origin
    assert by_pos[(0, 20)].derived.northing == 2010.0  # 10m "ahead"
    assert by_pos[(0, 30)].derived.northing == 2020.0  # two hops ahead


def test_walk_chain_returns_none_when_origin_too_far():
    nodes = [_pt(0, 0), _pt(0, 10)]
    dims = [_dim(nodes[0], nodes[1], 10.0)]
    from pdfchecker.ir import SetoutPoint

    origin_real = SetoutPoint(point_id="", easting=0.0, northing=0.0)
    walk = walk_chain(dims, _pt(1000, 1000), origin_real, bearing_deg=0.0, units="m", origin_pair_max_distance_m=5.0)
    assert walk is None


def test_walk_chain_prefers_stated_text_over_measurement():
    # The "check the manual entry, not the model" principle — module
    # docstring / _effective_value_m.
    nodes = [_pt(0, 0), _pt(0, 10)]
    dims = [_dim(nodes[0], nodes[1], measurement=10.0, stated_text="12.5")]
    from pdfchecker.ir import SetoutPoint

    origin_real = SetoutPoint(point_id="", easting=0.0, northing=0.0)
    walk = walk_chain(dims, nodes[0], origin_real, bearing_deg=0.0, units="m")
    by_pos = {(r.local_point.x, r.local_point.y): r for r in walk}
    assert by_pos[(0, 10)].derived.northing == 12.5  # not 10.0


# --- match_chain_points_to_piles: one-to-one assignment ----------------------


def test_match_is_one_to_one_not_first_come_first_served():
    # Two labels both within range of the SAME node — the closer one
    # should win it, and the loser must NOT silently grab a farther node
    # if the schedule only has this one node available; it should end up
    # unmatched instead of duplicating the winner's position (the real
    # bug found against the sample — see module docstring).
    node = _pt(0, 0)
    close_label = DxfText(text="PIL_A", insert=_pt(0.5, 0), layer="X")
    far_label = DxfText(text="PIL_B", insert=_pt(2.0, 0), layer="X")
    matched = match_chain_points_to_piles(
        [node], [close_label, far_label], {"PIL_A", "PIL_B"}, max_pair_distance_m=5.0
    )
    assert matched == {"PIL_A": node}
    assert "PIL_B" not in matched


def test_match_beyond_max_distance_excluded():
    node = _pt(0, 0)
    label = DxfText(text="PIL_A", insert=_pt(10, 0), layer="X")
    matched = match_chain_points_to_piles([node], [label], {"PIL_A"}, max_pair_distance_m=5.0)
    assert matched == {}


# --- find_control_points -----------------------------------------------------


def test_find_control_points_pairs_insert_to_nearest_text():
    dxf_sheet = DxfSheet(
        source_path="x.dxf",
        dimensions=[],
        viewports=[],
        units="m",
        inserts=[DxfInsert(name="CS_SYMB_SETOUT POINT - 5mm", insert=_pt(0, 0), layer="X")],
        texts=[DxfText(text="E 100.000\nN 200.000", insert=_pt(0.1, 0.1), layer="X")],
    )
    origins = find_control_points(dxf_sheet, "SETOUT POINT", max_pair_distance_m=5.0)
    assert len(origins) == 1
    local, real = origins[0]
    assert real.easting == 100.0 and real.northing == 200.0


# --- parse_pile_schedule: real "title glued into header row" shape ----------


def test_parse_pile_schedule_finds_header_past_a_title_row():
    # Confirmed real shape on the sample: some schedules have their title
    # ("ABUTMENT A PILE SCHEDULE") as row 0 of the SAME table as the real
    # header row — parse_pile_schedule must scan past it, not assume
    # row 0 is the header (see its docstring).
    table = Table(
        bbox=BBox(0, 0, 10, 10),
        rows=[
            ["ABUTMENT A PILE SCHEDULE", None, None],
            ["SITE ID", "EASTING (m)", "NORTHING (m)"],
            ["PIL001", "100.000", "200.000"],
        ],
    )
    points = parse_pile_schedule(table)
    assert len(points) == 1
    assert points[0].point_id == "PIL001"
    assert points[0].easting == 100.0
    assert points[0].northing == 200.0


def test_parse_pile_schedule_returns_empty_for_non_schedule_table():
    table = Table(bbox=BBox(0, 0, 10, 10), rows=[["REV", "DESCRIPTION"], ["0", "ISSUED"]])
    assert parse_pile_schedule(table) == []


def test_parse_pile_locations():
    table = Table(
        bbox=BBox(0, 0, 10, 10),
        rows=[
            ["SITE ID", "LOCATION", "EASTING (m)"],
            ["PIL001", "ABUTMENT A", "100.000"],
        ],
    )
    assert parse_pile_locations(table) == {"PIL001": "ABUTMENT A"}


# --- find_location_for_chain --------------------------------------------------


def test_find_location_for_chain_picks_nearest_known_label():
    chain_points = [_pt(0, 0)]
    texts = [
        DxfText(text="ABUTMENT A", insert=_pt(1, 1), layer="X"),
        DxfText(text="ABUTMENT B", insert=_pt(100, 100), layer="X"),
    ]
    location = find_location_for_chain(chain_points, texts, {"ABUTMENT A", "ABUTMENT B"}, 10.0)
    assert location == "ABUTMENT A"


# --- against the real sample --------------------------------------------------


def test_real_sample_reconstructs_both_abutment_pile_rows():
    # Sheet 2871051's "FOUNDATION LAYOUT" — 24 real piles across two
    # abutments, each independently reconstructed from the sheet's own
    # setout point + bearing + dimension chain and compared to the pile
    # schedule. Confirmed 2026-08-11: every real pile reconstructs to
    # within ~7mm of its schedule row (well inside the 10mm default
    # tolerance) — this is a real, calibrated figure from the sample, not
    # a loose bound picked to make the test pass. The sheet's four "OFF
    # STRUCTURE BARRIER" piles have no dimension chain of their own on
    # this DXF and must NOT be reconstructed from a neighboring abutment's
    # chain (the real ambiguity find_location_for_chain exists to resolve
    # — see setout_reconstruction.py's docstring).
    #
    # Builds its own Sheet directly from page 15 (0-indexed 14) of the PDF
    # sample rather than using the shared session-scoped `project` fixture
    # — that fixture is reused read-only across the whole suite, and this
    # test needs to attach a DxfSheet to it, which would mutate shared
    # state for every other test module that runs afterward.
    with pdfplumber.open(SAMPLE_PDF) as pdf:
        tables = extract_tables(pdf.pages[14])
    sheet = Sheet(
        page_index=14,
        page_width=100.0,
        page_height=100.0,
        title_block=TitleBlock(fields={"sheet_no": "2871051"}),
        revision_schedule=[],
        tables=tables,
        words=[],
        paths=[],
        raw_text="",
        dxf_sheet=ingest_dxf(SAMPLE_DXF),
    )

    results = reconstruct_sheet(sheet, RuleConfig())
    by_id = {r.point_id: r for r in results}

    abutment_a = [f"PIL23430{n}" if n < 10 else f"PIL2343{n}" for n in range(1, 13)]
    abutment_b = [f"PIL2343{n}" for n in range(21, 33)]
    for point_id in abutment_a + abutment_b:
        r = by_id[point_id]
        assert r.status == "reconstructed", f"{point_id}: {r.status}"
        delta_mm = math.hypot(
            r.derived.easting - r.stated.easting, r.derived.northing - r.stated.northing
        ) * 1000
        assert delta_mm < 10.0, f"{point_id}: {delta_mm:.1f}mm"

    for point_id in ("PIL234341", "PIL234342", "PIL234343", "PIL234344"):
        assert by_id[point_id].status != "reconstructed"
