"""Dimension checks: where a dimension's measurement comes from, and
whether the number printed on the drawing matches it.

Two rules live here because they are the two halves of one problem, and
they read each other's classification:

- `revit.dimension_provenance` — does this dimension measure the model,
  or a picture of the model?
- `revit.dimension_override_consistency` — where a drafter has replaced
  the measured value with typed text, do the two still agree within
  what rounding can explain?

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

Case 1 is what `revit.dimension_override_consistency` covers, and Revit
makes it cheaper still: `ValueOverride` sits directly next to the
model's own `Value` on the same segment. The DXF pipeline needed an
export, a unit table and a text-parsing pass to recover that pair; here
it arrives assembled, in millimetres.

**Why provenance was the right first tool.** It needs no tolerances, no
reconstruction, no IFC and no per-client calibration, and its output is
precisely the input the harder follow-up tool needs: the set of views
whose dimensions are all drafted, and therefore whose setout can only be
verified against the model itself.

**What neither rule does.** Neither decides whether a *drafted*
dimension is wrong — only that the file cannot answer the question. Per
the user's standing position, recorded in PLANNING.md §5: *assume
nothing is trustworthy or you will be caught out.* An override can be
stale exactly as a witness line can, and an override on a drafted
dimension is being compared against the length of a detail line rather
than against the model — still worth reporting, but it means something
different, so the provenance verdict travels on the Issue. Classification
here is triage, telling you how to read a finding, not a filter deciding
what gets checked.
"""

from __future__ import annotations

import re
from typing import Dict, List, Optional, Tuple

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

# Fraction of a view's dimensions that must classify as DRAFTED before
# the view rolls up to one issue instead of reporting each dimension.
# **Calibrated against the first real capture** (T2DPAA-T2D-C3S-BR-M3D
# -100304, 2026-08-21, 134 sheets / 1566 views / 17117 dimensions), not
# inherited as a placeholder. The rollup originally required literally
# every dimension to be DRAFTED, which sounds equivalent to "the view is
# drafted" but is not: on that capture, one elevation view had 946
# drafted dimensions and 4 model-backed ones, so the 100% rule fell
# through to 946 individual issues for a view whose story is one
# sentence long. At this default (0.9) the same run's dimension_provenance
# volume drops from 4434 to 1042 issues with no finding actually lost —
# see `_issue_for_view_rollup`'s docstring for what still gets reported
# individually inside a rolled-up view.
_DEFAULT_ROLLUP_THRESHOLD = 0.9


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


def _view_rollup_issue(
    view: ViewInfo, dims: List[DimensionInfo], drafted: List[DimensionInfo], config: RuleConfig
) -> Issue:
    """One issue standing in for a view whose dimensions are (almost)
    all drafted.

    Two wordings, not one, because "every" and "946 of 950" are
    different claims and a reader should not have to open the model to
    find out which one this is. The drafting-view branch of each still
    has to read differently from the model-view branch — see the
    docstring on `check_dimension_provenance`.
    """
    fully_drafted = len(drafted) == len(dims)
    if fully_drafted:
        subject = "Every dimension in {0} ({1} of them)".format(
            _describe_view(view), len(drafted)
        )
        verb = "is"
    else:
        subject = "{0} of {1} dimensions in {2}".format(
            len(drafted), len(dims), _describe_view(view)
        )
        verb = "are"

    if view.is_drafting_view:
        summary = (
            "{0} {1} taken from detail linework. A drafting view has no "
            "model behind it, so that is expected for a standard detail "
            "— but if this view carries project-specific setout, nothing "
            "in the file can show whether it has drifted."
        ).format(subject, verb)
    else:
        summary = (
            "{0} {1} taken from detail linework. Nothing in {2} tracks "
            "the model, and nothing in the file can show whether it has "
            "drifted — it can only be verified against the model itself."
        ).format(subject, verb, "this view" if fully_drafted else "that part of the view")

    return Issue(
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
            "drafted_dimensions": len(drafted),
            "scope": "view",
            "action": "verify this view's setout against the model",
        },
    )


