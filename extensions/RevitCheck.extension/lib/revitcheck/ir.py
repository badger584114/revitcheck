"""The Revit-side Intermediate Representation.

Same discipline as the parked `pdfchecker.ir` (ARCHIVE-pdf-dwg.md; the
code is at `git checkout pdf-dwg-final`) and for the same reason —
CLAUDE.md's standing rule that any new extractor must normalize into the
IR rather than build format-specific downstream logic into the check
engines. Revit is simply a third extractor. What changes is the *location model*: a PDF Issue
points at a page and a bounding box because the fix happened somewhere
else, in Revit. Here the check runs where the fix happens, so a location
is an element id plus the view it lives in — which pyRevit can turn into
a clickable link that selects and zooms the offending element. That is
why this is an adapted schema rather than a shared one.

Two rules govern what belongs in here, both of which exist to keep
development possible on a machine with no Revit installed:

1. **Nothing in this module may import the Revit API.** Only
   `adapters/revit_source.py` may, and nothing imports that except the
   pyRevit scripts themselves.

2. **These are raw facts, not judgements.** A `ReferenceInfo` records
   what Revit says about the referenced element (its class, its
   category, whether it is view-specific); it does *not* record whether
   that counts as "drafted". The classification lives in
   `checks/dimensions.py`. This split is deliberate and load-bearing for
   the workflow: a capture taken at work stays valid when the
   classification rule is tuned on the Mac, so tuning never costs a trip
   back to a Revit machine.

Units: **every length in this IR is millimetres.** Revit's internal
length unit is always decimal feet regardless of project settings, so
the adapter multiplies by 304.8 on the way in. No API call is needed for
that and no version branching either — see `adapters/revit_source.py`.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Dict, List, Optional

# Revit's internal length unit is decimal feet, always — independent of
# the project's display units. Exact by definition, so this is a
# conversion rather than an approximation.
MM_PER_FOOT = 304.8


class Provenance(object):
    """Where a dimension takes its measurement from.

    Plain string constants rather than an `enum.Enum` so a value
    survives a JSON round-trip through `capture.py` as itself, with no
    encode/decode step and nothing to get wrong if these ever have to
    run under pyRevit's IronPython engine instead of CPython.
    """

    MODEL = "model"        # real model geometry — updates when the model does
    DATUM = "datum"        # grid, level or reference plane — also live
    DRAFTED = "drafted"    # view-specific linework — will silently go stale
    MIXED = "mixed"        # some of each; suspicious in its own right
    UNKNOWN = "unknown"    # reference could not be resolved

    ALL = (MODEL, DATUM, DRAFTED, MIXED, UNKNOWN)


@dataclass
class Point3D:
    """A point in project coordinates, millimetres."""

    x: float
    y: float
    z: float


@dataclass
class ReferenceInfo:
    """One endpoint of a dimension — what it is attached to.

    Raw facts only, per this module's rule 2. `view_specific` is the
    load-bearing one: Revit sets it on every element that belongs to a
    single view (detail lines, detail components, filled and masking
    regions), which is exactly the population that cannot track the
    model. It is a property of the API rather than of a client's
    drafting standard, so it is the kind of invariant the Flinders
    exercise showed actually survives contact with a second client —
    unlike the DXF-side layer-name proxy (`D-BDGE` vs `A-DETL`) it
    replaces.
    """

    element_id: int
    resolved: bool = True
    class_name: Optional[str] = None
    category: Optional[str] = None
    # Negative int for a Revit built-in category. Language-independent
    # and stable across versions, unlike the display name in `category`,
    # which is localized — record both, classify on this one.
    builtin_category: Optional[int] = None
    view_specific: Optional[bool] = None
    # A dimension may reference geometry inside a linked model. The
    # adapter follows `Reference.LinkedElementId` to describe the real
    # element rather than the link instance wrapping it; this flag says
    # it did. Common on bridge projects, where the structure is often
    # linked into a coordination file.
    linked: bool = False
    link_instance_id: Optional[int] = None


@dataclass
class DimensionSegmentInfo:
    """One segment of a dimension.

    A dimension chain in Revit is a *single* `Dimension` element with
    many segments, not many dimensions — worth knowing, because the DXF
    side of this project spent real effort reassembling chains from
    shared witness points and here they arrive pre-assembled.

    `value_mm` is what the model measures. `value_override` is the text
    a drafter typed to replace it, or None. Both are kept: the pair is
    what `pdfchecker`'s `geometry.dimension_consistency` compared, and
    the same comparison is available here without a DXF export.
    """

    value_mm: Optional[float] = None
    value_override: Optional[str] = None
    prefix: Optional[str] = None
    suffix: Optional[str] = None

    @property
    def is_overridden(self) -> bool:
        return bool(self.value_override)


@dataclass
class DimensionInfo:
    element_id: int
    view_id: int
    # Spot dimensions (spot elevation / spot coordinate / spot slope)
    # subclass Dimension in the API and are collected alongside them.
    # They matter here more than ordinary dimensions do, not less: a
    # spot coordinate placed on detail linework is a setout value that
    # looks authoritative and tracks nothing.
    is_spot: bool = False
    references: List[ReferenceInfo] = field(default_factory=list)
    segments: List[DimensionSegmentInfo] = field(default_factory=list)
    origin: Optional[Point3D] = None
    type_name: Optional[str] = None

    @property
    def value_mm(self) -> Optional[float]:
        """Total measured length across every segment, or None if the
        model reports no value (which Revit does for some spot types)."""
        values = [s.value_mm for s in self.segments if s.value_mm is not None]
        return sum(values) if values else None


@dataclass
class ViewInfo:
    element_id: int
    name: str
    # Revit's ViewType as a string, e.g. "FloorPlan", "Section",
    # "Elevation", "Detail", "DraftingView", "ThreeD".
    view_type: str
    is_template: bool = False
    scale: Optional[int] = None
    # The sheet this view is placed on, via its Viewport, or None if the
    # view is not on a sheet. An unplaced view is not issued to anyone,
    # so rules scope to placed views by default.
    sheet_id: Optional[int] = None
    sheet_no: Optional[str] = None
    # True when this view is a Drafting View displayed via a "Reference
    # other view" callout drawn on a Section/Plan — i.e. it stands in
    # for a section cut through the model rather than being a
    # free-standing standard detail. A Drafting View never contains
    # model geometry either way (see `is_drafting_view` below), but this
    # one is masquerading as a live section, so its drift risk is the
    # model-view kind, not the standard-detail kind. Defaults False,
    # the conservative direction — see `adapters/revit_source.py`'s note
    # on how this is populated and what still needs confirming on a
    # real Revit machine.
    linked_to_model_section: bool = False

    @property
    def is_drafting_view(self) -> bool:
        """A drafting view has no model behind it at all — every line in
        one is 2D by construction. That is not necessarily an error (a
        standard detail is legitimately drafted), so rules treat it as a
        different case rather than the same one, and say so."""
        return self.view_type in ("DraftingView", "Drafting", "Legend")


@dataclass
class SheetInfo:
    element_id: int
    sheet_number: str
    name: Optional[str] = None


@dataclass
class RevitModel:
    """One Revit document, reduced to what the checks need.

    This is what `capture.py` serializes and what every rule consumes,
    so a rule written against it runs identically on a live document at
    work and on a captured JSON file on a laptop.
    """

    doc_title: str
    sheets: List[SheetInfo] = field(default_factory=list)
    views: List[ViewInfo] = field(default_factory=list)
    dimensions: List[DimensionInfo] = field(default_factory=list)
    revit_version: Optional[str] = None
    captured_at: Optional[str] = None
    # Elements the adapter could not read. Per CLAUDE.md's "report a
    # coverage indicator, don't fail silently" convention: a capture
    # that quietly dropped 200 dimensions must not be indistinguishable
    # from a model that has none.
    extraction_errors: List[str] = field(default_factory=list)

    def view_by_id(self, view_id: Optional[int]) -> Optional[ViewInfo]:
        if view_id is None:
            return None
        return self._view_index().get(view_id)

    def dimensions_by_view(self) -> Dict[int, List[DimensionInfo]]:
        """Dimensions grouped by their owning view, in one pass.

        Rules that roll up per view use this rather than filtering the
        dimension list once per view — on a real set that is a few
        hundred views against a few thousand dimensions, and the naive
        form is the product of the two.
        """
        grouped: Dict[int, List[DimensionInfo]] = {}
        for dim in self.dimensions:
            grouped.setdefault(dim.view_id, []).append(dim)
        return grouped

    def _view_index(self) -> Dict[int, ViewInfo]:
        # Built once on first use. A RevitModel is assembled whole — by
        # the adapter or by a capture load — and not mutated afterwards,
        # so there is nothing for the cache to go stale against.
        index = getattr(self, "_view_index_cache", None)
        if index is None:
            index = {v.element_id: v for v in self.views}
            object.__setattr__(self, "_view_index_cache", index)
        return index
