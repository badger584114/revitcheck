"""Write Issues as BCF 2.1 (`.bcfzip`) files.

**Why BCF, decided 2026-08-18** (PLANNING.md §5d): it is the only
off-machine format that keeps the element anchor. Every other
persistent option — HTML, CSV, a project-level Forma issue — degrades a
finding's location to a number a human retypes into Select by ID. A BCF
`Component` carries `AuthoringToolId`, which this module fills with
`Issue.unique_id` (Revit's `Element.UniqueId` — see `ir.ViewInfo`'s
field docstring for why that and not `element_id`, which is stable
within a session but not the identifier Revit itself calls durable).

**§12, 2026-08-21: this is currently the proof, not the product.** The
checks stay Python either way (§12's decided direction is a native
add-in around them, not a rewrite of them), so this module's job is to
prove the Revit → BCF → Forma → Revit round trip works at all before
the add-in exists to automate it — see the module's own tests for what
"prove" means here: a real `.bcfzip` this project can hand to Forma and
watch what comes back.

**A `.bcfzip` is a ZIP file** with this layout, one folder per finding
("Topic" in BCF's vocabulary):

    bcf.version
    project.bcfp                   -- project id + name, both deterministic
    <topic-guid>/markup.bcf        -- title, description, status
    <topic-guid>/viewpoint.bcfv    -- the Component this Topic pins to,
                                       omitted when the Issue has
                                       nothing to pin (a coverage
                                       finding with no element)

**Topic Guids are derived from `Issue.issue_id`, not random.** A capture
is a snapshot, not something the tools keep and diff against — every
run recomputes the full issue list fresh (see `capture.py`'s docstring
on why old captures don't need to be retained). But re-running on a
changed model and re-exporting should let a downstream consumer, chiefly
Forma, recognise "this is the same finding as last time" for the ~99%
of findings that haven't changed, so a reviewer's triage in Forma
survives a re-export rather than resetting to a pile of unread topics
every time. `issue_id` already exists for exactly this ("the same
finding on the same model gets the same id on every run", `issue.py`'s
docstring) — `_deterministic_guid` just makes sure the BCF layer
actually uses it instead of throwing it away with `uuid.uuid4()`.

**`project.bcfp` is written, and it wasn't originally.** The first
version of this module left it out on the grounds that it's optional
in the spec and every real reader tolerates its absence — reasonable
in principle, wrong in practice: a real Forma import of that first
export reported the whole file as **empty**. `project.bcfp` (and a
default camera in every Viewpoint, `_DEFAULT_CAMERA_XML` below — the
module's own note at the time flagged "does a Viewpoint with no camera
import at all" as unconfirmed) are the two closest, cheapest
candidates for what a stricter-than-the-spec importer might be
requiring, added 2026-08-22 to rule them out. **Still not confirmed
which one (or something else) was the actual cause** — this needs a
second real import to know for sure, not just fewer red flags in the
docstring.

**Confirmed against the public BCF 2.1 spec, not guessed at the way the
Revit callout API was** (PLANNING.md §12's `_referenced_drafting_view_ids`
note) — this is an open, versioned, widely-implemented standard, not a
private vendor surface, so the shape below is trustworthy. What
genuinely isn't confirmed yet, because it needs a real BCF import into
Forma to answer: whether a Viewpoint with a Component selection and no
camera imports at all, and — CLAUDE.md's own open item — what Forma
actually does with `AuthoringToolId` on import. Both are exactly what
running this once is for.
"""

from __future__ import annotations

import datetime
import io
import re
import uuid
import zipfile
from typing import Iterable, List, Optional, Tuple
from xml.sax.saxutils import escape

from revitcheck.issue import Issue, sort_issues

BCF_VERSION = "2.1"

# Forma's BCF import rejects a file over this many issues (stated by the
# user, 2026-08-19) -- splitting at this boundary means every exported
# file is importable on its own, rather than discovering the cap on
# whichever upload happens to cross it.
DEFAULT_MAX_ISSUES_PER_FILE = 100

# BCF's TopicStatus and Priority are free text in the base schema (a
# project can extend the allowed values via extensions.xsd, which this
# module doesn't read or write -- see the module docstring on
# project.bcfp). "Open" and this severity mapping are the same sane
# defaults nearly every BCF-producing tool ships with, not a value
# discovered from this firm's Forma project; revisit once the round
# trip shows what Forma actually does with them.
_TOPIC_STATUS = "Open"
_TOPIC_TYPE = "Issue"
_PRIORITY = {"high": "High", "medium": "Normal", "low": "Low"}

