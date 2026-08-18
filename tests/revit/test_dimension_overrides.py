"""`revit.dimension_override_consistency` — typed value vs. what the
model measures.

Synthetic IR throughout: no capture from a real model exists yet, and
these are branch tests rather than calibration. The three tolerance
figures the rule uses are inherited placeholders (see `RuleConfig`), so
nothing here asserts that a *particular* millimetre threshold is right —
only that the rounding-grid comparison behaves as designed and that the
skip-rather-than-guess cases are counted instead of dropped.
"""

import pytest

from revitcheck import RuleConfig
from revitcheck.checks.dimensions import (
    check_dimension_override_consistency,
    parse_override_mm,
)
from revitcheck.ir import Provenance


def findings(issues):
    """The real findings — the coverage Issue is always present and is
    asserted separately."""
    return [i for i in issues if i.category != "coverage"]


def coverage(issues):
    matching = [i for i in issues if i.category == "coverage"]
    assert len(matching) == 1, "exactly one coverage Issue per run"
    return matching[0]


def one_dimension(make, value_mm, override, type_name=None, refs=None):
    view = make.view(10)
    dim = make.dimension(
        1,
        10,
        refs if refs is not None else [make.model_ref()],
        value_mm=value_mm,
        override=override,
        type_name=type_name,
    )
    return make.model(views=[view], dimensions=[dim])


class TestParseOverride:
    @pytest.mark.parametrize(
        "text,expected",
        [
            ("1200", 1200.0),
            (" 1200 ", 1200.0),
            ("1200.5", 1200.5),
            ("1200mm", 1200.0),
            ("1200 MM", 1200.0),
            ("1,200", 1200.0),
            ("-15", -15.0),
        ],
    )
    def test_numeric_forms(self, text, expected):
        assert parse_override_mm(text) == expected

    @pytest.mark.parametrize(
        "text",
        [
            None,
            "",
            "EQ",
            "VARIES",
            "TYP",
            "A",  # a bar-mark letter keying into a schedule table
            "500 MIN.",
            "1200-1400",
            "1,2",  # decimal comma is a different convention — not guessed at
        ],
    )
    def test_non_numeric_forms_are_not_guessed(self, text):
        assert parse_override_mm(text) is None

    def test_invisible_format_characters_are_stripped(self):
        """The DXF export carried a literal trailing U+200E on some
        override text — invisible in an editor, so a valid override
        failed to parse for a reason with no visible cause."""

        assert parse_override_mm("1200‎") == 1200.0


class TestToleranceBranches:
    def test_within_the_rounding_grid_passes(self, make):
        # 1200 typed over a measured 1198.2: 1.8mm, inside 5/2 + 0.5.
        model = one_dimension(make, 1198.2, "1200")
        assert findings(check_dimension_override_consistency(model, RuleConfig())) == []

    def test_beyond_the_rounding_grid_is_flagged(self, make):
        model = one_dimension(make, 1150.0, "1200")
        issues = findings(check_dimension_override_consistency(model, RuleConfig()))
        assert len(issues) == 1
        assert issues[0].severity == "high"
        assert issues[0].suggested_fix["delta_mm"] == 50.0
        assert issues[0].suggested_fix["tier"] == "default"
        # Both values reach the reader — never just "these disagree".
        assert "1200" in issues[0].description
        assert "1150" in issues[0].description

    def test_setout_critical_type_gets_the_tighter_grid(self, make):
        """1.8mm passes on the default grid and fails on the tight one.
        Same dimension, same override — only the type name differs."""

        config = RuleConfig(setout_critical_type_names=["Setout - 1mm"])

        loose = one_dimension(make, 1198.2, "1200")
        assert findings(check_dimension_override_consistency(loose, config)) == []

        tight = one_dimension(make, 1198.2, "1200", type_name="Setout - 1mm")
        issues = findings(check_dimension_override_consistency(tight, config))
        assert len(issues) == 1
        assert issues[0].suggested_fix["tier"] == "setout_critical"

    def test_an_unlisted_type_name_is_not_assumed_critical(self, make):
        model = one_dimension(make, 1198.2, "1200", type_name="Some Other Style")
        assert findings(check_dimension_override_consistency(model, RuleConfig())) == []


class TestWhatIsSkipped:
    def test_a_dimension_with_no_override_is_not_compared(self, make):
        # Wildly wrong, but nobody typed anything — there is no claim to
        # check. This rule covers workaround 1 only.
        model = one_dimension(make, 5.0, None)
        issues = check_dimension_override_consistency(model, RuleConfig())
        assert findings(issues) == []
        assert coverage(issues).suggested_fix["overridden"] == 0

    def test_a_non_numeric_override_is_counted_not_guessed(self, make):
        model = one_dimension(make, 1150.0, "VARIES")
        issues = check_dimension_override_consistency(model, RuleConfig())
        assert findings(issues) == []
        summary = coverage(issues).suggested_fix
        assert summary["overridden"] == 1
        assert summary["checked"] == 0
        assert summary["unparsed"] == 1
        assert "'VARIES'" in coverage(issues).description

    def test_no_measured_value_is_skipped(self, make):
        """Revit reports no value for some spot dimension types. Nothing
        to compare against, so it is not a finding and not 'checked'."""

        model = one_dimension(make, None, "1200")
        issues = check_dimension_override_consistency(model, RuleConfig())
        assert findings(issues) == []
        assert coverage(issues).suggested_fix["checked"] == 0

    def test_unsheeted_views_are_out_of_scope_by_default(self, make):
        view = make.view(10, sheet_no=None)
        dim = make.dimension(1, 10, [make.model_ref()], value_mm=1150.0, override="1200")
        model = make.model(views=[view], dimensions=[dim])

        assert findings(check_dimension_override_consistency(model, RuleConfig())) == []

        swept = check_dimension_override_consistency(
            model, RuleConfig(sheeted_views_only=False)
        )
        assert len(findings(swept)) == 1


