"""Tests for the BCF 2.1 writer.

Runs entirely off Revit, same as every other test here: `bcf.py` only
consumes `Issue` objects, so a `.bcfzip`'s bytes can be built and
re-parsed with the stdlib `zipfile`/`xml` modules right here.
"""

import io
import zipfile
from xml.etree import ElementTree as ET

import pytest

from revitcheck.bcf import DEFAULT_MAX_ISSUES_PER_FILE, to_bcf_files
from revitcheck.issue import Issue


def _issue(**kw):
    defaults = dict(rule_id="revit.dimension_provenance", category="geometry", description="A finding.")
    defaults.update(kw)
    return Issue(**defaults)


def _unzip(data):
    return zipfile.ZipFile(io.BytesIO(data))


class TestSplitting:
    def test_no_issues_produces_no_files(self):
        assert to_bcf_files([]) == []

    def test_issues_under_the_cap_produce_one_file(self):
        issues = [_issue(element_id=i) for i in range(5)]
        files = to_bcf_files(issues, max_issues_per_file=100)
        assert len(files) == 1
        assert files[0][0].endswith(".bcfzip")

    def test_issues_over_the_cap_split_into_multiple_files(self):
        issues = [_issue(element_id=i) for i in range(250)]
        files = to_bcf_files(issues, max_issues_per_file=100)
        assert len(files) == 3
        names = [name for name, _ in files]
        assert names == sorted(names)  # -001-, -002-, -003- sort in order
        for name in names:
            assert "of-003" in name

    def test_exactly_at_the_cap_is_one_file(self):
        issues = [_issue(element_id=i) for i in range(100)]
        files = to_bcf_files(issues, max_issues_per_file=100)
        assert len(files) == 1

    def test_default_cap_matches_forma(self):
        assert DEFAULT_MAX_ISSUES_PER_FILE == 100

    def test_filenames_are_distinct(self):
        issues = [_issue(element_id=i) for i in range(150)]
        files = to_bcf_files(issues, max_issues_per_file=100)
        assert len({name for name, _ in files}) == len(files)

    def test_every_topic_lands_in_exactly_one_file(self):
        issues = [_issue(element_id=i, description="finding {0}".format(i)) for i in range(120)]
        files = to_bcf_files(issues, max_issues_per_file=100)
        total_topics = 0
        for _name, data in files:
            zf = _unzip(data)
            topic_dirs = {n.split("/")[0] for n in zf.namelist() if "/" in n}
            total_topics += len(topic_dirs)
        assert total_topics == 120


class TestTopicIdentity:
    def test_same_finding_gets_the_same_topic_guid_across_runs(self):
        # The whole point: re-exporting after a model change should let
        # Forma recognise unchanged findings as the same topic, not mint
        # a fresh one every run.
        issue = _issue(element_id=5, view_id=10, sheet_no="S101")
        first = to_bcf_files([issue])
        second = to_bcf_files([issue])
        guid_1 = _unzip(first[0][1]).namelist()[1].split("/")[0]
        guid_2 = _unzip(second[0][1]).namelist()[1].split("/")[0]
        assert guid_1 == guid_2

    def test_different_findings_get_different_topic_guids(self):
        a = _issue(element_id=5, description="finding A")
        b = _issue(element_id=6, description="finding B")
        files = to_bcf_files([a, b])
        zf = _unzip(files[0][1])
        topic_dirs = {n.split("/")[0] for n in zf.namelist() if "/" in n}
        assert len(topic_dirs) == 2

    def test_topic_guid_tracks_issue_id_not_incidental_fields(self):
        # severity isn't part of issue_id's identity (issue.py), so
        # re-tiering a rule in config must not re-mint the Topic Guid
        # either -- otherwise config changes would look like new findings
        # to Forma the same way they would to a human re-running the tool.
        a = _issue(element_id=5, severity="high")
        b = _issue(element_id=5, severity="low")
        assert a.issue_id == b.issue_id
        guid_a = _unzip(to_bcf_files([a])[0][1]).namelist()[1].split("/")[0]
        guid_b = _unzip(to_bcf_files([b])[0][1]).namelist()[1].split("/")[0]
        assert guid_a == guid_b


class TestBcfVersion:
    def test_every_file_declares_2_1(self):
        files = to_bcf_files([_issue()], max_issues_per_file=100)
        zf = _unzip(files[0][1])
        version_xml = zf.read("bcf.version").decode("utf-8")
        root = ET.fromstring(version_xml)
        assert root.attrib["VersionId"] == "2.1"


