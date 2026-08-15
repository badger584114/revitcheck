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
    # PLANNING.md §4 "Custom dictionary / glossary management" +
    # `glossary.session_additions` in §4's consolidated schema — terms
    # added inline in a session's uploaded rules file rather than a
    # separate JSON file. Layered on top of the two file-backed tiers
    # above, session-only (never written back to either JSON file), by
    # checks/session_config.py's loader.
    session_glossary_words: set[str] = field(default_factory=set)

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

    # PLANNING.md §5b "Structure reconstruction from setout data" —
    # extraction/setout_reconstruction.py's bearing + dimension-chain
    # walk, consumed by checks/geometry.py's geometry.setout_reconstruction
    # rule. Defaults are calibrated against the real sample (see that
    # module's docstring); the two "*_insert_substring" values are a DXF
    # block-naming convention this firm's export happens to use, not a
    # DXF standard, so they're config, not code, per CLAUDE.md.
    setout_point_insert_substring: str = "SETOUT POINT"
    chain_link_tolerance_m: float = 0.01
    origin_pair_max_distance_m: float = 5.0
    bearing_pair_max_distance_m: float = 30.0
    pile_label_pair_max_distance_m: float = 5.0
    # A branch-less chain's own sign is unreliable (walk_chain's docstring)
    # — how far away a branch-having neighbor chain can be and still donate
    # its already-oriented span, when the two are a real local-space
    # continuation of each other. Real BR08 gaps between a main abutment
    # chain and its sub-group continuation are ~2m; 10m default leaves
    # headroom without reaching across to an unrelated structure.
    chain_continuation_max_gap_m: float = 10.0
    # Flat for now, not PLANNING.md §5's base + per_hop×√hops scaling —
    # see extraction/setout_reconstruction.py's docstring for why: this
    # MVP has no real multi-hop-from-different-origins case to calibrate
    # that formula against yet.
    survey_tolerance_mm: float = 10.0

    # PLANNING.md §5's proposed third geometry source (IFC), added
    # 2026-08-12 — checks/geometry.py's geometry.ifc_setout_consistency.
    # `ifc_pile_footprint_max_m`/`ifc_pile_aspect_ratio_min` are the
    # bounding-box shape heuristic ("small footprint, tall") that
    # identifies pile-like IFC elements without reading Name/
    # PredefinedType — confirmed on the real sample (28 real piles,
    # 0 false positives/negatives against a Name-text search) but this
    # firm's actual pile geometry (0.75m x 0.75m x 10.55m), so it's
    # config, not a hardcoded assumption another project's pile
    # dimensions would have to match exactly.
    ifc_pile_footprint_max_m: float = 2.0
    ifc_pile_aspect_ratio_min: float = 3.0
    # How far a reconstructed point may be from its nearest candidate IFC
    # element before treating it as "no IFC counterpart found" rather
    # than "found, but too far off" — distinct from
    # ifc_setout_tolerance_mm (the actual pass/fail delta) so a genuinely
    # missing/unmodeled pile doesn't get reported with a nonsense delta
    # to whatever unrelated element happened to be nearest.
    ifc_match_max_distance_m: float = 2.0
    ifc_setout_tolerance_mm: float = 10.0

    # PLANNING.md §5's IFC subsection, "non-pile superstructure" gap —
    # geometry.ifc_superstructure_coverage (added 2026-08-15). Two more
    # schema-general shape heuristics, same "footprint + aspect ratio"
    # shape as ifc_pile_footprint_max_m/ifc_pile_aspect_ratio_min above,
    # calibrated against the same real BR06 IFC model — see
    # checks/geometry.py's _is_thin_horizontal_plate/_is_elongated_beam
    # docstrings for the real dx/dy/dz figures found for deck-slab pours
    # (footprint 7.56-23.07m, ratio 0.0106-0.0402) vs. abutment beam/
    # headstock elements (footprint 10.51-13.32m, ratio 0.100-0.124) —
    # confirmed on one real project only, not yet cross-checked against
    # BR08 (that file's IFC model is far larger and ifcopenshell.geom is
    # genuinely slow per complex element — see checks/geometry.py).
    ifc_deck_footprint_min_m: float = 5.0
    ifc_deck_aspect_ratio_max: float = 0.06
    ifc_beam_footprint_min_m: float = 5.0
    ifc_beam_aspect_ratio_min: float = 0.06
    ifc_beam_aspect_ratio_max: float = 0.5

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
