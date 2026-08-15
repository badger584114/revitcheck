"""checks/session_config.py — PLANNING.md §9 build-order step 5: loading
a session's uploaded YAML/JSON rules file into a RuleConfig + check_scope.
Two kinds of test:

- Mechanics against synthetic YAML files (tmp_path) — every real,
  currently-wired schema key maps onto the right RuleConfig field, the
  four accepted-but-not-yet-wired schema keys (module docstring) warn
  rather than silently no-op or raise, and structural problems (missing
  `rules[].id`, invalid check_scope, non-mapping top level) do raise.
- Against the repo's own config/session_example.yaml, so that file stays
  honest as a real, loadable example rather than drifting out of sync
  with the loader.
"""

import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from pdfchecker.checks import geometry  # noqa: F401,E402 — registers geometry.* rule ids
from pdfchecker.checks.catalog import all_rule_ids  # noqa: E402
from pdfchecker.checks.session_config import load_session_config  # noqa: E402

REPO_ROOT = Path(__file__).resolve().parent.parent


def _write(tmp_path: Path, text: str) -> Path:
    p = tmp_path / "session.yaml"
    p.write_text(text)
    return p


def test_example_config_loads_clean_against_the_real_catalog():
    loaded = load_session_config(REPO_ROOT / "config" / "session_example.yaml")
    assert loaded.check_scope == "drafting_and_geometry"
    # The one warning it should produce: "regex_text_check" is a real §4
    # primitive that isn't implemented as a rule yet (by design — see the
    # file's own comment) — nothing else in that file should warn.
    assert len(loaded.warnings) == 1
    assert "regex_text_check" in loaded.warnings[0]


def test_defaults_when_file_is_minimal(tmp_path):
    loaded = load_session_config(_write(tmp_path, "check_scope: drafting_and_geometry\n"))
    assert loaded.rule_config.enabled_rule_ids == set(all_rule_ids())
    assert loaded.rule_config.rounding_grid_default_mm == 5.0
    assert loaded.warnings == []


def test_check_scope_drafting_only_strips_geometry_rules(tmp_path):
    loaded = load_session_config(_write(tmp_path, "check_scope: drafting_only\n"))
    assert not any(r.startswith("geometry.") for r in loaded.rule_config.enabled_rule_ids)
    # non-geometry rules stay enabled by default
    assert "spelling.en_gb" in loaded.rule_config.enabled_rule_ids


def test_check_scope_drafting_and_geometry_keeps_geometry_rules(tmp_path):
    loaded = load_session_config(_write(tmp_path, "check_scope: drafting_and_geometry\n"))
    assert "geometry.dimension_consistency" in loaded.rule_config.enabled_rule_ids


def test_invalid_check_scope_raises(tmp_path):
    with pytest.raises(ValueError, match="check_scope"):
        load_session_config(_write(tmp_path, "check_scope: nonsense\n"))


def test_rules_list_disables_and_enables(tmp_path):
    cfg = """
rules:
  - id: "spelling.en_gb"
    enabled: false
  - id: "title_block.required_fields_present"
    enabled: true
"""
    loaded = load_session_config(_write(tmp_path, cfg))
    assert "spelling.en_gb" not in loaded.rule_config.enabled_rule_ids
    assert "title_block.required_fields_present" in loaded.rule_config.enabled_rule_ids
    # everything not mentioned keeps the "enabled by default" behaviour
    assert "cross_sheet.reference_resolves" in loaded.rule_config.enabled_rule_ids


def test_rules_entry_missing_id_raises(tmp_path):
    with pytest.raises(ValueError, match="id"):
        load_session_config(_write(tmp_path, "rules:\n  - enabled: true\n"))


def test_unregistered_rule_id_warns_not_raises(tmp_path):
    cfg = """
rules:
  - id: "regex_text_check"
    enabled: true
    params: {pattern: "Grade 60"}
    source: "manual"
"""
    loaded = load_session_config(_write(tmp_path, cfg))
    assert any("regex_text_check" in w and "no implementation" in w for w in loaded.warnings)
    # doesn't get silently added as if it ran
    assert "regex_text_check" not in set(all_rule_ids())


def test_registered_rule_with_params_warns(tmp_path):
    cfg = """
rules:
  - id: "spelling.en_gb"
    enabled: true
    params: {some: "thing"}
"""
    loaded = load_session_config(_write(tmp_path, cfg))
    assert any("spelling.en_gb" in w and "params" in w for w in loaded.warnings)


def test_glossary_session_additions(tmp_path):
    cfg = """
glossary:
  session_additions: ["chainage", "T2DPAA", "  Woronora  "]
"""
    loaded = load_session_config(_write(tmp_path, cfg))
    assert loaded.rule_config.session_glossary_words == {"chainage", "t2dpaa", "woronora"}


def test_glossary_project_path_settable_firm_path_is_not(tmp_path):
    cfg = """
glossary:
  project_glossary_path: "some/uploaded/project_glossary.json"
  firm_glossary_path: "should/not/apply.json"
"""
    loaded = load_session_config(_write(tmp_path, cfg))
    assert loaded.rule_config.project_glossary_path == "some/uploaded/project_glossary.json"
    assert loaded.rule_config.firm_glossary_path is None
    assert any("firm_glossary_path" in w for w in loaded.warnings)


def test_title_block_required_fields(tmp_path):
    cfg = """
title_block:
  required_fields: ["drawing_no", "sheet_latitude", "sheet_longitude"]
"""
    loaded = load_session_config(_write(tmp_path, cfg))
    assert loaded.rule_config.required_title_block_fields == [
        "drawing_no",
        "sheet_latitude",
        "sheet_longitude",
    ]


