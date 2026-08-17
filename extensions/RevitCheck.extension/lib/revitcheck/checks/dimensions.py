"""Dimension provenance — does a dimension measure the model, or a
picture of the model?

**The problem, in the user's words (PLANNING.md §5, 2026-08-17):** some
drafting teams don't use live sections. They draw setout details as
static 2D linework to save time, and those details always drift as the
model changes. The 2D-vs-model drift is the live problem this whole
project exists to catch, and the reason the earlier PDF/DWG approach
struggled with it is worth restating, because it is exactly what this
module makes trivial.

From a DXF export, the two drafting workarounds for the same underlying
problem (curved bridge geometry making sections non-perpendicular, so
model-derived dimensions land a few mm out) look like this:

1. **Overwrite the dimension text.** The model's own measurement
   survives alongside the override, so the discrepancy is visible in the
   file. Checkable.
2. **Draw small witness lines and dimension to those.** The dimension
   agrees perfectly with the line it measures, so the file is
   *internally consistent while collectively stale*. When the model
   moves, line and dimension drift together, still agreeing. There is
   no discrepancy inside the file to find at all.

Case 2 is the dangerous one and it was undetectable from the export
except by proxy — the best available signal was the CAD layer of the
geometry nearest each witness point (BR06: 44/60 on `D-BDGE`, a model
category; Flinders: 50/52 on `A-DETL`, detail linework). A reasonable
proxy, but a proxy.

Inside Revit it is not a proxy. A `Dimension` holds `References`, each
resolving to a real element, and Revit itself records whether that
element is view-specific — that is, whether it belongs to one view and
therefore cannot track the model. So "is this dimension going to go
stale?" stops being an inference and becomes a lookup.

**Why this is the right first tool.** It needs no tolerances, no
reconstruction, no IFC and no per-client calibration, and its output is
precisely the input the harder follow-up tool needs: the set of views
whose dimensions are all drafted, and therefore whose setout can only be
verified against the model itself.

**What it deliberately does not do.** It does not decide whether a
drafted dimension is *wrong* — only that the file cannot answer the
question. Per the user's standing position, recorded in PLANNING.md §5:
*assume nothing is trustworthy or you will be caught out.* An override
can be stale exactly as a witness line can. Classification here is
triage, telling you how to interpret a view, not a filter deciding what
gets checked.
"""

from __future__ import annotations

from typing import Dict, List, Optional

from revitcheck.catalog import RuleConfig, register
from revitcheck.ir import DimensionInfo, Provenance, ReferenceInfo, RevitModel, ViewInfo
from revitcheck.issue import Issue

# Datums are model elements that other model geometry is positioned
# against. Dimensioning to a grid or a level is good practice, not a
# risk: move the grid and the dimension follows.
DATUM_CLASSES = frozenset(
    {"Grid", "MultiSegmentGrid", "Level", "ReferencePlane", "DatumPlane"}
)

# An imported CAD file is the one case that is neither view-specific nor
# model geometry. It matters here: importing a survey or a consultant's
# DWG and dimensioning to it is common on bridge projects, and the
# result is a dimension anchored to a static snapshot of somebody else's
# file. Same failure mode as detail linework, different mechanism.
DRAFTED_CLASSES = frozenset({"ImportInstance"})

# Below this, "every dimension in the view is drafted" is not a
# meaningful statement about the view — a view holding one dimension
# says nothing about how it was drafted. Those fall through to
# per-dimension reporting instead.
_MIN_DIMS_FOR_VIEW_ROLLUP = 2


def classify_reference(ref: ReferenceInfo) -> str:
    """Classify one dimension endpoint.

    Order matters. `view_specific` is checked first and beats everything
    else, because it is Revit's own record of "this element belongs to a
    single view" — the API-level invariant, not a naming convention. The
    Flinders exercise is the argument for leaning on it: logic built on
    domain invariants held across clients, logic built on client
    conventions broke.
    """
    if not ref.resolved or ref.element_id is None or ref.element_id <= 0:
        return Provenance.UNKNOWN
    if ref.view_specific:
        return Provenance.DRAFTED
    if ref.class_name in DRAFTED_CLASSES:
        return Provenance.DRAFTED
    if ref.class_name in DATUM_CLASSES:
        return Provenance.DATUM
    return Provenance.MODEL


