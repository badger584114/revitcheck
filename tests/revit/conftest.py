"""Put the extension's `lib` folder on `sys.path`.

The package lives at `extensions/RevitCheck.extension/lib/revitcheck`
rather than under `src/` because that is where pyRevit picks it up
automatically — see `revitcheck/__init__.py` for why there is exactly
one copy of it rather than a source tree plus a synced deployment copy.

Done here rather than via pytest's `pythonpath` ini option so it works
regardless of the pytest version installed, and so it applies only to
this directory's tests.
"""

import os
import sys

import pytest

_LIB = os.path.abspath(
    os.path.join(
        os.path.dirname(__file__), "..", "..", "extensions", "RevitCheck.extension", "lib"
    )
)

if _LIB not in sys.path:
    sys.path.insert(0, _LIB)


from revitcheck.ir import (  # noqa: E402
    DimensionInfo,
    DimensionSegmentInfo,
    ReferenceInfo,
    RevitModel,
    SheetInfo,
    ViewInfo,
)


def model_ref(element_id=100, class_name="Wall"):
    """A reference to real model geometry."""
    return ReferenceInfo(
        element_id=element_id,
        class_name=class_name,
        category="Structural Framing",
        view_specific=False,
    )


def drafted_ref(element_id=200, class_name="DetailLine"):
    """A reference to view-specific linework — the drift case."""
    return ReferenceInfo(
        element_id=element_id,
        class_name=class_name,
        category="Lines",
        view_specific=True,
    )


def datum_ref(element_id=300, class_name="Grid"):
    return ReferenceInfo(
        element_id=element_id,
        class_name=class_name,
        category="Grids",
        view_specific=False,
    )


def unresolved_ref(element_id=400):
    return ReferenceInfo(element_id=element_id, resolved=False)


def dimension(
    element_id,
    view_id,
    references,
    value_mm=1000.0,
    override=None,
    spot=False,
    type_name=None,
    unique_id=None,
):
    return DimensionInfo(
        element_id=element_id,
        view_id=view_id,
        is_spot=spot,
        references=list(references),
        segments=[DimensionSegmentInfo(value_mm=value_mm, value_override=override)],
        type_name=type_name,
        unique_id=unique_id,
    )


def chain(element_id, view_id, references, segments, type_name=None):
    """A dimension chain: one Revit element carrying many segments.

    Revit models a chain as a single `Dimension` with `Segments`, not as
    several dimensions — the opposite of the DXF side, where chains had
    to be reassembled from shared witness points. `segments` here is a
    list of `(value_mm, override)` pairs.
    """
    return DimensionInfo(
        element_id=element_id,
        view_id=view_id,
        references=list(references),
        segments=[
            DimensionSegmentInfo(value_mm=value, value_override=override)
            for value, override in segments
        ],
        type_name=type_name,
    )


def view(element_id, name="SECTION A-A", view_type="Section", sheet_no="S101", **kw):
    return ViewInfo(
        element_id=element_id,
        name=name,
        view_type=view_type,
        sheet_no=sheet_no,
        sheet_id=1 if sheet_no else None,
        **kw
    )


def build_model(views=(), dimensions=(), sheets=None, errors=(), excluded_worksets=()):
    return RevitModel(
        doc_title="TEST-BRIDGE",
        sheets=list(sheets if sheets is not None else [SheetInfo(1, "S101", "Plan")]),
        views=list(views),
        dimensions=list(dimensions),
        extraction_errors=list(errors),
        excluded_worksets=list(excluded_worksets),
    )


@pytest.fixture
def make():
    """Bundle of synthetic-IR builders, so tests read as scenarios."""

    class _Make(object):
        model_ref = staticmethod(model_ref)
        drafted_ref = staticmethod(drafted_ref)
        datum_ref = staticmethod(datum_ref)
        unresolved_ref = staticmethod(unresolved_ref)
        dimension = staticmethod(dimension)
        chain = staticmethod(chain)
        view = staticmethod(view)
        model = staticmethod(build_model)

    return _Make()
