"""Session-level rule config loader — PLANNING.md §9 build-order step 5,
§4's "Project rule configuration — consolidated schema" (layer 3, the
"session-level config" that travels with each upload — §2's "Stateless by
design": no persisted project to attach it to, so this is re-supplied
fresh each session rather than admin-edited over time).

`load_session_config(path)` reads a YAML (or JSON — `yaml.safe_load`
parses both) rules file into a `RuleConfig` — the same object every rule
function already reads from (`catalog.py`) — plus the `check_scope`
selection (§2) that decides whether geometry rules run at all.

**Deliberately scoped to the schema keys that have a real mechanism
behind them today.** §4's schema is written as "a representative session
config file, showing several of the pieces designed above coexisting" —
not a promise that every key already does something. Four real gaps
exist between what §4 sketches and what's actually wired into extraction/
the rule catalog, and this loader is honest about all four rather than
silently accepting-and-ignoring them:

1. **`extends_standard`** (§4 layer 2, firm-level named standard bundles)
   — nothing in this codebase stores or looks up a named bundle yet; only
   the firm glossary persists so far (§2). Accepted, reported as a
   warning, not applied — every session starts from `RuleConfig()`'s own
   built-in defaults instead.
2. **`title_block.custom_fields` / `reference_values`** (§4's fourth
   rule primitive, value-correctness against a reference) — needs
   `extraction/titleblock.py`'s field list to be config-driven at
   *ingest* time, but `ingest_pdf(path)` doesn't take a config argument
   yet (see that module's docstring, "a project config can add more
   without touching extraction code" — the config plumbing to actually
   reach it isn't built). Accepted, warned, not applied.
3. **`revision_schedule.column_mapping` / `require_cloud_for_every_row`**
   — same ingest-time gap for the column mapping
   (`extraction/tables.py`'s `DEFAULT_REVISION_COLUMNS` is fixed), and no
   rule implements PLANNING.md §4 point 4 ("schedule row with no
   matching cloud") at all yet. Accepted, warned, not applied.
4. **`rules[].params`** on a rule that isn't in the live catalog
   (`catalog.all_rule_ids()`) — §4's `regex_text_check` /
   `value_correctness_check` primitives, or any `spec.*` id §6's
   extraction would auto-register — aren't implemented as rule functions
   yet. The `id`/`enabled` fields still work (a config can reference a
   rule that doesn't exist yet without the whole file failing to load,
   the same "config outlives code" property §6 depends on), but the
   entry has no effect and says so via a warning.

Everything else — the `rules:` enable/disable list, `check_scope`,
`glossary.session_additions`, `title_block.required_fields`, and every
tolerance under `tolerances:` (including the setout-reconstruction and
IFC knobs added after §4's schema was first written, extended here rather
than left Python-only) — maps onto a real, already-consumed `RuleConfig`
field or `enabled_rule_ids` entry.

Structural problems in the file itself (malformed YAML, a non-mapping
top level, an invalid `check_scope`, a `rules` entry with no `id`, a
tolerance value that doesn't parse as a length) raise `ValueError` —
those would silently misconfigure an entire check run if swallowed.
Everything else — an unrecognized key, a reference to a not-yet-built
mechanism — is reported in `LoadedSessionConfig.warnings`, never silently
dropped, same "confidence, not silent" principle this codebase already
applies to geometry reconstruction and the cross-sheet reference graph.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path

import yaml

from pdfchecker.checks.catalog import RuleConfig, all_rule_ids

VALID_CHECK_SCOPES = ("drafting_only", "drafting_and_geometry")

# §4's schema keys with no extraction/rule mechanism behind them yet —
# see the module docstring's numbered list (points 2 and 3).
_UNWIRED_TITLE_BLOCK_KEYS = ("custom_fields", "reference_values")
_UNWIRED_REVISION_SCHEDULE_KEYS = ("column_mapping", "require_cloud_for_every_row")

_KNOWN_TOP_LEVEL_KEYS = {
    "extends_standard",
    "glossary",
    "check_scope",
    "title_block",
    "revision_schedule",
    "tolerances",
    "rules",
}

_LENGTH_UNITS = (("mm", 1.0), ("cm", 10.0), ("m", 1000.0))  # order matters: check "mm"/"cm" before bare "m"


@dataclass
class LoadedSessionConfig:
    """What `load_session_config()` hands back: the `RuleConfig` every
    rule function already consumes, the drafting-vs-geometry
    `check_scope` (§2), and a `warnings` list for anything the file asked
    for that this codebase doesn't implement yet."""

    rule_config: RuleConfig
    check_scope: str
    warnings: list[str] = field(default_factory=list)


