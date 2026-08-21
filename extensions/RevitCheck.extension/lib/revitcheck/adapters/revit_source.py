"""Read an open Revit document into `RevitModel`.

**This is the only module in the package that imports the Revit API**,
and nothing imports it except the pyRevit scripts. Everything else stays
runnable on a machine with no Revit, which is the whole reason the
package is laid out this way.

It is also the only module that cannot be tested off a Revit machine, so
it is written to a deliberate rule: **extract facts, judge nothing.** No
classification, no tolerances, no filtering by view type. It reads what
Revit says and hands it over. Anything that might need tuning lives in
`checks/`, where it can be tuned and tested offline against a capture.

Failures are isolated per element and recorded in
`RevitModel.extraction_errors` rather than raised. One unreadable
dimension must not abort a capture of several thousand, and a capture
that quietly dropped elements must not look like a clean model —
`checks/coverage.py` turns those records into a visible Issue.

**Version notes**, both of which are real and worth knowing before
debugging anything here:

- `ElementId.IntegerValue` is deprecated from Revit 2024 in favour of
  `ElementId.Value` (Int64). `_eid` handles both, so this runs across
  versions.
- Revit's internal length unit is decimal feet, always, regardless of
  project display units. So converting to millimetres is `* 304.8` and
  needs no `UnitUtils` call — which also means no `UnitTypeId` versus
  `DisplayUnitType` branching, the usual source of version breakage.
"""

from __future__ import annotations

import datetime
from typing import Any, List, Optional

from revitcheck.ir import (
    MM_PER_FOOT,
    DimensionInfo,
    DimensionSegmentInfo,
    Point3D,
    ReferenceInfo,
    RevitModel,
    SheetInfo,
    ViewInfo,
)

try:
    from Autodesk.Revit.DB import (  # type: ignore
        Dimension,
        ElementId,
        FilteredElementCollector,
        FilteredWorksetCollector,
        SpotDimension,
        View,
        ViewSheet,
        Viewport,
        WorksetKind,
    )
except ImportError as exc:  # pragma: no cover - only reachable off Revit
    raise ImportError(
        "revitcheck.adapters.revit_source requires the Revit API and can "
        "only be imported from inside Revit (via pyRevit). Rules and IR do "
        "not import it — to work with a model off a Revit machine, load a "
        "capture with revitcheck.capture.load()."
    ) from exc


def _eid(element_id: Any) -> Optional[int]:
    """An ElementId as a plain int, across Revit versions.

    2024 deprecated `IntegerValue` in favour of the Int64 `Value`. Try
    the new name first so a future removal of the old one is a non-event.
    """
    if element_id is None:
        return None
    for attr in ("Value", "IntegerValue"):
        try:
            value = getattr(element_id, attr)
        except AttributeError:
            continue
        if value is not None:
            return int(value)
    return None


def _mm(feet: Optional[float]) -> Optional[float]:
    return None if feet is None else float(feet) * MM_PER_FOOT


def _point(xyz: Any) -> Optional[Point3D]:
    if xyz is None:
        return None
    return Point3D(x=_mm(xyz.X), y=_mm(xyz.Y), z=_mm(xyz.Z))


def list_worksets(doc: Any) -> List[Any]:
    """User-created worksets in `doc`, as `(workset_id, name)` pairs.

    Empty on a model that isn't workshared — `FilteredWorksetCollector`
    only means something on one that is. Exposed here, rather than left
    for the button to collect itself, so the picker (script.py, which
    stays thin by this file's own rule) has one place to get the list
    from and one place to get it wrong.
    """
    if not bool(getattr(doc, "IsWorkshared", False)):
        return []
    result = []
    try:
        for workset in FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset):
            workset_id = _eid(getattr(workset, "Id", None))
            if workset_id is not None:
                result.append((workset_id, str(workset.Name)))
    except Exception:  # noqa: BLE001 - no worksets beats a broken capture
        return []
    return result


def _workset_name(doc: Any, element: Any) -> Optional[str]:
    """The name of the workset `element` belongs to, or None.

    None on a non-workshared model and on any lookup failure alike —
    both mean "nothing to filter on", and neither is worth an
    `extraction_errors` entry of its own since the element itself is
    still read normally either way. Not the same kind of fact as
    `ReferenceInfo` records, so failing open here (never excluding an
    element because its workset couldn't be determined) matches
    `extraction_errors`' own principle: a capture must not quietly
    shrink for a reason nobody can see.
    """
    try:
        workset_id = getattr(element, "WorksetId", None)
        if workset_id is None:
            return None
        workset = doc.GetWorksetTable().GetWorkset(workset_id)
        return str(workset.Name) if workset is not None else None
    except Exception:  # noqa: BLE001
        return None


