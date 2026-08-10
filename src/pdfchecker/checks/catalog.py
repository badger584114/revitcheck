"""Rule catalog — PLANNING.md §4: "each rule is a small function
(IR, config) -> [Issue]. Rules are registered in a catalog; a project's
active rule set is just a list of rule IDs + parameters." This is the
minimal version of that: a decorator-based registry plus a RuleConfig
carrying which rules are active and their parameters. The full YAML
project-config schema (§4's "Project rule configuration — consolidated
schema") is a later step (PLANNING.md §9 step 5) — this is deliberately
smaller, just enough to run Stage 2's rules without hardcoding which ones
execute into the engine itself.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Callable

from pdfchecker.checks.issue import Issue
from pdfchecker.ir import Project

RuleFunc = Callable[[Project, "RuleConfig"], list]

_CATALOG: dict[str, RuleFunc] = {}


def register(rule_id: str):
    """Decorator: adds a rule function to the catalog under `rule_id`.
    A rule function's signature is always (project, config) -> [Issue]."""

    def decorator(func: RuleFunc) -> RuleFunc:
        if rule_id in _CATALOG:
            raise ValueError(f"duplicate rule_id: {rule_id}")
        _CATALOG[rule_id] = func
        return func

    return decorator


def all_rule_ids() -> list[str]:
    return sorted(_CATALOG.keys())


@dataclass
class RuleConfig:
    """A project's active rule set + parameters — the config-not-code
    object every rule reads from. Deliberately a plain dataclass with a
    generic `params` bag for now rather than the full schema in
    PLANNING.md §4; fields get promoted out of `params` as more rules
    need strongly-typed access to them."""

    enabled_rule_ids: set[str] = field(default_factory=lambda: set(all_rule_ids()))
    required_title_block_fields: list[str] = field(
        default_factory=lambda: ["drawing_no", "sheet_no", "amend_no"]
    )
    project_glossary_path: str | None = None
    firm_glossary_path: str | None = None

    # PLANNING.md §5 "Tolerance configuration" — drawn-vs-stated dimension
    # tolerance is a rounding-grid, not a flat delta (see checks/geometry.py).
    # Defaults are PLANNING.md's own placeholder figures, not confirmed
    # against a real chain of setout-critical dimensions yet — never
    # hardcoded into the check itself, per CLAUDE.md's tolerance rule.
    rounding_grid_default_mm: float = 5.0
    rounding_grid_setout_critical_mm: float = 1.0
    measurement_epsilon_mm: float = 0.5
    # Layer names treated as setout_critical (tighter tolerance) — a
    # project config mapping, per PLANNING.md §5. Automatic promotion for
    # any dimension that's an edge in the §5b reconstruction graph isn't
    # possible yet (§5b isn't built), so this list is the only
    # classification source for now — everything else gets the default tier.
    setout_critical_layers: list[str] = field(default_factory=list)

    def is_enabled(self, rule_id: str) -> bool:
        return rule_id in self.enabled_rule_ids


def run_checks(project: Project, config: RuleConfig) -> list[Issue]:
    """Runs every enabled rule against the project and returns the
    combined, flat Issue list — the engine doesn't care which category a
    rule belongs to, only whether it's enabled."""

    issues: list[Issue] = []
    for rule_id in all_rule_ids():
        if not config.is_enabled(rule_id):
            continue
        issues.extend(_CATALOG[rule_id](project, config))
    return issues
