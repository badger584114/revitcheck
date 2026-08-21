"""Tests for the provenance classifier and its rule.

Every one of these runs without Revit, which is the point of the
adapter/IR split — see `revitcheck/__init__.py`.
"""

import pytest

from revitcheck import RuleConfig, run_checks
from revitcheck.checks.dimensions import (
    classify_dimension,
    classify_reference,
    drafted_views,
    views_in_scope,
)
from revitcheck.ir import Provenance, ReferenceInfo


class TestClassifyReference:
    def test_model_geometry(self, make):
        assert classify_reference(make.model_ref()) == Provenance.MODEL

    def test_view_specific_is_drafted(self, make):
        assert classify_reference(make.drafted_ref()) == Provenance.DRAFTED

    def test_grid_is_a_datum_not_a_risk(self, make):
        # Dimensioning to a grid is good practice: move the grid and the
        # dimension follows. It must not be lumped in with linework.
        assert classify_reference(make.datum_ref()) == Provenance.DATUM

    def test_level_and_reference_plane_are_datums(self):
        for cls in ("Level", "ReferencePlane", "MultiSegmentGrid"):
            ref = ReferenceInfo(element_id=5, class_name=cls, view_specific=False)
            assert classify_reference(ref) == Provenance.DATUM

    def test_imported_cad_is_drafted_despite_not_being_view_specific(self):
        # A DWG imported into the model isn't view-specific, so the
        # view_specific test alone would call it model geometry. It is
        # a static snapshot of someone else's file — same failure mode
        # as detail linework, different mechanism.
        ref = ReferenceInfo(
            element_id=7, class_name="ImportInstance", view_specific=False
        )
        assert classify_reference(ref) == Provenance.DRAFTED

    def test_unresolved_is_unknown_not_assumed_clean(self, make):
        assert classify_reference(make.unresolved_ref()) == Provenance.UNKNOWN

    def test_invalid_element_id_is_unknown(self):
        assert classify_reference(ReferenceInfo(element_id=-1)) == Provenance.UNKNOWN

    def test_view_specific_beats_class_name(self):
        # Order matters: Revit's own view_specific flag is the invariant,
        # so it wins even when the class name looks like model geometry.
        ref = ReferenceInfo(element_id=9, class_name="Wall", view_specific=True)
        assert classify_reference(ref) == Provenance.DRAFTED


class TestClassifyDimension:
    def test_all_model(self, make):
        dim = make.dimension(1, 10, [make.model_ref(), make.model_ref(101)])
        assert classify_dimension(dim) == Provenance.MODEL

    def test_all_drafted(self, make):
        dim = make.dimension(1, 10, [make.drafted_ref(), make.drafted_ref(201)])
        assert classify_dimension(dim) == Provenance.DRAFTED

    def test_datum_only(self, make):
        dim = make.dimension(1, 10, [make.datum_ref(), make.datum_ref(301)])
        assert classify_dimension(dim) == Provenance.DATUM

    def test_model_plus_datum_is_live(self, make):
        dim = make.dimension(1, 10, [make.model_ref(), make.datum_ref()])
        assert classify_dimension(dim) == Provenance.MODEL

    def test_model_plus_drafted_is_mixed(self, make):
        dim = make.dimension(1, 10, [make.model_ref(), make.drafted_ref()])
        assert classify_dimension(dim) == Provenance.MIXED

    def test_datum_plus_drafted_is_also_mixed(self, make):
        dim = make.dimension(1, 10, [make.datum_ref(), make.drafted_ref()])
        assert classify_dimension(dim) == Provenance.MIXED

    def test_unknown_does_not_mask_a_drafted_reference(self, make):
        # A dimension half of whose references failed to resolve is
        # still drafted if the resolvable half is linework — an
        # extraction gap must not launder a real finding.
        dim = make.dimension(1, 10, [make.unresolved_ref(), make.drafted_ref()])
        assert classify_dimension(dim) == Provenance.DRAFTED

    def test_all_unknown(self, make):
        dim = make.dimension(1, 10, [make.unresolved_ref()])
        assert classify_dimension(dim) == Provenance.UNKNOWN

    def test_no_references_at_all(self, make):
        dim = make.dimension(1, 10, [])
        assert classify_dimension(dim) == Provenance.UNKNOWN