def classify_dimension(dim: DimensionInfo) -> str:
    """Roll a dimension's endpoints up into a single verdict.

    A dimension with no resolvable references at all is UNKNOWN rather
    than assumed innocent — CLAUDE.md's "report a coverage indicator,
    don't fail silently".
    """
    found = set(classify_reference(r) for r in dim.references)
    if not found:
        return Provenance.UNKNOWN

    known = found - {Provenance.UNKNOWN}
    if not known:
        return Provenance.UNKNOWN

    live = known & {Provenance.MODEL, Provenance.DATUM}
    if Provenance.DRAFTED in known:
        return Provenance.MIXED if live else Provenance.DRAFTED
    if known == {Provenance.DATUM}:
        return Provenance.DATUM
    return Provenance.MODEL


def views_in_scope(model: RevitModel, config: RuleConfig) -> List[ViewInfo]:
    """Views a check should look at.

    View templates never hold real dimensions. Unplaced views are
    excluded by default because nothing in them is issued to anyone, and
    flagging in-progress work is how a check earns a reputation for
    noise it never recovers from.
    """
    scoped = []
    for view in model.views:
        if view.is_template:
            continue
        if config.sheeted_views_only and view.sheet_no is None:
            continue
        scoped.append(view)
    return scoped


def _view_type_label(view_type: str) -> str:
    """Revit's ViewType name as something readable in a sentence.

    "DraftingView" -> "drafting", "FloorPlan" -> "floor plan". These
    strings end up in issue descriptions a person reads, and "DraftingView
    view" is the kind of wording that makes a tool feel machine-generated.
    """
    words: List[str] = []
    current = ""
    for char in view_type:
        if char.isupper() and current:
            words.append(current)
            current = char
        else:
            current += char
    if current:
        words.append(current)
    if len(words) > 1 and words[-1] == "View":
        words = words[:-1]
    return " ".join(w.lower() for w in words)


def _describe_view(view: Optional[ViewInfo]) -> str:
    if view is None:
        return "an unknown view"
    label = "{0} view '{1}'".format(_view_type_label(view.view_type), view.name)
    if view.sheet_no:
        label += " (sheet {0})".format(view.sheet_no)
    return label


def _drafted_severity(view: Optional[ViewInfo], config: RuleConfig) -> str:
    if view is not None and view.is_drafting_view:
        return config.drafted_in_drafting_view_severity
    return config.drafted_in_model_view_severity


def _issue_for_dimension(
    dim: DimensionInfo, view: Optional[ViewInfo], verdict: str, config: RuleConfig
) -> Optional[Issue]:
    kind = "Spot dimension" if dim.is_spot else "Dimension"
    common = dict(
        rule_id="revit.dimension_provenance",
        category="geometry",
        element_id=dim.element_id,
        view_id=dim.view_id,
        view_name=view.name if view else None,
        sheet_no=view.sheet_no if view else None,
    )

    if verdict == Provenance.DRAFTED:
        if view is not None and view.is_drafting_view:
            detail = (
                "{0} in {1} measures detail linework. A drafting view has no "
                "model behind it, so this cannot track the model by any means "
                "— correct for a standard detail, a drift risk if it is "
                "project-specific setout."
            ).format(kind, _describe_view(view))
        else:
            detail = (
                "{0} in {1} measures detail linework, not model geometry — it "
                "will not update when the model changes, and will keep "
                "agreeing with the line it measures while doing so."
            ).format(kind, _describe_view(view))
        return Issue(
            description=detail,
            severity=_drafted_severity(view, config),
            suggested_fix={
                "provenance": Provenance.DRAFTED,
                "references": len(dim.references),
                "action": "re-dimension to model geometry, or verify against the model",
            },
            **common
        )

    if verdict == Provenance.MIXED:
        return Issue(
            description=(
                "{0} in {1} measures model geometry at one end and detail "
                "linework at the other, so part of it tracks the model and "
                "part of it does not."
            ).format(kind, _describe_view(view)),
            severity=config.mixed_provenance_severity,
            suggested_fix={
                "provenance": Provenance.MIXED,
                "drafted_references": sum(
                    1
                    for r in dim.references
                    if classify_reference(r) == Provenance.DRAFTED
                ),
                "references": len(dim.references),
            },
            **common
        )

    if verdict == Provenance.UNKNOWN:
        return Issue(
            description=(
                "{0} in {1} has references that could not be resolved, so "
                "whether it tracks the model is unknown — it was not checked."
            ).format(kind, _describe_view(view)),
            severity="low",
            suggested_fix={"provenance": Provenance.UNKNOWN},
            **common
        )

    return None