def _text_or_none(value: Any) -> Optional[str]:
    """Revit returns an empty string for an unset text property, which
    is a different thing from a drafter typing one. Normalize to None so
    `is_overridden` means what it says."""
    if value is None:
        return None
    text = str(value).strip()
    return text or None


def _read_reference(doc: Any, ref: Any, errors: List[str]) -> ReferenceInfo:
    """Describe one dimension endpoint.

    The linked case matters on bridge projects, where the structure is
    routinely linked into a coordination file: `Reference.ElementId`
    then points at the `RevitLinkInstance`, not at the geometry the
    dimension actually measures. Follow `LinkedElementId` into the link
    document so the facts describe the real element — otherwise every
    dimension to a linked beam looks like a dimension to a link.
    """
    raw_id = _eid(getattr(ref, "ElementId", None))
    info = ReferenceInfo(element_id=raw_id if raw_id is not None else -1)

    try:
        element = doc.GetElement(ref.ElementId) if raw_id is not None else None

        linked_id = _eid(getattr(ref, "LinkedElementId", None))
        if linked_id is not None and linked_id > 0 and element is not None:
            get_link_doc = getattr(element, "GetLinkDocument", None)
            if get_link_doc is not None:
                link_doc = get_link_doc()
                if link_doc is not None:
                    inner = link_doc.GetElement(ElementId(linked_id))
                    if inner is not None:
                        info.linked = True
                        info.link_instance_id = raw_id
                        info.element_id = linked_id
                        element = inner

        if element is None:
            info.resolved = False
            return info

        info.class_name = type(element).__name__
        info.view_specific = bool(getattr(element, "ViewSpecific", False))

        category = getattr(element, "Category", None)
        if category is not None:
            info.category = category.Name
            info.builtin_category = _eid(category.Id)

    except Exception as exc:  # noqa: BLE001 - isolation is the point
        info.resolved = False
        errors.append("reference on element {0}: {1}".format(raw_id, exc))

    return info


def _read_segments(dim: Any) -> List[DimensionSegmentInfo]:
    """A dimension's measured values.

    A dimension *chain* in Revit is one `Dimension` element carrying
    many segments, not many elements — the opposite of the DXF side,
    where chains had to be reassembled from shared witness points.
    `NumberOfSegments` is 0 for a plain single-value dimension, in which
    case the value lives on the dimension itself.
    """
    prefix = _text_or_none(getattr(dim, "Prefix", None))
    suffix = _text_or_none(getattr(dim, "Suffix", None))

    count = int(getattr(dim, "NumberOfSegments", 0) or 0)
    if count == 0:
        return [
            DimensionSegmentInfo(
                value_mm=_mm(getattr(dim, "Value", None)),
                value_override=_text_or_none(getattr(dim, "ValueOverride", None)),
                prefix=prefix,
                suffix=suffix,
            )
        ]

    segments = []
    for seg in dim.Segments:
        segments.append(
            DimensionSegmentInfo(
                value_mm=_mm(getattr(seg, "Value", None)),
                value_override=_text_or_none(getattr(seg, "ValueOverride", None)),
                prefix=_text_or_none(getattr(seg, "Prefix", None)) or prefix,
                suffix=_text_or_none(getattr(seg, "Suffix", None)) or suffix,
            )
        )
    return segments


