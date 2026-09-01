"""Rule catalog — `(RevitModel, RuleConfig) -> [Issue]`.

Same shape as the parked `pdfchecker.checks.catalog`
(ARCHIVE-pdf-dwg.md), and kept for the same reason
CLAUDE.md gives: "New drafting rules go in the rule catalog, not
hardcoded into pipeline logic", so a project-specific check is a config
change rather than a code change. With two rules that is mild overkill;
with the toolbar this is meant to grow into, it is the difference
between adding a button and rewiring one.

One inherited fix is worth not losing. `enabled_rule_ids` defaults to
`None` — meaning "every rule in the catalog", resolved at *run* time —
rather than to a snapshot of the catalog taken when the config was
built. The snapshot version was a real bug (2026-08-17 backend review):
rule registration is an import side-effect, so a config constructed
before a rule module had been imported excluded that module's rules
forever, silently, with import order deciding behaviour. That mistake
cost a whole check scope running zero rules and looking clean while
doing it.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Callable, Dict, List, Optional, Set

from revitcheck.ir import RevitModel
from revitcheck.issue import Issue

RuleFunc = Callable[[RevitModel, "RuleConfig"], List[Issue]]

_CATALOG: Dict[str, RuleFunc] = {}


def register(rule_id: str):
    """Decorator: adds a rule to the catalog under `rule_id`."""

    def decorator(func: RuleFunc) -> RuleFunc:
        if rule_id in _CATALOG:
            raise ValueError("duplicate rule_id: {0}".format(rule_id))
        _CATALOG[rule_id] = func
        return func

    return decorator


def all_rule_ids() -> List[str]:
    return sorted(_CATALOG.keys())


@dataclass
class RuleConfig:
    """Active rules plus their parameters — the config-not-code object.

    Tolerances and scoping live here rather than as constants in a rule,
    per CLAUDE.md: "Tolerances (drafting vs. survey) must be
    configurable, never hardcoded constants."
    """

    enabled_rule_ids: Optional[Set[str]] = None

    # Only check views that are actually placed on a sheet. A dimension
    # in an unplaced working view is not issued to anyone, and flagging
    # it is how a check earns a reputation for noise. Set False to sweep
    # the whole document, including in-progress views.
    sheeted_views_only: bool = True

    # Skip a Drafting View's dimensions entirely unless the view is
    # linked to a section cut through the model (a "Reference other
    # view" callout, rather than a free-standing standard detail — see
    # `ir.ViewInfo.linked_to_model_section`). A standard detail was
    # never going to track the model regardless of how many of its
    # dimensions get individually flagged, so processing it at all is
    # pure volume with no decision it changes; a *linked* one still gets
    # checked in full, because it is standing in for a section and
    # carries that drift risk. Set False to check every drafting view
    # regardless, e.g. for an audit that wants full coverage on record.
    skip_unlinked_drafting_views: bool = True

    # Skip every view on a sheet whose title contains one of these
    # words (case-insensitive substring match against `SheetInfo.name`).
    # Confirmed by the user, 2026-08-22, against the real T2DPAA
    # capture: reinforcement/detailing sheets dimension bar spacing and
    # cover to static linework as a matter of normal drafting practice
    # — never intended to be live, and typically overridden with a bar
    # mark or spacing callout ("A1", "N16-200") rather than a number, so
    # `revit.dimension_override_consistency` was already silently
    # skipping most of them as unparseable. What `revit.dimension_
    # provenance` couldn't tell was that this is expected rather than a
    # drift risk — flagging it "high" is technically correct (it *does*
    # measure a detail line) and practically wrong (it was never
    # setout). Default is one word because that's what was confirmed;
    # add more once a second convention turns up rather than guessing
    # ahead of one. Empty list checks every sheet regardless.
    excluded_sheet_title_keywords: List[str] = field(
        default_factory=lambda: ["reinforcement"]
    )

    # Severity for a dimension that measures detail linework, split by
    # the kind of view it sits in — because the two cases mean genuinely
    # different things. In a section or plan the view *could* have been
    # live and someone chose otherwise, which is the drift risk. In a
    # drafting view there is no model behind the view at all, so 2D is
    # the only option there is; that may still be the problem, but it is
    # not the same finding and must not read like one.
    drafted_in_model_view_severity: str = "high"
    drafted_in_drafting_view_severity: str = "low"

    # A dimension mixing model geometry and detail linework across its
    # own witness points. Originally assumed rare and usually accidental;
    # corrected 2026-09-02 by real data (drg-2873061 section 1) — a
    # deliberate "extent of barrier" style dimension is exactly this
    # shape (a real modeled anchor at one end, a drafted witness point
    # marking the extent at the other), not an accident. That correction
    # lives in the override-suppression rule
    # (`_overridden_without_stated_limit`), not here — an *unoverridden*
    # MIXED dimension is still worth this severity.
    mixed_provenance_severity: str = "medium"

    # --- overridden-dimension tolerance (revit.dimension_override_consistency)
    #
    # A drafter overriding a dimension's text is asserting the intended
    # value over the measured one, and doing so is legitimate: bridge
    # geometry is curved, sections cannot always be cut perpendicular,
    # and the model's own measurement lands a few mm off a buildable
    # number. So the test is not "do these agree" but "is the difference
    # explainable as rounding to a sensible grid" — `grid/2 + epsilon`.
    # A flat delta cannot do that job: loose enough to allow real
    # rounding also allows a stale override, and tight enough to catch a
    # stale override flags every rounded dimension on the sheet.
    #
    # **These three figures are inherited, not calibrated.** They come
    # from the parked PDF/DWG pipeline (PLANNING.md §5's rounding-grid
    # design), where §5 itself calls the setout_critical figure "a
    # placeholder — confirm the real figure against samples". No Revit
    # model has been measured against them yet. The coverage Issue the
    # rule emits is what will make the first real run say how much it
    # actually checked.
    rounding_grid_default_mm: float = 5.0
    rounding_grid_setout_critical_mm: float = 1.0
    measurement_epsilon_mm: float = 0.5

    # Dimension *types* (Revit's `DimensionType.Name`) whose values are
    # setout-critical and get the tighter grid. Revit's dimension style
    # is the direct analogue of the CAD layer/dimstyle the PDF pipeline
    # keyed on, and PLANNING.md §4 names dimension-style as a
    # classification source explicitly. Empty by default: an unlisted
    # type gets the default grid rather than a guess, and no client's
    # naming is assumed.
    setout_critical_type_names: List[str] = field(default_factory=list)

    params: Dict[str, dict] = field(default_factory=dict)

    def resolved_rule_ids(self) -> Set[str]:
        if self.enabled_rule_ids is None:
            return set(all_rule_ids())
        return set(self.enabled_rule_ids)

    def is_enabled(self, rule_id: str) -> bool:
        return rule_id in self.resolved_rule_ids()


def run_checks(
    model: RevitModel, config: Optional[RuleConfig] = None
) -> List[Issue]:
    """Run every enabled rule and return the combined issues.

    Rules are isolated from one another: a rule that raises produces a
    coverage Issue and the run continues. The PDF pipeline deliberately
    did *not* do this (`run_checks` there fails the whole run if one
    rule raises, flagged as a known gap in `session.py`'s docstring),
    and there the cost was a failed CLI run someone could retry. Here
    the same failure would take down a toolbar button mid-review with a
    stack trace, so it is worth getting right in the new code rather
    than inheriting the gap.
    """
    config = config or RuleConfig()
    enabled = config.resolved_rule_ids()
    issues: List[Issue] = []

    for rule_id in sorted(enabled):
        func = _CATALOG.get(rule_id)
        if func is None:
            # Not an error: PLANNING.md §4's project rule config accepts
            # ids the live catalog does not carry, so a config can name
            # a rule that has not been built yet.
            continue
        try:
            issues.extend(func(model, config))
        except Exception as exc:  # noqa: BLE001 - deliberate, see docstring
            issues.append(
                Issue(
                    rule_id=rule_id,
                    category="coverage",
                    description=(
                        "Rule '{0}' failed to run ({1}: {2}) — anything it "
                        "would have found is missing from these results."
                    ).format(rule_id, type(exc).__name__, exc),
                    severity="high",
                )
            )

    return issues