class TestMarkup:
    def test_topic_carries_title_description_and_status(self):
        issue = _issue(
            description="Dimension measures detail linework.",
            sheet_no="S101",
            view_name="SECTION A-A",
            severity="high",
            element_id=42,
        )
        files = to_bcf_files([issue])
        zf = _unzip(files[0][1])
        markup_path = next(n for n in zf.namelist() if n.endswith("markup.bcf"))
        root = ET.fromstring(zf.read(markup_path))
        topic = root.find("Topic")
        assert topic.attrib["TopicStatus"] == "Open"
        assert "S101" in topic.find("Title").text
        assert "SECTION A-A" in topic.find("Title").text
        assert topic.find("Description").text == "Dimension measures detail linework."
        assert topic.find("Priority").text == "High"

    def test_severity_maps_to_priority(self):
        for severity, expected in [("high", "High"), ("medium", "Normal"), ("low", "Low")]:
            files = to_bcf_files([_issue(severity=severity)])
            zf = _unzip(files[0][1])
            markup_path = next(n for n in zf.namelist() if n.endswith("markup.bcf"))
            root = ET.fromstring(zf.read(markup_path))
            assert root.find("Topic/Priority").text == expected

    def test_xml_special_characters_are_escaped(self):
        issue = _issue(description='Typed as <5mm> but measures 6mm & "drifts".', element_id=1)
        files = to_bcf_files([issue])
        zf = _unzip(files[0][1])
        markup_path = next(n for n in zf.namelist() if n.endswith("markup.bcf"))
        raw = zf.read(markup_path).decode("utf-8")
        # Must parse as valid XML despite the raw text containing '<', '&', '"'.
        root = ET.fromstring(raw)
        assert root.find("Topic/Description").text == 'Typed as <5mm> but measures 6mm & "drifts".'

    def test_title_falls_back_to_rule_id_with_no_location(self):
        issue = _issue(rule_id="revit.capture_coverage", category="coverage", sheet_no=None, view_name=None)
        files = to_bcf_files([issue])
        zf = _unzip(files[0][1])
        markup_path = next(n for n in zf.namelist() if n.endswith("markup.bcf"))
        root = ET.fromstring(zf.read(markup_path))
        assert root.find("Topic/Title").text == "revit.capture_coverage"


class TestViewpoint:
    def test_issue_with_unique_id_gets_a_pinned_viewpoint(self):
        issue = _issue(element_id=5, unique_id="d919e769-2a86-4b1c-a9c4-00000000abcd-0002f1e3")
        files = to_bcf_files([issue])
        zf = _unzip(files[0][1])
        vp_path = next(n for n in zf.namelist() if n.endswith("viewpoint.bcfv"))
        root = ET.fromstring(zf.read(vp_path))
        component = root.find("Components/Selection/Component")
        assert component.attrib["AuthoringToolId"] == "d919e769-2a86-4b1c-a9c4-00000000abcd-0002f1e3"
        assert component.attrib["OriginatingSystem"] == "Revit"
        assert "IfcGuid" not in component.attrib

        markup_path = next(n for n in zf.namelist() if n.endswith("markup.bcf"))
        markup_root = ET.fromstring(zf.read(markup_path))
        viewpoints = markup_root.find("Viewpoints")
        assert viewpoints.attrib["Viewpoint"] == "viewpoint.bcfv"

    def test_coverage_issue_with_no_element_gets_no_viewpoint(self):
        issue = _issue(rule_id="revit.capture_coverage", category="coverage", element_id=None, unique_id=None)
        files = to_bcf_files([issue])
        zf = _unzip(files[0][1])
        assert not any(n.endswith("viewpoint.bcfv") for n in zf.namelist())
        markup_path = next(n for n in zf.namelist() if n.endswith("markup.bcf"))
        root = ET.fromstring(zf.read(markup_path))
        assert root.find("Viewpoints") is None

    def test_element_id_without_unique_id_still_gets_a_viewpoint(self):
        # unique_id is what makes the pin durable, but a capture taken
        # before that field existed should still produce a pinned
        # viewpoint -- just without AuthoringToolId to anchor it.
        issue = _issue(element_id=5, unique_id=None)
        files = to_bcf_files([issue])
        zf = _unzip(files[0][1])
        assert any(n.endswith("viewpoint.bcfv") for n in zf.namelist())
        vp_path = next(n for n in zf.namelist() if n.endswith("viewpoint.bcfv"))
        root = ET.fromstring(zf.read(vp_path))
        component = root.find("Components/Selection/Component")
        assert "AuthoringToolId" not in component.attrib