def test_title_block_custom_fields_warns_not_applied(tmp_path):
    cfg = """
title_block:
  custom_fields:
    - name: "site_lat_long"
      label_match: "LAT/LONG"
"""
    loaded = load_session_config(_write(tmp_path, cfg))
    assert any("title_block.custom_fields" in w for w in loaded.warnings)


def test_revision_schedule_keys_warn_not_applied(tmp_path):
    cfg = """
revision_schedule:
  require_cloud_for_every_row: true
  column_mapping: {rev_id: "REV"}
"""
    loaded = load_session_config(_write(tmp_path, cfg))
    warned = " ".join(loaded.warnings)
    assert "revision_schedule.require_cloud_for_every_row" in warned
    assert "revision_schedule.column_mapping" in warned


def test_extends_standard_warns_not_applied(tmp_path):
    loaded = load_session_config(_write(tmp_path, 'extends_standard: "internal_firm_standard_v3"\n'))
    assert any("extends_standard" in w for w in loaded.warnings)


def test_unrecognized_top_level_key_warns(tmp_path):
    loaded = load_session_config(_write(tmp_path, "not_a_real_key: 1\n"))
    assert any("not_a_real_key" in w for w in loaded.warnings)


def test_top_level_must_be_a_mapping(tmp_path):
    with pytest.raises(ValueError, match="mapping"):
        load_session_config(_write(tmp_path, "- 1\n- 2\n"))


class TestTolerances:
    def test_rounding_grid_and_epsilon(self, tmp_path):
        cfg = """
tolerances:
  rounding_grid: {default: "5mm", setout_critical: "1mm"}
  measurement_epsilon: "0.5mm"
"""
        loaded = load_session_config(_write(tmp_path, cfg))
        c = loaded.rule_config
        assert c.rounding_grid_default_mm == 5.0
        assert c.rounding_grid_setout_critical_mm == 1.0
        assert c.measurement_epsilon_mm == 0.5

    def test_unit_conversion(self, tmp_path):
        cfg = """
tolerances:
  rounding_grid: {default: "0.5cm", setout_critical: "0.001m"}
"""
        loaded = load_session_config(_write(tmp_path, cfg))
        assert loaded.rule_config.rounding_grid_default_mm == pytest.approx(5.0)
        assert loaded.rule_config.rounding_grid_setout_critical_mm == pytest.approx(1.0)

    def test_bare_number_is_mm(self, tmp_path):
        cfg = """
tolerances:
  measurement_epsilon: 0.5
"""
        loaded = load_session_config(_write(tmp_path, cfg))
        assert loaded.rule_config.measurement_epsilon_mm == 0.5

    def test_unparseable_length_raises(self, tmp_path):
        cfg = """
tolerances:
  measurement_epsilon: "half a millimetre"
"""
        with pytest.raises(ValueError, match="measurement_epsilon"):
            load_session_config(_write(tmp_path, cfg))

    def test_setout_critical_overrides(self, tmp_path):
        cfg = """
tolerances:
  setout_critical_overrides:
    - layer: "C-BEARING"
    - layer: "C-PILE"
"""
        loaded = load_session_config(_write(tmp_path, cfg))
        assert loaded.rule_config.setout_critical_layers == ["C-BEARING", "C-PILE"]

    def test_survey_tolerance_base_applied_per_hop_warns(self, tmp_path):
        cfg = """
tolerances:
  survey_tolerance: {base_tolerance: "12mm", per_hop_allowance: "3mm"}
"""
        loaded = load_session_config(_write(tmp_path, cfg))
        assert loaded.rule_config.survey_tolerance_mm == 12.0
        assert any("per_hop_allowance" in w for w in loaded.warnings)

    def test_setout_reconstruction_knobs(self, tmp_path):
        cfg = """
tolerances:
  setout_reconstruction:
    setout_point_insert_substring: "SET OUT PT"
    chain_link_tolerance_m: 0.02
    origin_pair_max_distance_m: 3.0
    bearing_pair_max_distance_m: 20.0
    pile_label_pair_max_distance_m: 4.0
    chain_continuation_max_gap_m: 8.0
"""
        loaded = load_session_config(_write(tmp_path, cfg))
        c = loaded.rule_config
        assert c.setout_point_insert_substring == "SET OUT PT"
        assert c.chain_link_tolerance_m == 0.02
        assert c.origin_pair_max_distance_m == 3.0
        assert c.bearing_pair_max_distance_m == 20.0
        assert c.pile_label_pair_max_distance_m == 4.0
        assert c.chain_continuation_max_gap_m == 8.0

    def test_ifc_knobs(self, tmp_path):
        cfg = """
tolerances:
  ifc:
    pile_footprint_max_m: 1.5
    pile_aspect_ratio_min: 2.5
    match_max_distance_m: 1.0
    setout_tolerance_mm: "8mm"
    deck_footprint_min_m: 4.0
    deck_aspect_ratio_max: 0.05
    beam_footprint_min_m: 6.0
    beam_aspect_ratio_min: 0.07
    beam_aspect_ratio_max: 0.4
"""
        loaded = load_session_config(_write(tmp_path, cfg))
        c = loaded.rule_config
        assert c.ifc_pile_footprint_max_m == 1.5
        assert c.ifc_pile_aspect_ratio_min == 2.5
        assert c.ifc_match_max_distance_m == 1.0
        assert c.ifc_setout_tolerance_mm == 8.0
        assert c.ifc_deck_footprint_min_m == 4.0
        assert c.ifc_deck_aspect_ratio_max == 0.05
        assert c.ifc_beam_footprint_min_m == 6.0
        assert c.ifc_beam_aspect_ratio_min == 0.07
        assert c.ifc_beam_aspect_ratio_max == 0.4