class TestScoping:
    def test_view_templates_excluded(self, make):
        views = [make.view(10), make.view(11, is_template=True)]
        scoped = views_in_scope(make.model(views=views), RuleConfig())
        assert [v.element_id for v in scoped] == [10]

    def test_unplaced_views_excluded_by_default(self, make):
        views = [make.view(10), make.view(11, sheet_no=None)]
        scoped = views_in_scope(make.model(views=views), RuleConfig())
        assert [v.element_id for v in scoped] == [10]

    def test_unplaced_views_included_when_asked(self, make):
        views = [make.view(10), make.view(11, sheet_no=None)]
        scoped = views_in_scope(
            make.model(views=views), RuleConfig(sheeted_views_only=False)
        )
        assert len(scoped) == 2

    def test_template_still_excluded_when_sweeping_everything(self, make):
        views = [make.view(11, sheet_no=None, is_template=True)]
        scoped = views_in_scope(
            make.model(views=views), RuleConfig(sheeted_views_only=False)
        )
        assert scoped == []

    def test_unlinked_drafting_view_excluded_by_default(self, make):
        views = [make.view(10), make.view(11, view_type="DraftingView")]
        scoped = views_in_scope(make.model(views=views), RuleConfig())
        assert [v.element_id for v in scoped] == [10]

    def test_linked_drafting_view_stays_in_scope(self, make):
        views = [
            make.view(11, view_type="DraftingView", linked_to_model_section=True)
        ]
        scoped = views_in_scope(make.model(views=views), RuleConfig())
        assert [v.element_id for v in scoped] == [11]

    def test_unlinked_drafting_view_included_when_opted_in(self, make):
        views = [make.view(11, view_type="DraftingView")]
        scoped = views_in_scope(
            make.model(views=views), RuleConfig(skip_unlinked_drafting_views=False)
        )
        assert [v.element_id for v in scoped] == [11]

    def test_legend_is_never_linked_and_stays_excluded(self, make):
        # linked_to_model_section is a Drafting View concept (a callout
        # references one); a Legend can't be one, so it has no escape
        # hatch out of this exclusion the way a Drafting View does.
        views = [make.view(11, view_type="Legend")]
        scoped = views_in_scope(make.model(views=views), RuleConfig())
        assert scoped == []


def _run(model, config=None):
    return run_checks(
        model, config or RuleConfig(enabled_rule_ids={"revit.dimension_provenance"})
    )


