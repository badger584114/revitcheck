"""Smoke test for `report.to_bcf`'s delegation to `bcf.to_bcf_files`.

`report.py` otherwise has no dedicated test file — `to_json`/`to_markdown`
are exercised indirectly through `scripts/check_capture.py` rather than
directly, a pre-existing gap this doesn't attempt to close. `to_bcf` gets
one here because it's new and thin enough that "does it actually call
through" is the whole test; `bcf.py`'s own tests (`test_bcf.py`) cover
what the output looks like.
"""

from revitcheck.issue import Issue
from revitcheck.report import to_bcf


def test_to_bcf_delegates_to_the_bcf_writer():
    issues = [Issue(rule_id="r", category="geometry", description="d", element_id=1)]
    files = to_bcf(issues, model_title="TEST-BRIDGE")
    assert len(files) == 1
    filename, data = files[0]
    assert filename.startswith("test-bridge")
    assert filename.endswith(".bcfzip")
    assert isinstance(data, bytes)


def test_to_bcf_respects_max_issues_per_file():
    issues = [Issue(rule_id="r", category="geometry", description="d", element_id=i) for i in range(5)]
    files = to_bcf(issues, max_issues_per_file=2)
    assert len(files) == 3