# Some real BCF readers truncate or reflow a very long Title. The full
# text always survives in Description regardless, so Title only needs
# to be recognisable in a list, not complete.
_MAX_TITLE_LEN = 200


# A fixed namespace for deriving deterministic Topic/Viewpoint GUIDs
# from an Issue's own `issue_id` (uuid.uuid5) -- generated once for this
# project and never regenerated, since regenerating it would silently
# re-mint every Topic Guid this project has ever exported. Random per
# run (uuid.uuid4) was the first cut and it was wrong: `Issue.issue_id`
# exists precisely so "the same finding on the same model gets the same
# id on every run" (issue.py's docstring), so that a re-export of a
# re-run model lands on the *same* BCF Topic a consumer already has
# open, carrying its status/comments forward, rather than minting a
# fresh "new" topic for a finding that was already triaged last week.
_GUID_NAMESPACE = uuid.UUID("6f6e4b9a-2b1c-4b7a-9b3a-9f6a8f0c9a4e")


def _deterministic_guid(*parts: str) -> str:
    return str(uuid.uuid5(_GUID_NAMESPACE, "\x1f".join(parts)))


def _xml(text: Optional[str]) -> str:
    return escape(text or "")


def _slugify(text: str) -> str:
    slug = re.sub(r"[^A-Za-z0-9]+", "-", text or "").strip("-").lower()
    return slug or "revitcheck"


def _topic_title(issue: Issue) -> str:
    """A short, recognisable label for the topic list — the full
    finding text is `issue.description`, not this."""
    parts = []
    if issue.sheet_no:
        parts.append("Sheet {0}".format(issue.sheet_no))
    if issue.view_name:
        parts.append(issue.view_name)
    title = " — ".join(parts) if parts else issue.rule_id
    if len(title) > _MAX_TITLE_LEN:
        title = title[: _MAX_TITLE_LEN - 1] + "…"
    return title


def _bcf_version_xml() -> str:
    return (
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        '<Version VersionId="{0}"/>\n'
    ).format(BCF_VERSION)


def _markup_xml(
    issue: Issue, topic_guid: str, vp_guid: Optional[str], created_at: str, author: str
) -> str:
    lines = [
        '<?xml version="1.0" encoding="UTF-8"?>',
        '<Markup>',
        '  <Topic Guid="{0}" TopicType="{1}" TopicStatus="{2}">'.format(
            topic_guid, _TOPIC_TYPE, _TOPIC_STATUS
        ),
        "    <Title>{0}</Title>".format(_xml(_topic_title(issue))),
        "    <Priority>{0}</Priority>".format(_PRIORITY.get(issue.severity, "Normal")),
        "    <Description>{0}</Description>".format(_xml(issue.description)),
        "    <CreationDate>{0}</CreationDate>".format(created_at),
        "    <CreationAuthor>{0}</CreationAuthor>".format(_xml(author)),
        "  </Topic>",
    ]
    if vp_guid is not None:
        lines.append(
            '  <Viewpoints Guid="{0}" Viewpoint="viewpoint.bcfv"/>'.format(vp_guid)
        )
    lines.append("</Markup>")
    return "\n".join(lines) + "\n"


# A placeholder camera, not a real one -- this project doesn't carry a
# dimension's or view's camera direction/position anywhere in the IR
# yet, only witness-point origins. Added 2026-08-22 after a real Forma
# import reported the export as "empty": the module docstring already
# flagged "does a Viewpoint with a Component selection and no camera
# import at all" as unconfirmed, and this is the cheap way to rule that
# specific unknown out. Looking at nothing meaningful (the world
# origin, -Z) rather than the element is the honest trade for now --
# fixing that for real needs the check layer to carry a real position
# through onto Issue, not just this file.
_DEFAULT_CAMERA_XML = (
    "  <OrthogonalCamera>\n"
    "    <CameraViewPoint><X>0</X><Y>0</Y><Z>0</Z></CameraViewPoint>\n"
    "    <CameraDirection><X>0</X><Y>0</Y><Z>-1</Z></CameraDirection>\n"
    "    <CameraUpVector><X>0</X><Y>1</Y><Z>0</Z></CameraUpVector>\n"
    "    <ViewToWorldScale>1</ViewToWorldScale>\n"
    "  </OrthogonalCamera>\n"
)