@register("revit.dimension_provenance")
def check_dimension_provenance(model: RevitModel, config: RuleConfig) -> List[Issue]:
    """Flag dimensions that measure linework rather than the model.

    Output is rolled up per view where that is the honest summary: if
    *every* dimension in a view is drafted, one view-level issue is
    reported instead of twenty identical dimension-level ones. That is
    not just noise control — a wholly-drafted view is a different and
    larger finding than a stray drafted dimension in an otherwise live
    view, and it is the unit the follow-up "verify these against the
    model" tool will operate on. Set `roll_up_fully_drafted_views` off
    in config to get the per-dimension form regardless.
    """
    roll_up = config.params.get("dimension_provenance", {}).get(
        "roll_up_fully_drafted_views", True
    )

    issues: List[Issue] = []
    by_view = model.dimensions_by_view()
    scoped = views_in_scope(model, config)
    checked_any = False

    for view in scoped:
        dims = by_view.get(view.element_id, [])
        if not dims:
            continue
        checked_any = True

        verdicts = dict((d.element_id, classify_dimension(d)) for d in dims)
        drafted = [d for d in dims if verdicts[d.element_id] == Provenance.DRAFTED]

        fully_drafted = (
            roll_up
            and len(dims) >= _MIN_DIMS_FOR_VIEW_ROLLUP
            and len(drafted) == len(dims)
        )

        if fully_drafted:
            # The drafting-view case has to read differently here too,
            # not just in the per-dimension path. A section with no live
            # dimensions is someone's choice; a drafting view never had
            # a model behind it to begin with, and describing the two
            # identically would make the more serious one easy to skip.
            if view.is_drafting_view:
                summary = (
                    "Every dimension in {0} ({1} of them) is taken from detail "
                    "linework. A drafting view has no model behind it, so that "
                    "is expected for a standard detail — but if this view "
                    "carries project-specific setout, nothing in the file can "
                    "show whether it has drifted."
                ).format(_describe_view(view), len(dims))
            else:
                summary = (
                    "Every dimension in {0} ({1} of them) is taken from detail "
                    "linework. Nothing in this view tracks the model, and "
                    "nothing in the file can show whether it has drifted — it "
                    "can only be verified against the model itself."
                ).format(_describe_view(view), len(dims))

            issues.append(
                Issue(
                    rule_id="revit.dimension_provenance",
                    category="geometry",
                    description=summary,
                    severity=_drafted_severity(view, config),
                    element_id=view.element_id,
                    view_id=view.element_id,
                    view_name=view.name,
                    sheet_no=view.sheet_no,
                    suggested_fix={
                        "provenance": Provenance.DRAFTED,
                        "dimensions": len(dims),
                        "scope": "view",
                        "action": "verify this view's setout against the model",
                    },
                )
            )
            continue

        for dim in dims:
            issue = _issue_for_dimension(dim, view, verdicts[dim.element_id], config)
            if issue is not None:
                issues.append(issue)

    if not checked_any:
        issues.append(
            Issue(
                rule_id="revit.dimension_provenance",
                category="coverage",
                description=(
                    "No dimensions were found in any view in scope ({0} views "
                    "checked{1}), so this rule reported nothing because there "
                    "was nothing to report on — not because the model is clean."
                ).format(
                    len(scoped),
                    ", placed on sheets" if config.sheeted_views_only else "",
                ),
                severity="low",
            )
        )

    return issues


def drafted_views(model: RevitModel, config: RuleConfig) -> List[ViewInfo]:
    """Views whose dimensions are *all* drafted.

    Exposed separately from the rule because it is the handoff to the
    planned follow-up tool: these are the views whose setout cannot be
    checked from the file and has to be compared against the model. A
    caller wanting that list should not have to parse it back out of
    Issue descriptions.
    """
    by_view = model.dimensions_by_view()
    result = []
    for view in views_in_scope(model, config):
        dims = by_view.get(view.element_id, [])
        if len(dims) < _MIN_DIMS_FOR_VIEW_ROLLUP:
            continue
        if all(classify_dimension(d) == Provenance.DRAFTED for d in dims):
            result.append(view)
    return result
