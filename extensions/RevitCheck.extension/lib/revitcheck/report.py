"""Turn a list of Issues into something a person reads.

Two audiences, one shape. `to_json` is the machine-readable artifact —
the same role `markup/report.py` plays on the PDF side, and the input a
future selection step would round-trip. `to_markdown` is what the
pyRevit output window renders.

The markdown form deliberately does *not* try to be a report document.
On the PDF side an Issue had to carry enough text to survive being
printed onto a sheet and handed to someone who could not ask a question
about it. Here the reader is sitting in the model with the element one
click away, so the output's job is to get them to that element quickly:
group by sheet, most severe first, and let the link do the explaining.
"""

from __future__ import annotations

import json
from typing import Any, Dict, Iterable, List, Optional

from revitcheck.issue import Issue, sort_issues

_SEVERITY_MARK = {"high": "!!", "medium": "!", "low": "-"}


def to_json(issues: Iterable[Issue], model_title: str = "", indent: int = 2) -> str:
    payload: Dict[str, Any] = {
        "model": model_title,
        "issue_count": 0,
        "issues": [],
    }
    listed = [i.to_dict() for i in sort_issues(list(issues))]
    payload["issues"] = listed
    payload["issue_count"] = len(listed)
    return json.dumps(payload, indent=indent, sort_keys=True)


def summarize(issues: Iterable[Issue]) -> Dict[str, int]:
    counts = {"high": 0, "medium": 0, "low": 0}
    for issue in issues:
        counts[issue.severity] = counts.get(issue.severity, 0) + 1
    return counts


def to_markdown(
    issues: Iterable[Issue],
    model_title: str = "",
    linkify=None,
    max_rows: Optional[int] = None,
) -> str:
    """Render issues as markdown for pyRevit's output window.

    `linkify` is pyRevit's `output.linkify` when one is available: given
    an element id it returns clickable markup that selects and zooms the
    element in Revit. It is passed in rather than imported so this
    module stays free of any pyRevit dependency and remains testable
    off a Revit machine — the same reason the Revit API is confined to
    one adapter.
    """
    issues = sort_issues(list(issues))
    counts = summarize(issues)

    lines: List[str] = []
    header = "## Check results"
    if model_title:
        header += " — {0}".format(model_title)
    lines.append(header)

    if not issues:
        lines.append("")
        lines.append("No issues found.")
        return "\n".join(lines)

    lines.append("")
    lines.append(
        "{0} issue(s): {1} high, {2} medium, {3} low".format(
            len(issues), counts["high"], counts["medium"], counts["low"]
        )
    )

    shown = issues if max_rows is None else issues[:max_rows]
    current_sheet = object()  # sentinel: no sheet grouped yet

    for issue in shown:
        if issue.sheet_no != current_sheet:
            current_sheet = issue.sheet_no
            lines.append("")
            lines.append(
                "### Sheet {0}".format(current_sheet)
                if current_sheet
                else "### Not on a sheet"
            )

        mark = _SEVERITY_MARK.get(issue.severity, "-")
        target = ""
        if issue.element_id is not None and linkify is not None:
            target = " {0}".format(linkify(issue.element_id))
        elif issue.element_id is not None:
            target = " [id {0}]".format(issue.element_id)

        lines.append("- **{0}** {1}{2}".format(mark, issue.description, target))

    if max_rows is not None and len(issues) > max_rows:
        lines.append("")
        lines.append(
            "_...and {0} more. Export the JSON for the full list._".format(
                len(issues) - max_rows
            )
        )

    return "\n".join(lines)
