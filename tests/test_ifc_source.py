"""IFC ingestion (extraction/ifc_source.py) — PLANNING.md §5's proposed
third geometry-check source. Tests run against the two real `.ifc` files
in samples/ — `samples/BR06/T2DPAA-T2D-C3S-BR-M3D-100302.ifc` and
`samples/BR08/T2DPAA-T2D-C3S-BR-M3D-100304.ifc`, same client project
(confirmed by the user and independently by their identical `IfcSite`
placement/RefLatitude — see the shared-project assertion below), "real
sample first" like every other extraction module here.

**Genuinely slow, worth knowing before running this file inline**:
`ifcopenshell.geom.create_shape` is native geometry-kernel work, one
call per element — observed 130-210s for BR06's 132 elements alone on
this machine (old x86_64-under-Rosetta Python, CLAUDE.md's environment-
quirk note), and BR08 has 934 elements to BR06's 132. Session-scoped
fixtures below pay each file's ingestion cost once; still expect this
file alone to take several minutes for BR08's fixture the first time
it's used — run in the background rather than waiting on it inline,
same guidance CLAUDE.md already gives for the full-suite spelling pass.
"""

import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from pdfchecker.extraction.ifc_source import _dms_to_decimal, ingest_ifc  # noqa: E402

SAMPLES_DIR = Path(__file__).resolve().parent.parent / "samples"
BR06_IFC = str(SAMPLES_DIR / "BR06" / "T2DPAA-T2D-C3S-BR-M3D-100302.ifc")
BR08_IFC = str(SAMPLES_DIR / "BR08" / "T2DPAA-T2D-C3S-BR-M3D-100304.ifc")


@pytest.fixture(scope="session")
def br06_model():
    return ingest_ifc(BR06_IFC)


@pytest.fixture(scope="session")
def br08_model():
    return ingest_ifc(BR08_IFC)


def test_dms_to_decimal_converts_compound_angle():
    # IfcSite.RefLatitude on both real files: (-34, -57, -26, -34426).
    assert _dms_to_decimal((-34, -57, -26, -34426)) == pytest.approx(-34.957231785, abs=1e-6)


def test_dms_to_decimal_handles_missing_value():
    # A real, legitimate case (an IfcSite with no RefLatitude/RefLongitude
    # set at all) — must not raise or fabricate 0.0.
    assert _dms_to_decimal(None) is None
    assert _dms_to_decimal(()) is None


def test_br06_schema_and_units(br06_model):
    assert br06_model.schema == "IFC4"
    # The file declares millimetres (confirmed via IfcProject.UnitsInContext,
    # not just any IfcSIUnit in the file — see this module's docstring for
    # the derived-unit-component bug this guards against).
    assert br06_model.length_unit == "MILLI.METRE"


def test_neither_real_file_has_schema_georeferencing(br06_model, br08_model):
    # Confirmed real finding: neither file carries IFC4's own
    # IfcMapConversion/IfcProjectedCRS georeferencing entities — the
    # reason this module doesn't trust raw world coordinates as real
    # Easting/Northing without a caller separately verifying it.
    assert br06_model.has_map_conversion is False
    assert br08_model.has_map_conversion is False


def test_both_files_share_the_same_site_reference(br06_model, br08_model):
    # Confirmed by the user and independently here: BR06 and BR08 are the
    # same client project, so their IfcSite reference point matches
    # exactly — this is real, but it's evidence the two files share one
    # project's convention, not that any IFC file's raw placement can be
    # trusted as real-world coordinates in general.
    assert br06_model.site_ref_lat == pytest.approx(br08_model.site_ref_lat)
    assert br06_model.site_ref_long == pytest.approx(br08_model.site_ref_long)
    assert br06_model.site_ref_lat == pytest.approx(-34.957231785, abs=1e-6)
    assert br06_model.site_ref_long == pytest.approx(138.568684593, abs=1e-6)


def test_br06_element_count_and_class_mix(br06_model):
    # Confirmed real counts, 2026-08-12.
    assert len(br06_model.elements) == 132
    from collections import Counter

    by_class = Counter(e.ifc_class for e in br06_model.elements)
    assert by_class == {
        "IfcBeam": 68,
        "IfcBuildingElementProxy": 16,
        "IfcRailing": 2,
        "IfcSlab": 40,
        "IfcBuildingElementPart": 6,
    }


def test_br08_element_count_and_class_mix(br08_model):
    # Confirmed real counts, 2026-08-12 — deliberately a different class
    # mix from BR06 (adds IfcMember/IfcRoof/IfcWall, no IfcRailing) even
    # though it's the same client project: real evidence extraction must
    # not hardcode an expected class whitelist (see this module's
    # docstring).
    assert len(br08_model.elements) == 934
    from collections import Counter

    by_class = Counter(e.ifc_class for e in br08_model.elements)
    assert by_class == {
        "IfcBeam": 176,
        "IfcBuildingElementProxy": 647,
        "IfcMember": 24,
        "IfcRoof": 1,
        "IfcSlab": 82,
        "IfcWall": 2,
        "IfcBuildingElementPart": 2,
    }


def test_opening_elements_excluded(br06_model, br08_model):
    # IfcOpeningElement (voids/penetrations) are schema-excluded, not
    # built geometry — confirmed real: 20 on BR06, 145 on BR08, neither
    # should surface as an IfcElement here.
    assert all(e.ifc_class != "IfcOpeningElement" for e in br06_model.elements)
    assert all(e.ifc_class != "IfcOpeningElement" for e in br08_model.elements)


def test_global_ids_are_unique(br06_model):
    ids = [e.global_id for e in br06_model.elements]
    assert len(ids) == len(set(ids))


def test_bbox_is_real_world_scale_not_raw_file_units(br06_model):
    # Confirmed real finding: ifcopenshell.geom's default normalizes
    # geometry to real metres regardless of the file's declared
    # millimetre unit — bbox values should be ~278,xxx / ~6,130,xxx
    # (real MGA-zone-scale survey coordinates), not ~278,xxx,000.
    beam = next(e for e in br06_model.elements if e.ifc_class == "IfcBeam")
    assert 270_000 < beam.bbox_min.x < 290_000
    assert 6_120_000 < beam.bbox_min.y < 6_140_000
    assert beam.bbox_max.x > beam.bbox_min.x
    assert beam.bbox_max.y > beam.bbox_min.y


def test_display_name_carries_raw_revit_string_not_parsed(br06_model):
    # display_name is deliberately the raw "Family and Type: Tag" Revit
    # string, kept only for human-readable labeling — not parsed into
    # structured fields, since that shape is confirmed firm-specific
    # (see this module's docstring).
    beam = next(e for e in br06_model.elements if e.ifc_class == "IfcBeam")
    assert beam.display_name is not None
    assert ":" in beam.display_name
