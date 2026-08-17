"""IFC ingestion (extraction/ifc_source.py) — PLANNING.md §5's proposed
third geometry-check source. Tests run against the two real `.ifc` files
in samples/ — `samples/BR06/T2DPAA-T2D-C3S-BR-M3D-100302.ifc` and
`samples/BR08/T2DPAA-T2D-C3S-BR-M3D-100304.ifc`, same client project
(confirmed by the user and independently by their identical `IfcSite`
placement/RefLatitude — see the shared-project assertion below), "real
sample first" like every other extraction module here.

**Much faster since 2026-08-17.** This file used to be the slowest in
the suite: `ifcopenshell.geom.create_shape` is native geometry-kernel
work and BR06's 132 elements alone took ~205s, 99% of it on two
`IfcPolygonalFaceSet` deck pours. `extract_elements` now reads
tessellated elements' own coordinate lists instead of meshing them
(`_faceset_bbox`), which brought BR06's ingest to ~1s with bit-identical
bboxes — `TestFaceSetFastPath` below is the regression guard for that
equivalence. Elements that aren't purely tessellated still mesh.

One test here is still deliberately slow: `test_fast_path_matches_
meshing_exactly` re-derives every fast-path element the *slow* way to
prove the two agree, which means paying the ~205s meshing cost the
optimization exists to avoid. That's the point of it — the cost buys the
evidence that ingestion can skip it — so it stays, and it's why this
file still runs ~4 minutes.
"""

import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from pdfchecker.extraction.ifc_source import (  # noqa: E402
    _bbox,
    _dms_to_decimal,
    _EXCLUDED_CLASSES,
    _faceset_bbox,
    _geom_settings,
    ingest_ifc,
)

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


def test_dms_to_decimal_sign_when_degrees_is_zero():
    # Bug fixed 2026-08-12: sign was read from the degrees component
    # alone (`value[0] < 0`), which is wrong whenever degrees is exactly
    # 0 but minutes/seconds carry the sign instead — `0 < 0` is False, so
    # this silently came back positive. A real, schema-legal encoding for
    # a site within the first degree of the equator/prime meridian.
    assert _dms_to_decimal((0, -30, 0)) == pytest.approx(-0.5, abs=1e-9)
    assert _dms_to_decimal((0, 0, -30)) == pytest.approx(-30 / 3600, abs=1e-9)


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


class TestFaceSetFastPath:
    """`_faceset_bbox` — the 2026-08-17 optimization that reads a
    tessellated element's own `Coordinates.CoordList` instead of asking
    `ifcopenshell.geom` to build a full BRep just to bound it.

    The whole value of this path is that it changes *nothing* except
    speed, so that's what these assert: same elements, same boxes."""

    def test_fast_path_matches_meshing_exactly(self, br06_model):
        """The equivalence that justifies the optimization. Every
        fast-path element is re-derived the slow way and compared.

        Confirmed 2026-08-17 across all 132 of BR06's elements (both
        paths, not just these): max difference 0.000001 mm, i.e. float
        noise. 1e-6 m here is still far tighter than any tolerance a
        geometry rule applies (the strictest is 10mm)."""

        import ifcopenshell
        import ifcopenshell.geom as ifc_geom
        import ifcopenshell.util.unit as ifc_unit

        f = ifcopenshell.open(BR06_IFC)
        unit_scale = ifc_unit.calculate_unit_scale(f)

        compared = 0
        for e in f.by_type("IfcElement"):
            if e.is_a() in _EXCLUDED_CLASSES or e.Representation is None:
                continue
            fast = _faceset_bbox(e, unit_scale)
            if fast is None:
                continue
            # `shape` MUST stay bound while its vertex buffer is read.
            # Chaining `create_shape(...).geometry.verts` segfaults: the
            # shape is a temporary, `.geometry.verts` is a view into its
            # native memory, and the temporary is freed before the view
            # is consumed. `extract_elements` binds it for the same
            # reason — this is not incidental style.
            shape = ifc_geom.create_shape(_geom_settings, e)
            verts = list(shape.geometry.verts)
            if not verts:
                continue
            slow_min, slow_max = _bbox(verts)
            for axis in ("x", "y", "z"):
                assert getattr(fast[0], axis) == pytest.approx(getattr(slow_min, axis), abs=1e-6)
                assert getattr(fast[1], axis) == pytest.approx(getattr(slow_max, axis), abs=1e-6)
            compared += 1

        assert compared > 0, "no element exercised the fast path — the optimization isn't being hit"

    def test_non_tessellated_elements_fall_back(self, br06_model):
        """BR06's real geometry is a mix — `IfcExtrudedAreaSolid` (with
        arbitrary arc profiles, which genuinely need the kernel) as well
        as face sets. A swept-solid element must return `None` so the
        caller meshes it, rather than silently yielding a partial box."""

        import ifcopenshell
        import ifcopenshell.util.unit as ifc_unit

        f = ifcopenshell.open(BR06_IFC)
        unit_scale = ifc_unit.calculate_unit_scale(f)
        swept = [
            e
            for e in f.by_type("IfcElement")
            if e.Representation
            and any(
                r.RepresentationType == "SweptSolid"
                for r in e.Representation.Representations
            )
        ]
        assert swept, "expected real swept-solid elements on this sample"
        assert all(_faceset_bbox(e, unit_scale) is None for e in swept)

    def test_bboxes_are_metres_not_file_units(self, br06_model):
        """The fast path reads raw `CoordList` values, which are in the
        file's *declared* unit (millimetres on both samples), while
        `create_shape` always hands back metres. Forgetting that scale
        would put fast-path elements 1000x off — and only *some*
        elements, which is the kind of bug that hides. The deck pours
        are the elements this optimization actually targets."""

        parts = [e for e in br06_model.elements if e.ifc_class == "IfcBuildingElementPart"]
        assert parts
        for e in parts:
            assert 270_000 < e.bbox_min.x < 290_000
            assert 6_120_000 < e.bbox_min.y < 6_140_000

    def test_pile_count_unchanged_by_the_optimization(self, br06_model):
        """The end-to-end guard: 28 real piles, the figure every
        IFC-based geometry result in this project is anchored to (see
        checks/geometry.py's `_is_slender_vertical` docstring). If the
        fast path ever shifted a bbox, this is where it would show."""

        from pdfchecker.checks.catalog import RuleConfig
        from pdfchecker.checks.geometry import _is_slender_vertical

        config = RuleConfig()
        shape_found = {e.global_id for e in br06_model.elements if _is_slender_vertical(e, config)}
        name_found = {
            e.global_id
            for e in br06_model.elements
            if e.display_name and "PILE" in e.display_name.upper()
        }
        assert len(shape_found) == 28
        assert shape_found <= name_found