class TestChains:
    def test_only_the_overridden_segment_is_compared(self, make):
        """A chain is one element with many segments. Two segments here
        are wrong but untyped; only the third makes a claim."""

        view = make.view(10)
        dim = make.chain(
            1,
            10,
            [make.model_ref()],
            segments=[(500.0, None), (600.0, None), (1150.0, "1200")],
        )
        model = make.model(views=[view], dimensions=[dim])

        issues = findings(check_dimension_override_consistency(model, RuleConfig()))
        assert len(issues) == 1
        assert issues[0].suggested_fix["segment"] == 3
        assert issues[0].suggested_fix["segments"] == 3
        # The element id selects the chain; the description says which
        # number inside it to look at.
        assert issues[0].element_id == 1
        assert "Segment 3 of 3" in issues[0].description

    def test_every_segment_counts_towards_coverage(self, make):
        view = make.view(10)
        dim = make.chain(
            1, 10, [make.model_ref()], segments=[(500.0, None), (1200.0, "1200")]
        )
        model = make.model(views=[view], dimensions=[dim])

        summary = coverage(check_dimension_override_consistency(model, RuleConfig()))
        assert summary.suggested_fix["segments"] == 2
        assert summary.suggested_fix["overridden"] == 1
        assert summary.suggested_fix["checked"] == 1


class TestProvenanceTravelsWithTheFinding:
    """An override on a drafted dimension is being compared against the
    length of a detail line, not against the model. Same finding,
    different meaning — so the verdict rides along rather than being
    re-derived by whoever reads the output."""

    @pytest.mark.parametrize(
        "ref_name,expected",
        [
            ("model_ref", Provenance.MODEL),
            ("drafted_ref", Provenance.DRAFTED),
            ("datum_ref", Provenance.DATUM),
        ],
    )
    def test_verdict_is_carried(self, make, ref_name, expected):
        ref = getattr(make, ref_name)()
        model = one_dimension(make, 1150.0, "1200", refs=[ref])
        issues = findings(check_dimension_override_consistency(model, RuleConfig()))
        assert issues[0].suggested_fix["provenance"] == expected

    def test_a_drafted_dimension_is_still_reported(self, make):
        """Not filtered out. Per the standing position: assume nothing is
        trustworthy. A drafter disagreeing with their own linework by
        50mm is worth knowing about."""

        model = one_dimension(make, 1150.0, "1200", refs=[make.drafted_ref()])
        assert len(findings(check_dimension_override_consistency(model, RuleConfig()))) == 1


class TestCoverageIsAlwaysReported:
    """The gap this rule inherits, closed at the outset rather than
    discovered on a second client. The DXF ancestor was structurally
    inert on a whole client's drawings — 4.5% of dimensions overridden,
    none numeric — and reported zero findings, which reads as clean."""

    def test_a_clean_run_still_says_how_much_it_checked(self, make):
        model = one_dimension(make, 1200.0, "1200")
        issues = check_dimension_override_consistency(model, RuleConfig())
        assert findings(issues) == []
        summary = coverage(issues)
        assert summary.severity == "low"
        assert summary.suggested_fix["checked"] == 1

    def test_nothing_checkable_says_so_explicitly(self, make):
        view = make.view(10)
        dims = [
            make.dimension(i, 10, [make.model_ref()], value_mm=1000.0, override="EQ")
            for i in range(1, 4)
        ]
        model = make.model(views=[view], dimensions=dims)

        summary = coverage(check_dimension_override_consistency(model, RuleConfig()))
        assert summary.suggested_fix["checked"] == 0
        assert "nothing to check" in summary.description
        assert "'EQ' x3" in summary.description

    def test_an_empty_model_is_not_silence(self, make):
        summary = coverage(
            check_dimension_override_consistency(make.model(), RuleConfig())
        )
        assert "No dimensions were found" in summary.description

    def test_distinct_unparsed_forms_are_listed_for_recognition(self, make):
        """A new client's override convention should surface as data, not
        as an absence of findings."""

        view = make.view(10)
        dims = [
            make.dimension(1, 10, [make.model_ref()], override="EQ"),
            make.dimension(2, 10, [make.model_ref()], override="500 MIN."),
            make.dimension(3, 10, [make.model_ref()], override="VARIES"),
        ]
        model = make.model(views=[view], dimensions=dims)

        description = coverage(
            check_dimension_override_consistency(model, RuleConfig())
        ).description
        for form in ("'EQ'", "'500 MIN.'", "'VARIES'"):
            assert form in description