class TestRule:
    def test_live_view_reports_nothing(self, make):
        dims = [
            make.dimension(1, 10, [make.model_ref(), make.model_ref(101)]),
            make.dimension(2, 10, [make.model_ref(), make.datum_ref()]),
        ]
        issues = _run(make.model(views=[make.view(10)], dimensions=dims))
        assert issues == []

    def test_single_drafted_dimension_in_a_live_view(self, make):
        dims = [
            make.dimension(1, 10, [make.model_ref(), make.model_ref(101)]),
            make.dimension(2, 10, [make.drafted_ref(), make.drafted_ref(201)]),
        ]
        issues = _run(make.model(views=[make.view(10)], dimensions=dims))
        assert len(issues) == 1
        assert issues[0].element_id == 2
        assert issues[0].severity == "high"
        assert issues[0].sheet_no == "S101"
        assert "detail linework" in issues[0].description

    def test_fully_drafted_view_rolls_up_to_one_issue(self, make):
        # Twenty identical findings on one view is noise; the view is
        # the real finding, and it is the unit the follow-up tool works
        # on.
        dims = [
            make.dimension(i, 10, [make.drafted_ref(), make.drafted_ref(201)])
            for i in range(1, 6)
        ]
        issues = _run(make.model(views=[make.view(10)], dimensions=dims))
        assert len(issues) == 1
        assert issues[0].element_id == 10  # the view, not a dimension
        assert issues[0].suggested_fix["scope"] == "view"
        assert issues[0].suggested_fix["dimensions"] == 5

    def test_majority_drafted_view_rolls_up_with_the_live_dimension_excluded(self, make):
        # The real-world case that motivated the threshold: a view can
        # be almost entirely drafted with a handful of dimensions that
        # genuinely track the model. The old all-or-nothing rule fell
        # through to per-dimension reporting for the whole view; the
        # rollup should still fire, and the live dimension should not
        # appear as an issue at all (it is fine).
        drafted = [
            make.dimension(i, 10, [make.drafted_ref(), make.drafted_ref(200 + i)])
            for i in range(1, 10)
        ]
        live = [make.dimension(50, 10, [make.model_ref(), make.model_ref(301)])]
        issues = _run(make.model(views=[make.view(10)], dimensions=drafted + live))
        assert len(issues) == 1
        assert issues[0].element_id == 10  # the view, not a dimension
        assert issues[0].suggested_fix["scope"] == "view"
        assert issues[0].suggested_fix["dimensions"] == 10
        assert issues[0].suggested_fix["drafted_dimensions"] == 9
        assert "9 of 10 dimensions" in issues[0].description
        assert "Every dimension" not in issues[0].description

    def test_below_threshold_does_not_roll_up(self, make):
        # 7 of 10 drafted (70%) is below the default 90% threshold, so
        # this should still fall through to per-dimension reporting.
        drafted = [
            make.dimension(i, 10, [make.drafted_ref(), make.drafted_ref(200 + i)])
            for i in range(1, 8)
        ]
        live = [
            make.dimension(i, 10, [make.model_ref(), make.model_ref(300 + i)])
            for i in range(8, 11)
        ]
        issues = _run(make.model(views=[make.view(10)], dimensions=drafted + live))
        assert len(issues) == 7
        assert {i.element_id for i in issues} == {1, 2, 3, 4, 5, 6, 7}

    def test_mixed_and_unknown_dimensions_still_reported_inside_a_rollup(self, make):
        # A MIXED or UNKNOWN dimension is a distinct finding the rollup's
        # "detail linework" summary does not cover, so it must survive
        # alongside the rollup rather than being silently absorbed.
        drafted = [
            make.dimension(i, 10, [make.drafted_ref(), make.drafted_ref(200 + i)])
            for i in range(1, 10)
        ]
        mixed = [make.dimension(99, 10, [make.model_ref(), make.drafted_ref()])]
        issues = _run(make.model(views=[make.view(10)], dimensions=drafted + mixed))
        assert len(issues) == 2
        rollup = next(i for i in issues if i.suggested_fix.get("scope") == "view")
        mixed_issue = next(i for i in issues if i.element_id == 99)
        assert rollup.suggested_fix["drafted_dimensions"] == 9
        assert mixed_issue.severity == "medium"

    def test_rollup_threshold_is_configurable(self, make):
        drafted = [
            make.dimension(i, 10, [make.drafted_ref(), make.drafted_ref(200 + i)])
            for i in range(1, 8)
        ]
        live = [
            make.dimension(i, 10, [make.model_ref(), make.model_ref(300 + i)])
            for i in range(8, 11)
        ]
        config = RuleConfig(
            enabled_rule_ids={"revit.dimension_provenance"},
            params={"dimension_provenance": {"rollup_threshold": 0.7}},
        )
        issues = _run(make.model(views=[make.view(10)], dimensions=drafted + live), config)
        assert len(issues) == 1
        assert issues[0].suggested_fix["scope"] == "view"

    def test_roll_up_can_be_turned_off(self, make):
        dims = [
            make.dimension(i, 10, [make.drafted_ref(), make.drafted_ref(201)])
            for i in range(1, 6)
        ]
        config = RuleConfig(
            enabled_rule_ids={"revit.dimension_provenance"},
            params={"dimension_provenance": {"roll_up_fully_drafted_views": False}},
        )
        issues = _run(make.model(views=[make.view(10)], dimensions=dims), config)
        assert len(issues) == 5
        assert {i.element_id for i in issues} == {1, 2, 3, 4, 5}

    def test_single_dimension_view_does_not_roll_up(self, make):
        # One drafted dimension says nothing about how the view was
        # drafted, so it is reported as itself rather than as a verdict
        # on the whole view.
        dims = [make.dimension(1, 10, [make.drafted_ref(), make.drafted_ref(201)])]
        issues = _run(make.model(views=[make.view(10)], dimensions=dims))
        assert len(issues) == 1
        assert issues[0].element_id == 1
        assert issues[0].suggested_fix.get("scope") is None

    def test_unlinked_drafting_view_is_skipped_by_default(self, make):
        # A free-standing drafting view's dimensions were always going
        # to be DRAFTED — there is no decision left to report, so by
        # default it is out of scope entirely rather than producing a
        # low-severity finding for every one of them.
        dims = [
            make.dimension(i, 10, [make.drafted_ref(), make.drafted_ref(201)])
            for i in range(1, 4)
        ]
        views = [make.view(10, name="TYPICAL DETAIL", view_type="DraftingView")]
        issues = _run(make.model(views=views, dimensions=dims))
        assert issues == [] or all(i.category == "coverage" for i in issues)

    def test_unlinked_drafting_view_checked_when_opted_in(self, make):
        # Still reachable via config, e.g. for an audit that wants full
        # coverage on record rather than the reduced-volume default.
        dims = [
            make.dimension(i, 10, [make.drafted_ref(), make.drafted_ref(201)])
            for i in range(1, 4)
        ]
        views = [make.view(10, name="TYPICAL DETAIL", view_type="DraftingView")]
        config = RuleConfig(
            enabled_rule_ids={"revit.dimension_provenance"},
            skip_unlinked_drafting_views=False,
        )
        issues = _run(make.model(views=views, dimensions=dims), config)
        assert len(issues) == 1
        assert issues[0].severity == "low"
        assert "no model behind" in issues[0].description

    def test_linked_drafting_view_is_checked_at_model_severity(self, make):
        # A drafting view referenced by a "Reference other view" callout
        # from a section is standing in for that section — it never has
        # model geometry either way, but pretending it is a harmless
        # standard detail would hide the real drift risk it carries.
        dims = [
            make.dimension(i, 10, [make.drafted_ref(), make.drafted_ref(201)])
            for i in range(1, 4)
        ]
        views = [
            make.view(
                10,
                name="ABUTMENT A SECTION (ref)",
                view_type="DraftingView",
                linked_to_model_section=True,
            )
        ]
        issues = _run(make.model(views=views, dimensions=dims))
        assert len(issues) == 1
        assert issues[0].severity == "high"
        assert "no model behind" not in issues[0].description
        assert issues[0].suggested_fix["scope"] == "view"

    def test_mixed_provenance_reported_separately(self, make):
        dims = [make.dimension(1, 10, [make.model_ref(), make.drafted_ref()])]
        issues = _run(make.model(views=[make.view(10)], dimensions=dims))
        assert len(issues) == 1
        assert issues[0].severity == "medium"
        assert issues[0].suggested_fix["drafted_references"] == 1

    def test_spot_dimension_is_labelled_as_one(self, make):
        dims = [
            make.dimension(1, 10, [make.drafted_ref()], spot=True),
            make.dimension(2, 10, [make.model_ref()]),
        ]
        issues = _run(make.model(views=[make.view(10)], dimensions=dims))
        assert len(issues) == 1
        assert issues[0].description.startswith("Spot dimension")

    def test_unresolved_dimension_is_a_low_coverage_finding(self, make):
        dims = [
            make.dimension(1, 10, [make.unresolved_ref()]),
            make.dimension(2, 10, [make.model_ref()]),
        ]
        issues = _run(make.model(views=[make.view(10)], dimensions=dims))
        assert len(issues) == 1
        assert issues[0].severity == "low"
        assert "not checked" in issues[0].description

    def test_no_dimensions_reports_coverage_not_silence(self, make):
        # The bug this guards against is the expensive one: a rule that
        # ran against nothing looking exactly like a clean model.
        issues = _run(make.model(views=[make.view(10)], dimensions=[]))
        assert len(issues) == 1
        assert issues[0].category == "coverage"
        assert issues[0].element_id is None

    def test_dimensions_only_in_unplaced_views_still_reports_coverage(self, make):
        dims = [make.dimension(1, 11, [make.drafted_ref()])]
        views = [make.view(10), make.view(11, sheet_no=None)]
        issues = _run(make.model(views=views, dimensions=dims))
        assert len(issues) == 1
        assert issues[0].category == "coverage"


class TestDraftedViewsHandoff:
    def test_lists_only_fully_drafted_views(self, make):
        views = [make.view(10, name="ALL DRAFTED"), make.view(11, name="LIVE")]
        dims = [
            make.dimension(1, 10, [make.drafted_ref()]),
            make.dimension(2, 10, [make.drafted_ref()]),
            make.dimension(3, 11, [make.model_ref()]),
            make.dimension(4, 11, [make.drafted_ref()]),
        ]
        result = drafted_views(make.model(views=views, dimensions=dims), RuleConfig())
        assert [v.name for v in result] == ["ALL DRAFTED"]

    def test_empty_when_nothing_is_drafted(self, make):
        views = [make.view(10)]
        dims = [make.dimension(i, 10, [make.model_ref()]) for i in (1, 2)]
        assert drafted_views(make.model(views=views, dimensions=dims), RuleConfig()) == []
