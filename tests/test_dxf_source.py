"""DXF ingestion (extraction/dxf_source.py) — PLANNING.md §5's geometry-
check input. Tests run against real converted DXF committed to
samples/dxf/ (two of the 31 real sheets in samples/dwg/, pre-converted so
the suite doesn't depend on ODA File Converter being installed — see
that module's docstring for the conversion command). One dimension-rich
sheet (101051, the "FOUNDATION LAYOUT" also used to calibrate the module)
and one zero-dimension sheet (101032, a construction-staging diagram) —
same "real sample first" approach as every other extraction module here.

`convert_dwg_to_dxf`'s success path additionally needs ODA File Converter
itself, which isn't a pip dependency — skipped unless it's installed at
its default path, so the suite still passes on a machine without it.
"""

import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from pdfchecker.extraction.dxf_source import (  # noqa: E402
    _DEFAULT_ODA_PATH,
    convert_dwg_to_dxf,
    ingest_dxf,
)

SAMPLES_DIR = Path(__file__).resolve().parent.parent / "samples"
DIMENSIONED_SHEET = str(SAMPLES_DIR / "dxf" / "T2DPAA-T2D-C3S-BR-DRG-101051_0.dxf")
ZERO_DIMENSION_SHEET = str(SAMPLES_DIR / "dxf" / "T2DPAA-T2D-C3S-BR-DRG-101032_0.dxf")


def test_units_resolved_as_meters():
    sheet = ingest_dxf(DIMENSIONED_SHEET)
    assert sheet.units == "m"  # $INSUNITS=6 on this sample, confirmed against real header


def test_dimensions_extracted_with_measurement_and_witness_points():
    sheet = ingest_dxf(DIMENSIONED_SHEET)
    assert len(sheet.dimensions) == 30  # confirmed count on this real sheet
    d = sheet.dimensions[0]
    assert d.measurement == pytest.approx(3.0248700210370316)
    assert d.dim_line_point.x == pytest.approx(578.4514197658034)
    assert d.ext_line1_origin != d.ext_line2_origin  # two distinct witness-line origins, not a placeholder


def test_dimension_text_overrides_exist_on_this_sheet():
    # Confirmed across the full real sample (all 31 sheets, not just this
    # committed one): 54% of dimensions carry an explicit override — the
    # actual "drawn vs. stated" comparison target for the geometry check
    # that will consume this. This one sheet's own rate is much lower
    # (1/30) — overrides cluster on sheets with more setout-critical
    # dimensions — so this test only asserts they're not entirely absent
    # here, not the aggregate rate (not derivable from 2 committed sheets).
    sheet = ingest_dxf(DIMENSIONED_SHEET)
    overridden = [d for d in sheet.dimensions if d.stated_text is not None]
    assert len(overridden) >= 1


def test_dimension_autocad_placeholder_text_not_treated_as_override():
    # AutoCAD's "<>" placeholder means "show the computed measurement" —
    # not a real override. None of this sheet's dimensions should surface
    # "<>" as stated_text.
    sheet = ingest_dxf(DIMENSIONED_SHEET)
    assert all(d.stated_text != "<>" for d in sheet.dimensions)


def test_zero_dimension_sheet_has_no_dimensions_but_has_viewports():
    # A construction-staging diagram — nothing to dimension, but it's
    # still a real sheet with paper-space viewports.
    sheet = ingest_dxf(ZERO_DIMENSION_SHEET)
    assert sheet.dimensions == []
    assert len(sheet.viewports) == 8  # confirmed count on this real sheet


def test_viewports_carry_scale_inputs():
    sheet = ingest_dxf(DIMENSIONED_SHEET)
    assert len(sheet.viewports) == 4  # confirmed count on this real sheet
    for vp in sheet.viewports:
        assert vp.ps_height > 0
        assert vp.view_height > 0  # both needed to compute the paper-space/model-space scale factor


def test_inserts_extracted_with_name_and_position():
    # PLANNING.md §5b (extraction/setout_reconstruction.py) — confirmed
    # real counts on this sheet, 2026-08-11.
    sheet = ingest_dxf(DIMENSIONED_SHEET)
    assert len(sheet.inserts) == 42
    setout_points = [i for i in sheet.inserts if "SETOUT POINT" in i.name.upper()]
    assert len(setout_points) == 2  # one per abutment


def test_texts_extracted_as_plain_text():
    # `.plain_text()`, not raw `.text` — confirmed the real control-point
    # label's raw text carries a literal "\P" the raw attribute leaves
    # unresolved (see extraction/dxf_source.py's docstring).
    sheet = ingest_dxf(DIMENSIONED_SHEET)
    assert len(sheet.texts) == 67
    control_points = [t for t in sheet.texts if t.text.startswith("E ") and "\n" in t.text]
    assert len(control_points) == 2
    assert control_points[0].text == "E 278437.803\nN 6130709.230"


def test_convert_missing_oda_raises_clear_error():
    with pytest.raises(FileNotFoundError, match="ODA File Converter not found"):
        convert_dwg_to_dxf("/tmp", "/tmp/dxf_out_nonexistent", oda_path="/nonexistent/ODAFileConverter")


@pytest.mark.skipif(not Path(_DEFAULT_ODA_PATH).exists(), reason="ODA File Converter not installed on this machine")
def test_convert_dwg_to_dxf_real_conversion(tmp_path):
    in_dir = tmp_path / "in"
    out_dir = tmp_path / "out"
    in_dir.mkdir()
    dwg = SAMPLES_DIR / "dwg" / "T2DPAA-T2D-C3S-BR-DRG-101021_0.dwg"
    if not dwg.exists():
        pytest.skip("samples/dwg/ not present")
    (in_dir / dwg.name).write_bytes(dwg.read_bytes())

    convert_dwg_to_dxf(str(in_dir), str(out_dir))

    converted = list(out_dir.glob("*.dxf"))
    assert len(converted) == 1
    sheet = ingest_dxf(str(converted[0]))
    assert sheet.units == "m"