def _length_mm(value, field_name: str) -> float:
    """Parses a tolerance value into millimetres. Accepts a bare number
    (already mm, matching RuleConfig's own units) or a string with a
    unit suffix (`"5mm"`, `"0.01m"`, `"1cm"`) — §4's schema example
    writes tolerances as unit-suffixed strings throughout."""

    if isinstance(value, (int, float)) and not isinstance(value, bool):
        return float(value)
    text = str(value).strip().lower()
    for suffix, factor in _LENGTH_UNITS:
        if text.endswith(suffix) and text[: -len(suffix)].strip():
            try:
                return float(text[: -len(suffix)].strip()) * factor
            except ValueError:
                break
    raise ValueError(f"{field_name}: can't parse length '{value}' (expected e.g. '5mm', '0.01m', or a bare mm number)")


def load_session_config(path: str | Path) -> LoadedSessionConfig:
    """Parses a session's uploaded rules file into a `RuleConfig` +
    `check_scope`. Starts from `RuleConfig()`'s own defaults (the closest
    thing this codebase has to a firm standard bundle right now, since
    `extends_standard` isn't wired — see module docstring point 1) and
    layers the file's overrides on top."""

    p = Path(path)
    raw = yaml.safe_load(p.read_text()) or {}
    if not isinstance(raw, dict):
        raise ValueError(f"{path}: top-level config must be a mapping, got {type(raw).__name__}")

    warnings: list[str] = []
    config = RuleConfig()

    for key in raw:
        if key not in _KNOWN_TOP_LEVEL_KEYS:
            warnings.append(f"unrecognized top-level config key '{key}' — ignored")

    if "extends_standard" in raw:
        warnings.append(
            f"extends_standard: '{raw['extends_standard']}' accepted but ignored — named firm-level "
            "standard bundles (PLANNING.md §4 layer 2) aren't stored/looked up anywhere in this "
            "codebase yet; starting from RuleConfig()'s built-in defaults instead"
        )

    # --- glossary ---
    glossary = raw.get("glossary") or {}
    if "firm_glossary_path" in glossary:
        warnings.append(
            "glossary.firm_glossary_path is firm-level (§4 layer 2, persists across sessions), not "
            "session-settable — ignored"
        )
    if "project_glossary_path" in glossary:
        config.project_glossary_path = glossary["project_glossary_path"]
    session_additions = glossary.get("session_additions") or []
    config.session_glossary_words = {str(w).strip().lower() for w in session_additions if str(w).strip()}

    # --- check_scope (§2) ---
    check_scope = raw.get("check_scope", "drafting_and_geometry")
    if check_scope not in VALID_CHECK_SCOPES:
        raise ValueError(f"check_scope: '{check_scope}' must be one of {VALID_CHECK_SCOPES}")

    # --- rules: enable/disable against the live catalog ---
    catalog_ids = set(all_rule_ids())
    enabled = set(catalog_ids)
    for entry in raw.get("rules") or []:
        if "id" not in entry:
            raise ValueError(f"rules: entry {entry!r} is missing required 'id'")
        rule_id = entry["id"]
        if entry.get("enabled", True):
            enabled.add(rule_id)
        else:
            enabled.discard(rule_id)
        if rule_id not in catalog_ids:
            warnings.append(
                f"rules: '{rule_id}' isn't in the current catalog (checks/catalog.py all_rule_ids()) — "
                "accepted from config but has no implementation to run yet"
            )
        elif "params" in entry:
            warnings.append(
                f"rules: '{rule_id}' has 'params' in config, but this rule doesn't read per-rule "
                "params yet — tolerances live in the top-level tolerances: section instead"
            )
    if check_scope == "drafting_only":
        enabled = {rule_id for rule_id in enabled if not rule_id.startswith("geometry.")}
    elif not any(rule_id.startswith("geometry.") for rule_id in catalog_ids):
        # Defence in depth against the real 2026-08-17 bug this loader
        # sat downstream of (backend review finding 1.3): rule
        # registration is an import side-effect, so a catalog missing
        # `checks/geometry.py` entirely made `drafting_and_geometry`
        # silently equivalent to `drafting_only`. `checks/__init__.py`
        # now imports it, so this should be unreachable — but a scope
        # that asks for geometry and gets none is exactly the kind of
        # thing this codebase reports rather than swallows, and a
        # warning here costs nothing if the import list ever drifts again.
        warnings.append(
            "check_scope: 'drafting_and_geometry' was requested, but no geometry.* rules are "
            "registered in the catalog — geometry checks will produce nothing. This means a rule "
            "module failed to import (see checks/__init__.py), not that the drawings are clean."
        )
    config.enabled_rule_ids = enabled

    # --- title_block ---
    tb = raw.get("title_block") or {}
    if "required_fields" in tb:
        config.required_title_block_fields = list(tb["required_fields"])
    for key in _UNWIRED_TITLE_BLOCK_KEYS:
        if key in tb:
            warnings.append(
                f"title_block.{key} accepted but ignored — extraction/titleblock.py's field list is "
                "fixed at ingest time, before any session config is available; ingest_pdf() doesn't "
                "take a config argument yet, so there's no way to reach this yet (§4's 'extraction "
                "gap first' note)"
            )

    # --- revision_schedule ---
    rs = raw.get("revision_schedule") or {}
    for key in _UNWIRED_REVISION_SCHEDULE_KEYS:
        if key in rs:
            warnings.append(
                f"revision_schedule.{key} accepted but ignored — extraction/tables.py's "
                "DEFAULT_REVISION_COLUMNS is fixed at ingest time (same gap as title_block above), and "
                "no rule implements 'flag a schedule row with no matching cloud' yet (PLANNING.md §4 "
                "point 4)"
            )

    # --- tolerances ---
    tol = raw.get("tolerances") or {}

    rounding_grid = tol.get("rounding_grid") or {}
    if "default" in rounding_grid:
        config.rounding_grid_default_mm = _length_mm(rounding_grid["default"], "tolerances.rounding_grid.default")
    if "setout_critical" in rounding_grid:
        config.rounding_grid_setout_critical_mm = _length_mm(
            rounding_grid["setout_critical"], "tolerances.rounding_grid.setout_critical"
        )
    if "measurement_epsilon" in tol:
        config.measurement_epsilon_mm = _length_mm(tol["measurement_epsilon"], "tolerances.measurement_epsilon")
    overrides = tol.get("setout_critical_overrides") or []
    if overrides:
        config.setout_critical_layers = [o["layer"] for o in overrides]

    survey = tol.get("survey_tolerance") or {}
    if "base_tolerance" in survey:
        config.survey_tolerance_mm = _length_mm(
            survey["base_tolerance"], "tolerances.survey_tolerance.base_tolerance"
        )
    if "per_hop_allowance" in survey:
        warnings.append(
            "tolerances.survey_tolerance.per_hop_allowance accepted but ignored — PLANNING.md §5's "
            "base + per_hop×√hops scaling isn't built (no real multi-hop-from-different-origins case "
            "has surfaced yet to calibrate it against); survey_tolerance_mm is applied flat instead"
        )

    # Stage 3 additions (2026-08-11 onward) not in §4's original example —
    # extended here since these are real, already-consumed RuleConfig
    # tolerances that were otherwise only settable from Python.
    setout = tol.get("setout_reconstruction") or {}
    if "setout_point_insert_substring" in setout:
        config.setout_point_insert_substring = str(setout["setout_point_insert_substring"])
    for key in (
        "chain_link_tolerance_m",
        "origin_pair_max_distance_m",
        "bearing_pair_max_distance_m",
        "pile_label_pair_max_distance_m",
        "chain_continuation_max_gap_m",
    ):
        if key in setout:
            setattr(config, key, float(setout[key]))

    ifc = tol.get("ifc") or {}
    if "pile_footprint_max_m" in ifc:
        config.ifc_pile_footprint_max_m = float(ifc["pile_footprint_max_m"])
    if "pile_aspect_ratio_min" in ifc:
        config.ifc_pile_aspect_ratio_min = float(ifc["pile_aspect_ratio_min"])
    if "match_max_distance_m" in ifc:
        config.ifc_match_max_distance_m = float(ifc["match_max_distance_m"])
    if "setout_tolerance_mm" in ifc:
        config.ifc_setout_tolerance_mm = _length_mm(ifc["setout_tolerance_mm"], "tolerances.ifc.setout_tolerance_mm")
    # geometry.ifc_superstructure_coverage's shape heuristics (2026-08-15)
    if "deck_footprint_min_m" in ifc:
        config.ifc_deck_footprint_min_m = float(ifc["deck_footprint_min_m"])
    if "deck_aspect_ratio_max" in ifc:
        config.ifc_deck_aspect_ratio_max = float(ifc["deck_aspect_ratio_max"])
    if "beam_footprint_min_m" in ifc:
        config.ifc_beam_footprint_min_m = float(ifc["beam_footprint_min_m"])
    if "beam_aspect_ratio_min" in ifc:
        config.ifc_beam_aspect_ratio_min = float(ifc["beam_aspect_ratio_min"])
    if "beam_aspect_ratio_max" in ifc:
        config.ifc_beam_aspect_ratio_max = float(ifc["beam_aspect_ratio_max"])

    return LoadedSessionConfig(rule_config=config, check_scope=check_scope, warnings=warnings)