def _collect_dimensions(
    doc: Any,
    errors: List[str],
    views: List[ViewInfo],
    include_worksets: Optional[Any] = None,
    sheeted_views_only: bool = True,
) -> List[DimensionInfo]:
    """Every dimension in the document, spot dimensions included.

    **Collected per view**, not once document-wide. The original design
    trusted `Dimension.OwnerViewId` from a single
    `FilteredElementCollector(doc)` sweep, and that trust was wrong:
    confirmed 2026-08-22 against a real capture (`samples/T2DPAA...`),
    `OwnerViewId` had attributed 430 dimensions to one Elevation view
    that a reviewer counted at roughly a dozen. Select-by-ID in Revit
    confirmed several of the "extra" ones could not be selected while
    that view was active — view-specific elements can only be selected
    while their real owning view is active, so they did not actually
    belong there, whatever the stored property said. Changing the
    active view at capture time made no difference, ruling that out as
    the mechanism; what's left is that the stored property itself isn't
    reliable read document-wide. `FilteredElementCollector(doc,
    view.Id)` sidesteps the question rather than explains it: `view_id`
    below is the view this loop is currently on, known from the loop
    variable, not read back off a property that turned out not to be
    trustworthy.

    Scoped to views placed on a sheet by default
    (`sheeted_views_only`) — confirmed by the user as the right default
    for this project specifically: a heavy template leaves thousands of
    premade, never-placed views in the document, and a
    `FilteredElementCollector` call per view is real cost multiplied by
    however many views there are. An unplaced view is not issued to
    anyone either way (`checks/dimensions.py`'s own default), so this
    is the adapter doing the same narrowing the checks already do, for
    the same reason `include_worksets` does: avoiding the read, not
    just filtering the result afterwards. Set False for a full sweep —
    note that doing so only at the *check* layer (`RuleConfig.
    sheeted_views_only=False`) can no longer recover dimensions this
    layer never captured, so a full sweep now has to be asked for here
    too if one is wanted.

    `OfClass(Dimension)` returns `SpotDimension` too, since it derives
    from `Dimension` — but both are collected and deduplicated by id
    rather than relying on that, because it costs nothing and a missing
    population would be invisible. Spot dimensions matter here more than
    ordinary ones, not less: a spot coordinate placed on detail linework
    is a setout value that looks authoritative and tracks nothing.

    `include_worksets`, when not None, is a set of workset *names* — a
    dimension whose workset resolves and isn't in it is skipped before
    its references are read at all, which is the point: worksets like
    "Superseded" or "Geometry Creation" hold real volume on a bridge
    project, and there is no reason to pay for reading references on a
    dimension nobody wants checked.
    """
    seen = {}
    for view in views:
        if sheeted_views_only and view.sheet_no is None:
            continue

        try:
            view_element_id = ElementId(view.element_id)
        except Exception as exc:  # noqa: BLE001
            errors.append(
                "view {0}: could not scope a collector to it: {1}".format(
                    view.element_id, exc
                )
            )
            continue

        for cls in (Dimension, SpotDimension):
            try:
                collector = (
                    FilteredElementCollector(doc, view_element_id)
                    .OfClass(cls)
                    .WhereElementIsNotElementType()
                )
            except Exception as exc:  # noqa: BLE001
                errors.append(
                    "collecting {0} in view {1}: {2}".format(
                        cls.__name__, view.element_id, exc
                    )
                )
                continue

            for element in collector:
                element_id = _eid(element.Id)
                if element_id is None or element_id in seen:
                    continue

                workset_name = _workset_name(doc, element)
                if (
                    include_worksets is not None
                    and workset_name is not None
                    and workset_name not in include_worksets
                ):
                    continue

                try:
                    references = [
                        _read_reference(doc, ref, errors)
                        for ref in (element.References or [])
                    ]

                    origin = None
                    try:
                        origin = _point(getattr(element, "Origin", None))
                    except Exception:  # noqa: BLE001
                        # Some dimension geometries have no single origin.
                        # Not worth an error record — it costs a zoom
                        # target, not a finding.
                        origin = None

                    type_name = None
                    dim_type = getattr(element, "DimensionType", None)
                    if dim_type is not None:
                        type_name = getattr(dim_type, "Name", None)

                    seen[element_id] = DimensionInfo(
                        element_id=element_id,
                        view_id=view.element_id,
                        is_spot=isinstance(element, SpotDimension),
                        references=references,
                        segments=_read_segments(element),
                        origin=origin,
                        type_name=type_name,
                        workset_name=workset_name,
                        unique_id=_text_or_none(getattr(element, "UniqueId", None)),
                    )
                except Exception as exc:  # noqa: BLE001
                    errors.append("dimension {0}: {1}".format(element_id, exc))

    return list(seen.values())


def _referenced_drafting_view_ids(doc: Any, errors: List[str]):
    """Element ids of every Drafting View referenced by a "Reference
    other view" callout drawn on a Section or Plan.

    **Not yet implemented — always returns an empty set.** Every
    Drafting View comes back `ViewInfo.linked_to_model_section=False`
    until this is filled in. That is the conservative direction to fail
    in: an unconfirmed "yes" would silently exclude a view from
    `revit.dimension_provenance` that might carry real drift risk (see
    `checks/dimensions.py`'s `skip_unlinked_drafting_views`); an
    unconfirmed "no" only costs the volume that config option exists to
    cut, which is the worse of two acceptable failure directions, not an
    unsafe one.

    What's confirmed: `BuiltInCategory.OST_Callouts` is the annotation
    category a callout boundary draws with — visible today in
    Visibility/Graphics as "Callouts" under Annotation Categories. What
    is **not** confirmed, because it needs a real workshared model and
    Revit's own API reference (Copilot, at the Revit machine — this
    module cannot be tested anywhere else, see the module docstring):
    whether callout boundaries collect as distinct elements via
    `FilteredElementCollector(doc, view.Id).OfCategory(OST_Callouts)`,
    and if they do, which property on one names the Drafting View it
    points at for the "Reference other view" case specifically. (A
    *normal* callout, which creates its own child view instead of
    referencing an existing one, needs nothing special here — that
    child view is already just another `View` the loop below picks up.)
    """
    return set()