def _viewpoint_xml(issue: Issue, vp_guid: str) -> str:
    # `IfcGuid` is deliberately omitted rather than emitted empty --
    # this project has no real IFC GlobalId for the element, and a
    # fabricated one risks colliding with (or simply not matching) an
    # unrelated element in whatever IFC export a viewer cross-references
    # against. `AuthoringToolId` is the honest field for a Revit-only
    # identifier -- see the module docstring.
    component_attrs = ['OriginatingSystem="Revit"']
    if issue.unique_id:
        component_attrs.append('AuthoringToolId="{0}"'.format(_xml(issue.unique_id)))
    return (
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        '<VisualizationInfo Guid="{0}">\n'
        "{1}"
        "  <Components>\n"
        "    <Selection>\n"
        "      <Component {2}/>\n"
        "    </Selection>\n"
        "  </Components>\n"
        "</VisualizationInfo>\n"
    ).format(vp_guid, _DEFAULT_CAMERA_XML, " ".join(component_attrs))


def _project_bcfp_xml(project_guid: str, project_name: str) -> str:
    return (
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        "<ProjectExtension>\n"
        '  <Project ProjectId="{0}">\n'
        "    <Name>{1}</Name>\n"
        "  </Project>\n"
        "</ProjectExtension>\n"
    ).format(project_guid, _xml(project_name))


def _write_bcfzip(
    issues: List[Issue], model_title: str, created_at: str, author: str
) -> bytes:
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as zf:
        zf.writestr("bcf.version", _bcf_version_xml())
        # Same deterministic-GUID reasoning as a Topic's — the same
        # model should get the same project id on every export, not a
        # fresh one each run. Added 2026-08-22 alongside the default
        # camera above, for the same reason: a real Forma import
        # reported the export as "empty", and this file being
        # genuinely optional in the spec doesn't mean every consumer
        # tolerates its absence in practice.
        project_guid = _deterministic_guid("project", model_title)
        zf.writestr("project.bcfp", _project_bcfp_xml(project_guid, model_title))
        for issue in issues:
            topic_guid = _deterministic_guid(issue.issue_id)
            # A Viewpoint (and the Component it pins) only makes sense
            # when there's something to select -- a coverage finding
            # with no element still deserves a Topic (the round trip
            # should show the whole issue list, same as to_json/
            # to_markdown), it just doesn't get a pin.
            has_target = issue.unique_id is not None or issue.element_id is not None
            vp_guid = _deterministic_guid(issue.issue_id, "viewpoint") if has_target else None
            zf.writestr(
                "{0}/markup.bcf".format(topic_guid),
                _markup_xml(issue, topic_guid, vp_guid, created_at, author),
            )
            if vp_guid is not None:
                zf.writestr(
                    "{0}/viewpoint.bcfv".format(topic_guid),
                    _viewpoint_xml(issue, vp_guid),
                )
    return buffer.getvalue()


def to_bcf_files(
    issues: Iterable[Issue],
    model_title: str = "",
    max_issues_per_file: int = DEFAULT_MAX_ISSUES_PER_FILE,
    author: str = "RevitCheck",
    created_at: Optional[str] = None,
) -> List[Tuple[str, bytes]]:
    """Every issue, as one or more `.bcfzip` files of at most
    `max_issues_per_file` topics each.

    Returns `[(filename, bytes), ...]` rather than writing to disk —
    same reasoning as `report.to_json`/`to_markdown` staying pure
    functions: a pyRevit button decides where files go (it already has
    a save-dialog pattern for that), and this stays testable without
    touching a filesystem. `sort_issues`' sheet-major ordering is kept,
    so which chunk a finding lands in is stable and predictable rather
    than dependent on rule-run order.
    """
    ordered = sort_issues(list(issues))
    if not ordered:
        return []

    when = created_at or datetime.datetime.now(datetime.timezone.utc).strftime(
        "%Y-%m-%dT%H:%M:%SZ"
    )
    base_name = _slugify(model_title)

    chunks = [
        ordered[i : i + max_issues_per_file]
        for i in range(0, len(ordered), max_issues_per_file)
    ]

    files = []
    for index, chunk in enumerate(chunks, start=1):
        if len(chunks) == 1:
            filename = "{0}.bcfzip".format(base_name)
        else:
            filename = "{0}-{1:03d}-of-{2:03d}.bcfzip".format(
                base_name, index, len(chunks)
            )
        files.append((filename, _write_bcfzip(chunk, model_title, when, author)))
    return files
