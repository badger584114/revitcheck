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

from pdfchecker.ir import DxfSheet, Project, Sheet, TitleBlock  # noqa: E402
from pdfchecker.extraction.dxf_source import (  # noqa: E402
    _DEFAULT_ODA_PATH,
    attach_dxf_sheets,
    convert_dwg_to_dxf,
    filename_sheet_digits,
    ingest_dxf,
)

SAMPLES_DIR = Path(__file__).resolve().parent.parent / "samples" / "BR06"
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


class TestSheetJoin:
    """`attach_dxf_sheets` / `filename_sheet_digits` — matching a DXF file
    to the PDF sheet it belongs to.

    Reworked 2026-08-17 against a second client (Flinders / CS1). Two real
    filename conventions now, and they need different matching strengths:

      T2DPAA  "...-DRG-101051_0.dxf"  belongs to sheet "2871051"  — the two
              agree only on the last four digits
      CS1     "359944.dxf"            belongs to sheet "359944"   — exact
    """

    def _project(self, *sheet_nos):
        return Project(
            source_path="synthetic",
            sheets=[
                Sheet(
                    page_index=i,
                    page_width=800,
                    page_height=600,
                    title_block=TitleBlock(fields={"sheet_no": no}),
                    revision_schedule=[],
                    tables=[],
                    words=[],
                    paths=[],
                    raw_text="",
                )
                for i, no in enumerate(sheet_nos)
            ],
        )

    def _dxf(self, name):
        return DxfSheet(source_path=f"/tmp/{name}", dimensions=[], viewports=[], units="m")

    def test_filename_patterns(self):
        assert filename_sheet_digits("T2DPAA-T2D-C3S-BR-DRG-101032_0.dxf") == "101032"
        assert filename_sheet_digits("/a/b/359944.dxf") == "359944"
        assert filename_sheet_digits("no-digits-here.dxf") is None

    def test_exact_match(self):
        """The CS1 case — filename digits are the sheet's own number."""

        project = self._project("359944", "359945")
        assert attach_dxf_sheets(project, [self._dxf("359944.dxf")]) == 1
        assert project.sheets[0].dxf_sheet is not None
        assert project.sheets[1].dxf_sheet is None

    def test_last_four_match(self):
        """The T2DPAA case — the identifiers differ everywhere else."""

        project = self._project("2871051")
        assert attach_dxf_sheets(project, [self._dxf("X-DRG-101051_0.dxf")]) == 1
        assert project.sheets[0].dxf_sheet is not None

    def test_exact_wins_over_last_four(self):
        project = self._project("101051", "2871051")
        attach_dxf_sheets(project, [self._dxf("X-DRG-101051_0.dxf")])
        assert project.sheets[0].dxf_sheet is not None, "should prefer the exact identifier"
        assert project.sheets[1].dxf_sheet is None

    def test_ambiguous_last_four_attaches_nothing(self):
        """The bug this replaces: an earlier version keyed a plain dict on
        the last four digits, so two sheets sharing them silently
        overwrote each other and the DXF landed on whichever was indexed
        last. Checking one sheet's geometry against another sheet's
        drawing is a far worse outcome than reporting it unmatched — and
        with 116 sheets in a real set this is a birthday-problem risk, not
        a theoretical one."""

        project = self._project("2871051", "9991051")
        assert attach_dxf_sheets(project, [self._dxf("X-DRG-101051_0.dxf")]) == 0
        assert all(s.dxf_sheet is None for s in project.sheets)

    def test_unmatched_filename_is_counted_not_attached(self):
        project = self._project("2871051")
        assert attach_dxf_sheets(project, [self._dxf("X-DRG-999999_0.dxf")]) == 0
        assert project.sheets[0].dxf_sheet is None

    def test_sheets_without_a_sheet_no_are_skipped(self):
        project = self._project("2871051")
        project.sheets[0].title_block = TitleBlock(fields={})
        assert attach_dxf_sheets(project, [self._dxf("X-DRG-101051_0.dxf")]) == 0