@register("revit.dimension_provenance")
def check_dimension_provenance(model: RevitModel, config: RuleConfig) -> List[Issue]:
    """Flag dimensions that measure linework rather than the model.

    Output is rolled up per view where that is the honest summary: once
    at least `rollup_threshold` (default `_DEFAULT_ROLLUP_THRESHOLD`,
    0.9) of a view's dimensions are drafted, one view-level issue is
    reported instead of one per dimension. That is not just noise
    control — a wholly- or almost-wholly-drafted view is a different and
    larger finding than a stray drafted dimension in an otherwise live
    view, and it is the unit the follow-up "verify these against the
    model" tool will operate on. The threshold is deliberately not 100%:
    see `_DEFAULT_ROLLUP_THRESHOLD`'s comment for the real capture that
    showed why a single live dimension in an otherwise-drafted view
    should not suppress the rollup. Set `roll_up_fully_drafted_views` off
    in config to get the per-dimension form regardless, or tune
    `rollup_threshold` (0.0-1.0) directly.
    """
    roll_up = config.params.get("dimension_provenance", {}).get(
        "roll_up_fully_drafted_views", True
    )
    threshold = config.params.get("dimension_provenance", {}).get(
        "rollup_threshold", _DEFAULT_ROLLUP_THRESHOLD
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

        rolls_up = (
            roll_up
            and len(dims) >= _MIN_DIMS_FOR_VIEW_ROLLUP
            and drafted
            and len(drafted) / len(dims) >= threshold
        )

        if rolls_up:
            issues.append(_view_rollup_issue(view, dims, drafted, config))
            # A rolled-up view can still hold the rarer verdicts — MIXED
            # or UNKNOWN dimensions that survived because they are not
            # DRAFTED and so were not counted towards the threshold.
            # Below `_DEFAULT_ROLLUP_THRESHOLD` = 1.0 those can coexist
            # with a rollup (they could not when the rule required every
            # dimension to be DRAFTED), and they are distinct findings
            # the rollup's "detail linework" summary does not cover, so
            # they are still reported individually. MODEL/DATUM
            # dimensions need no issue either way.
            for dim in dims:
                if verdicts[dim.element_id] in (Provenance.MIXED, Provenance.UNKNOWN):
                    issue = _issue_for_dimension(dim, view, verdicts[dim.element_id], config)
                    if issue is not None:
                        issues.append(issue)
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


# --- revit.dimension_override_consistency ---------------------------------
#
# Ported from the parked PDF/DWG pipeline's `geometry.dimension_consistency`
# (ARCHIVE-pdf-dwg.md; the code is at `git checkout pdf-dwg-final`). What
# survives the port is the tolerance model and the discipline about what
# not to guess at; the machinery around it — a unit table driven by the
# DXF header's `$INSUNITS`, a `dim_type == 0` filter to exclude non-linear
# dimensions, a page-space bbox transform so a drafter could find the
# thing — is all gone. `DimensionSegmentInfo` arrives in millimetres with
# both values on it, and an element id is a better location than a box.

# Zero-width and bidi format characters. The DXF export carried a literal
# trailing U+200E on some override text — invisible in a terminal and in
# an editor, so a valid override failed to parse for a reason with no
# visible cause. Not yet observed coming out of the Revit API, and cheap
# enough that finding out the hard way is not worth it.
_FORMAT_CHARS = "​‌‍‎‏‪‫‬‭‮"

# Overrides that end in a unit. Revit shows units according to the
# dimension's own format, and a drafter retyping a value often retypes
# the unit with it.
_UNIT_SUFFIXES = ("mm", "MM")


def parse_override_mm(text: Optional[str]) -> Optional[float]:
    """A dimension's override text as a millimetre value, or None.

    None means **not checkable**, never "assume it is fine". Real
    overrides that land here: `EQ`, `VARIES`, `TYP`, a bar-mark letter
    keying into a schedule table, a range, a value with a qualifier. The
    rule counts and reports these rather than dropping them silently —
    a client whose overrides are all `'<>\\XMIN'` would otherwise get a
    clean result from a rule that checked nothing, which is exactly what
    happened on the Flinders sample (4.5% overridden, none numeric).
    """
    if text is None:
        return None

    cleaned = text
    for char in _FORMAT_CHARS:
        cleaned = cleaned.replace(char, "")
    cleaned = cleaned.strip()

    for suffix in _UNIT_SUFFIXES:
        if cleaned.endswith(suffix):
            cleaned = cleaned[: -len(suffix)].strip()

    # A thousands separator is presentation, not content. A decimal
    # comma is a different convention and would need real evidence
    # before being guessed at, so only the separator form is handled:
    # "1,200" -> 1200, while "1,2" is left to fail.
    if "," in cleaned:
        head, _, tail = cleaned.rpartition(",")
        if head and len(tail) == 3 and tail.isdigit():
            cleaned = head.replace(",", "") + tail

    try:
        return float(cleaned)
    except ValueError:
        return None


# `500 MIN.`, `MIN 500`, `1200 MAX`. A real override form from the
# Flinders sample, and a common one: the drafter is stating a limit the
# built work must respect, not a value they measured.
_BOUND_RE = re.compile(
    r"^(?:(?P<lead>MIN|MAX)\.?\s*(?P<lead_value>-?[\d.,]+)"
    r"|(?P<value>-?[\d.,]+)\s*(?P<trail>MIN|MAX)\.?)$",
    re.IGNORECASE,
)

# Which way each keyword constrains the measured value.
_BOUND_COMPARATOR = {"MIN": ">=", "MAX": "<="}


def parse_override_bound(text: Optional[str]) -> Optional[Tuple[float, str]]:
    """A limit-style override as `(value_mm, ">=" | "<=")`, or None.

    Separate from `parse_override_mm` because the two are different
    claims and get compared differently. An exact override says "this
    measures 1200" and is checked against the rounding grid, since
    rounding is exactly what produced it. `500 MIN.` says nothing about
    rounding at all — it is a limit, so the only slack that applies is
    measurement noise.

    Built because the parked pipeline skipped these outright, and its
    own notes flag that as arguably wrong: on the client where almost no
    override was numeric, the ones that existed were `'500 MIN.'` and
    `'<>\\XMIN'` — so treating a stated limit as uncheckable threw away
    most of what that client's drawings actually assert.
    """
    if text is None:
        return None

    cleaned = text
    for char in _FORMAT_CHARS:
        cleaned = cleaned.replace(char, "")
    cleaned = cleaned.strip()

    for suffix in _UNIT_SUFFIXES:
        if cleaned.endswith(suffix):
            cleaned = cleaned[: -len(suffix)].strip()

    match = _BOUND_RE.match(cleaned)
    if match is None:
        return None

    keyword = (match.group("lead") or match.group("trail")).upper()
    raw_value = match.group("lead_value") or match.group("value")
    value = parse_override_mm(raw_value)
    if value is None:
        return None
    return value, _BOUND_COMPARATOR[keyword]


def _dimension_tier(dim: DimensionInfo, config: RuleConfig) -> str:
    """Which rounding grid applies to this dimension.

    Keyed on the Revit dimension *type* name. The PDF pipeline keyed the
    same decision on the CAD layer, and PLANNING.md §4 names
    dimension-style alongside it as a classification source. §4's other
    proposed source — automatic promotion for any dimension feeding a
    setout reconstruction — has nothing to hook onto here yet.
    """
    if dim.type_name and dim.type_name in config.setout_critical_type_names:
        return "setout_critical"
    return "default"


def _rounding_tolerance_mm(tier: str, config: RuleConfig) -> float:
    grid = (
        config.rounding_grid_setout_critical_mm
        if tier == "setout_critical"
        else config.rounding_grid_default_mm
    )
    return grid / 2.0 + config.measurement_epsilon_mm


def _segment_label(dim: DimensionInfo, index: int) -> str:
    """Name a segment within its dimension.

    A Revit dimension chain is one element with many segments, so the
    element id alone does not say which number is wrong. Pointing at the
    element still selects the right thing to click; this says where to
    look once it is selected.
    """
    if len(dim.segments) <= 1:
        return "Dimension"
    return "Segment {0} of {1} in a dimension chain".format(index + 1, len(dim.segments))


# How many distinct unparseable override forms to name in the coverage
# Issue. Enough to recognise a convention, not enough to bury it.
_MAX_LISTED_FORMS = 10


@register("revit.dimension_override_consistency")
def check_dimension_override_consistency(
    model: RevitModel, config: RuleConfig
) -> List[Issue]:
    """Compare a typed-over dimension value against what the model measures.

    Overriding is legitimate — see the module docstring on why curved
    bridge geometry makes it necessary — so the test is whether the
    difference is explainable as rounding to a sensible grid
    (`grid/2 + epsilon`), not whether the two values are equal. Anything
    larger is a typo, a stale override left behind by a model change, or
    a deliberate assertion that the model is wrong. All three are worth
    a look and the file cannot tell them apart.

    Limit-style overrides (`500 MIN.`) are checked too, but against the
    limit rather than the grid — see `_bound_issue` for why those are a
    different kind of claim.

    Always emits one coverage Issue saying how much was actually
    checkable. That is not padding: on a real second client this rule's
    DXF ancestor was **structurally inert** — 4.5% of dimensions
    overridden, none of them numeric — and reported zero findings, which
    reads as "clean" but meant "nothing was in scope".
    """
    issues: List[Issue] = []
    by_view = model.dimensions_by_view()

    segments_seen = 0
    overridden = 0
    checked = 0
    bounds_checked = 0
    unparsed_forms: Dict[str, int] = {}

    for view in views_in_scope(model, config):
        for dim in by_view.get(view.element_id, []):
            provenance = classify_dimension(dim)

            for index, segment in enumerate(dim.segments):
                segments_seen += 1
                if not segment.is_overridden:
                    continue
                overridden += 1

                stated_mm = parse_override_mm(segment.value_override)
                bound = (
                    None
                    if stated_mm is not None
                    else parse_override_bound(segment.value_override)
                )
                if stated_mm is None and bound is None:
                    form = (segment.value_override or "").strip()
                    unparsed_forms[form] = unparsed_forms.get(form, 0) + 1
                    continue

                if segment.value_mm is None:
                    # Revit reports no value for some spot dimension
                    # types. Nothing to compare against; not an error.
                    continue

                if bound is not None:
                    checked += 1
                    bounds_checked += 1
                    issue = _bound_issue(dim, index, view, segment, bound, provenance, config)
                    if issue is not None:
                        issues.append(issue)
                    continue

                checked += 1
                delta = stated_mm - segment.value_mm
                tier = _dimension_tier(dim, config)
                tolerance = _rounding_tolerance_mm(tier, config)
                if abs(delta) <= tolerance:
                    continue

                issues.append(
                    Issue(
                        rule_id="revit.dimension_override_consistency",
                        category="geometry",
                        description=(
                            "{0} in {1} is typed as {2:g}mm but measures "
                            "{3:.1f}mm ({4:+.1f}mm, more than rounding to the "
                            "{5} grid explains: ±{6:.1f}mm)."
                        ).format(
                            _segment_label(dim, index),
                            _describe_view(view),
                            stated_mm,
                            segment.value_mm,
                            delta,
                            tier.replace("_", " "),
                            tolerance,
                        ),
                        severity="high",
                        element_id=dim.element_id,
                        view_id=dim.view_id,
                        view_name=view.name,
                        sheet_no=view.sheet_no,
                        suggested_fix={
                            "stated_mm": stated_mm,
                            "measured_mm": round(segment.value_mm, 3),
                            "delta_mm": round(delta, 3),
                            "tolerance_mm": round(tolerance, 3),
                            "tier": tier,
                            # What the measured value actually is. On a
                            # DRAFTED dimension it is the length of a
                            # detail line, not of model geometry — so
                            # agreement here means the drafter agrees
                            # with their own linework, which is not the
                            # same reassurance.
                            "provenance": provenance,
                            "segment": index + 1,
                            "segments": len(dim.segments),
                        },
                    )
                )

    issues.append(
        _override_coverage_issue(
            segments_seen, overridden, checked, bounds_checked, unparsed_forms
        )
    )
    return issues


def _bound_issue(
    dim: DimensionInfo,
    index: int,
    view: ViewInfo,
    segment,
    bound: Tuple[float, str],
    provenance: str,
    config: RuleConfig,
) -> Optional[Issue]:
    """Check a measured value against a stated MIN/MAX limit.

    **The rounding grid deliberately does not apply here.** An exact
    override is a rounded restatement of a measurement, so the grid is
    exactly the right slack. `500 MIN.` is not a restatement of anything
    — it is a limit the built work has to respect, and allowing 2.5mm of
    "rounding" below a stated minimum would be inventing tolerance the
    drawing does not offer. The only slack applied is
    `measurement_epsilon_mm`, for noise in the measurement itself.
    """
    limit, comparator = bound
    measured = segment.value_mm
    epsilon = config.measurement_epsilon_mm

    if comparator == ">=":
        violated = measured < limit - epsilon
        wording = "at least"
    else:
        violated = measured > limit + epsilon
        wording = "at most"

    if not violated:
        return None

    return Issue(
        rule_id="revit.dimension_override_consistency",
        category="geometry",
        description=(
            "{0} in {1} is annotated as {2} {3:g}mm, but the model measures "
            "{4:.1f}mm — the stated limit is not met."
        ).format(
            _segment_label(dim, index),
            _describe_view(view),
            wording,
            limit,
            measured,
        ),
        severity="high",
        element_id=dim.element_id,
        view_id=dim.view_id,
        view_name=view.name,
        sheet_no=view.sheet_no,
        suggested_fix={
            "stated_limit_mm": limit,
            "comparator": comparator,
            "measured_mm": round(measured, 3),
            "epsilon_mm": epsilon,
            "provenance": provenance,
            "segment": index + 1,
            "segments": len(dim.segments),
        },
    )


def _override_coverage_issue(
    segments_seen: int,
    overridden: int,
    checked: int,
    bounds_checked: int,
    unparsed_forms: Dict[str, int],
) -> Issue:
    """State how much of the model this rule could actually check.

    Reported unconditionally, because the number that matters is the one
    a finding count cannot show: a run with no findings and four checked
    segments out of nine thousand has told you almost nothing, and looks
    identical to a clean model.
    """
    if segments_seen == 0:
        detail = "No dimensions were found in any view in scope."
    else:
        detail = (
            "{0} of {1} dimension segments carry a typed override, and {2} of "
            "those were compared against the model."
        ).format(overridden, segments_seen, checked)
        if bounds_checked:
            detail += (
                " {0} of those stated a MIN/MAX limit rather than an exact "
                "value, and were checked against the limit."
            ).format(bounds_checked)

    if unparsed_forms:
        ordered = sorted(unparsed_forms.items(), key=lambda kv: (-kv[1], kv[0]))
        listed = ", ".join(
            "{0!r} x{1}".format(form, count) for form, count in ordered[:_MAX_LISTED_FORMS]
        )
        remainder = len(ordered) - min(len(ordered), _MAX_LISTED_FORMS)
        if remainder:
            listed += " (+{0} more distinct)".format(remainder)
        detail += (
            " {0} override(s) were not a number and were skipped rather than "
            "guessed at: {1}."
        ).format(sum(unparsed_forms.values()), listed)

    if checked == 0:
        detail += (
            " Nothing was compared, so this rule finding nothing means it had "
            "nothing to check — not that the dimensions are right."
        )

    return Issue(
        rule_id="revit.dimension_override_consistency",
        category="coverage",
        description=detail,
        severity="low",
        suggested_fix={
            "segments": segments_seen,
            "overridden": overridden,
            "checked": checked,
            "bounds": bounds_checked,
            "unparsed": sum(unparsed_forms.values()),
        },
    )