def _collect_sheets_and_views(
    doc: Any, errors: List[str], include_worksets: Optional[Any] = None
):
    """Every sheet and every view — neither filtered by `include_worksets`.

    Both are index/container entities, not volume: a sheet just names a
    page, and a view just names a scope dimensions live inside. Only
    the dimensions themselves (`_collect_dimensions`) are filtered by
    their own workset. Views weren't originally treated this way — see
    the comment where `workset_name` is read below for why that changed
    and what it broke before it did.
    """
    sheets: List[SheetInfo] = []
    sheet_by_id = {}

    for sheet in FilteredElementCollector(doc).OfClass(ViewSheet):
        try:
            sheet_id = _eid(sheet.Id)
            info = SheetInfo(
                element_id=sheet_id,
                sheet_number=str(sheet.SheetNumber),
                name=str(sheet.Name),
            )
            sheets.append(info)
            sheet_by_id[sheet_id] = info
        except Exception as exc:  # noqa: BLE001
            errors.append("sheet {0}: {1}".format(_eid(sheet.Id), exc))

    # A view knows nothing about the sheet it sits on; the Viewport
    # linking them is a separate element. Build the map first so each
    # view can be tagged in one pass.
    view_to_sheet = {}
    for viewport in FilteredElementCollector(doc).OfClass(Viewport):
        try:
            view_to_sheet[_eid(viewport.ViewId)] = _eid(viewport.SheetId)
        except Exception as exc:  # noqa: BLE001
            errors.append("viewport {0}: {1}".format(_eid(viewport.Id), exc))

    referenced_drafting_views = _referenced_drafting_view_ids(doc, errors)

    views: List[ViewInfo] = []
    for view in FilteredElementCollector(doc).OfClass(View):
        try:
            view_id = _eid(view.Id)
            # A view is never skipped for its own workset. Found the
            # hard way, 2026-08-22: a real project keeps every view on
            # one administrative workset, so filtering views the same
            # way dimensions are filtered meant deselecting that one
            # workset in the Capture Model picker silently produced a
            # capture with zero views and therefore zero dimensions.
            # `workset_name` is still recorded below, purely
            # informational.
            workset_name = _workset_name(doc, view)
            sheet_id = view_to_sheet.get(view_id)
            sheet = sheet_by_id.get(sheet_id)
            views.append(
                ViewInfo(
                    element_id=view_id,
                    name=str(view.Name),
                    view_type=str(view.ViewType),
                    is_template=bool(view.IsTemplate),
                    scale=int(getattr(view, "Scale", 0) or 0) or None,
                    sheet_id=sheet_id,
                    sheet_no=sheet.sheet_number if sheet else None,
                    workset_name=workset_name,
                    linked_to_model_section=view_id in referenced_drafting_views,
                    unique_id=_text_or_none(getattr(view, "UniqueId", None)),
                )
            )
        except Exception as exc:  # noqa: BLE001
            errors.append("view {0}: {1}".format(_eid(view.Id), exc))

    return sheets, views


def read_model(
    doc: Any,
    include_worksets: Optional[Any] = None,
    sheeted_views_only: bool = True,
) -> RevitModel:
    """Read the whole document into the IR.

    Read-only throughout — no transaction is opened, and none should be.
    A check that silently edited the model while reporting on it would
    be exactly the kind of black box CLAUDE.md rules out.

    `include_worksets`, when given, is a set of workset *names* to keep;
    dimensions on any other workset are skipped during collection
    rather than filtered afterwards, so the cost of reading them is
    avoided too, not just their presence in the output. `None` (the
    default) reads everything, unchanged from before this parameter
    existed. Sheets and views are never filtered by workset — both are
    index/container entities, not volume, and (found 2026-08-22, the
    hard way) a project can keep every view on one administrative
    workset, so filtering views the way dimensions are filtered risks
    a picker selection silently producing a capture with nothing in it
    at all.

    `sheeted_views_only` (default True) skips dimension collection for
    any view not placed on a sheet — see `_collect_dimensions` for why
    this now lives here rather than only at the check layer.
    """
    errors: List[str] = []
    sheets, views = _collect_sheets_and_views(doc, errors, include_worksets)
    dimensions = _collect_dimensions(
        doc, errors, views, include_worksets, sheeted_views_only
    )

    excluded_worksets: List[str] = []
    if include_worksets is not None:
        excluded_worksets = sorted(
            name for _wid, name in list_worksets(doc) if name not in include_worksets
        )

    version = None
    try:
        version = str(doc.Application.VersionNumber)
    except Exception:  # noqa: BLE001
        version = None

    return RevitModel(
        doc_title=str(getattr(doc, "Title", "")),
        sheets=sheets,
        views=views,
        dimensions=dimensions,
        revit_version=version,
        captured_at=datetime.datetime.now().isoformat(timespec="seconds"),
        extraction_errors=errors,
        excluded_worksets=excluded_worksets,
    )
