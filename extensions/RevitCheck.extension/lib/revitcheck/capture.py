"""Save a `RevitModel` to JSON and load it back.

**This is the development workflow, not a convenience feature.** Revit
is Windows-only and the machine it runs on here is locked down, while
the code gets drafted somewhere with no Revit at all. Without this, a
rule can only be exercised where it cannot comfortably be written.

With it, the loop is the one this repo already uses successfully for
PDF and DXF: capture a real project once, commit the JSON as a sample,
and develop and test every rule against real data offline. It is the
direct equivalent of `samples/BR06/dxf/` being committed so tests don't
need ODA File Converter installed.

Two consequences worth keeping in mind when extending the IR:

- **A capture must stay valid when a classification rule changes.**
  That is why `ir.py` stores raw facts and `checks/` does the judging.
  Retuning what counts as "drafted" must never require going back to a
  Revit machine for a fresh capture.
- **Captures may contain client geometry.** Coordinates, sheet numbers
  and view names all travel in the file. Treat a capture the way the
  rest of this project treats uploaded drawings (PLANNING.md §2's
  stateless-by-design reasoning); don't commit one from a live job
  without checking that is acceptable.
"""

from __future__ import annotations

import json
from dataclasses import asdict
from typing import Any, Dict, Optional

from revitcheck.ir import (
    DimensionInfo,
    DimensionSegmentInfo,
    Point3D,
    ReferenceInfo,
    RevitModel,
    SheetInfo,
    ViewInfo,
)

# Bumped when a change to the IR would make an older capture load
# wrongly rather than merely incompletely. Additive fields with
# defaults do not need a bump; a renamed or re-meaning'd field does.
SCHEMA_VERSION = 1


def to_dict(model: RevitModel) -> Dict[str, Any]:
    payload = asdict(model)
    payload["schema_version"] = SCHEMA_VERSION
    return payload


def dumps(model: RevitModel, indent: Optional[int] = 2) -> str:
    return json.dumps(to_dict(model), indent=indent, sort_keys=True)


def save(model: RevitModel, path: str) -> str:
    with open(path, "w") as handle:
        handle.write(dumps(model))
    return path


def _point(data: Optional[Dict[str, Any]]) -> Optional[Point3D]:
    if not data:
        return None
    return Point3D(x=data["x"], y=data["y"], z=data["z"])


def from_dict(data: Dict[str, Any]) -> RevitModel:
    version = data.get("schema_version", SCHEMA_VERSION)
    if version > SCHEMA_VERSION:
        raise ValueError(
            "capture uses schema version {0}, this build understands up to "
            "{1} — update revitcheck before loading it".format(
                version, SCHEMA_VERSION
            )
        )

    sheets = [SheetInfo(**s) for s in data.get("sheets", [])]
    views = [ViewInfo(**v) for v in data.get("views", [])]

    dimensions = []
    for raw in data.get("dimensions", []):
        dimensions.append(
            DimensionInfo(
                element_id=raw["element_id"],
                view_id=raw["view_id"],
                is_spot=raw.get("is_spot", False),
                references=[
                    ReferenceInfo(**r) for r in raw.get("references", [])
                ],
                segments=[
                    DimensionSegmentInfo(**s) for s in raw.get("segments", [])
                ],
                origin=_point(raw.get("origin")),
                type_name=raw.get("type_name"),
            )
        )

    return RevitModel(
        doc_title=data.get("doc_title", ""),
        sheets=sheets,
        views=views,
        dimensions=dimensions,
        revit_version=data.get("revit_version"),
        captured_at=data.get("captured_at"),
        extraction_errors=list(data.get("extraction_errors", [])),
    )


def loads(text: str) -> RevitModel:
    return from_dict(json.loads(text))


def load(path: str) -> RevitModel:
    with open(path) as handle:
        return loads(handle.read())
