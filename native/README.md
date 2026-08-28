# RevitCheck native add-in

The C# native Revit add-in. `extensions/RevitCheck.extension` (pyRevit) is
frozen — no further feature work there (PLANNING.md §12) — this is where new
work happens.

See the plan the metadata reconciliation feature was built from for the full
design reasoning: `~/.claude/plans/the-first-button-we-abundant-platypus.md`
(or ask for it to be copied into this repo if that path won't be around
later).

## Layout

```
native/
  RevitCheck.sln
  Directory.Build.props           # shared LangVersion/Nullable across every project
  src/
    RevitCheck.Core/               # netstandard2.0 - NO Revit API reference, no
                                    #   runtime dependency. Everything here is
                                    #   plain, pure, and testable with zero Revit.
      Ir/                          # RevitModel + everything it carries:
                                    #   ElementMetadata/ParameterValue (metadata
                                    #   reconciliation), SheetInfo/ViewInfo/
                                    #   DimensionInfo/DimensionSegmentInfo/
                                    #   ReferenceInfo/Provenance (dimension checks,
                                    #   ported from ir.py). One capture, growing.
      Capture/                     # JSON save/load - mirrors capture.py
      Issues/                      # Issue + IssueSorting - mirrors issue.py
      Catalog/                     # Catalog + CheckRegistry - mirrors catalog.py
      Mapping/                     # ParameterMapping ("the export file taken
                                    #   once and saved") + its serializer
      Csv/                         # CSV-only reference-data reader (CsvHelper)
      Checks/
        RuleConfig.cs                        # dimension-check tolerances/scoping,
        DimensionProvenanceOptions.cs         #   ported from catalog.py's RuleConfig
        DimensionClassification.cs           # classify_reference/classify_dimension
        ViewScoping.cs                        # views_in_scope and its helpers
        DimensionDescriptions.cs             # shared wording helpers
        DimensionProvenanceCheck.cs           # revit.dimension_provenance
        DimensionOverrideConsistencyCheck.cs  # revit.dimension_override_consistency
        MetadataReconciliationCheck.cs        # revitcheck.metadata_reconciliation
        ReconciliationConfig.cs
        CaptureCoverageCheck.cs               # revitcheck.capture_coverage
      Polyfills/                   # IsExternalInit / required-member shims
                                    #   netstandard2.0 needs for modern C#
      Reporting/                   # IssueJsonWriter (issues -> JSON, the
                                    #   complete/lossless record) +
                                    #   IssueCsvWriter (same data,
                                    #   spreadsheet-friendly - flattens the
                                    #   SuggestedFix keys this project's
                                    #   rules actually use) + IssueGrouping
                                    #   (not a full report.py port - see
                                    #   IssueJsonWriter's docstring) +
                                    #   IssueBcfWriter (issues -> BCF 2.1,
                                    #   a line-for-line port of bcf.py -
                                    #   fully off-Revit, testable and
                                    #   tested without the Revit machine)
    RevitCheck.Addin/               # net48 (Revit 2024) - the ONLY project that
                                    #   references the Revit API. Compiles cleanly
                                    #   on this Mac (Nice3point.Revit.Api.* NuGet
                                    #   packages). Wired up as of 2026-08-24 -
                                    #   see "What's built" below.
      Adapters/
        RevitMetadataElementSource.cs  # the metadata adapter - reads
                                        #   category/family/parameters off a
                                        #   live Document, judges nothing
        RevitDimensionSource.cs        # the dimension/sheet/view adapter -
                                        #   line-for-line port of
                                        #   revit_source.py's
                                        #   _collect_dimensions/
                                        #   _collect_sheets_and_views,
                                        #   including the per-view
                                        #   OwnerViewId fix
      Commands/
        MetadataReconciliationCommand.cs  # IExternalCommand: prompts for a
                                           #   mapping file + CSV (per run -
                                           #   see its docstring for why),
                                           #   runs the check, writes results
        CaptureModelCommand.cs            # IExternalCommand: writes a full
                                           #   model sweep (metadata +
                                           #   sheets/views/dimensions) to a
                                           #   CaptureSerializer JSON file -
                                           #   this side's dev-loop capture,
                                           #   not a live sync (see its
                                           #   docstring)
        DimensionProvenanceCommand.cs           # IExternalCommand:
                                                 #   revit.dimension_provenance
        DimensionOverrideConsistencyCommand.cs  # IExternalCommand:
                                                 #   revit.dimension_override_consistency
        ExceptionMessage.cs               # shared FullMessage(Exception)
                                           #   helper - walks the
                                           #   InnerException chain into a
                                           #   readable TaskDialog message,
                                           #   used by all four commands
        IssueOutput.cs                     # shared WriteNextToModel(doc,
                                            #   issues, kind, dialogTitle)
                                            #   helper - prompts for a save
                                            #   location (SaveFileDialog,
                                            #   same UX as Capture Model),
                                            #   then writes JSON + CSV + BCF
                                            #   sharing whatever base name
                                            #   the user picked; used by all
                                            #   three check-producing
                                            #   commands
        DocumentPaths.cs                   # SafeBaseName(doc) - a safe
                                            #   suggested filename for that
                                            #   dialog, tolerating
                                            #   doc.PathName/doc.Title
                                            #   throwing (cloud-worksharing
                                            #   models) rather than just
                                            #   parsing badly
      RevitCheckApplication.cs      # IExternalApplication.OnStartup - creates
                                     #   the RevitCheck ribbon tab/panel
      RevitCheck.addin              # the manifest Revit actually loads
      Resources/Icons/               # ribbon icons (32/16px PNG + 256px
                                      #   masters under source/)
      Polyfills/                    # same shim pattern as Core's, needed
                                     #   again because it's a separate
                                     #   assembly (net48, not netstandard2.0)
  tools/
    RevitCheck.MappingBuilder/      # net8.0 console app, no Revit refs. Builds a
                                    #   starter mapping file from a real capture +
                                    #   a real CSV's headers - off-machine, the
                                    #   analogue of scripts/check_capture.py for
                                    #   metadata reconciliation's setup step.
    RevitCheck.CheckRunner/         # net8.0 console app, no Revit refs. Loads a
                                    #   .capture.json and runs
                                    #   revit.dimension_provenance +
                                    #   revit.dimension_override_consistency
                                    #   against it - the actual off-machine
                                    #   analogue of scripts/check_capture.py,
                                    #   filling the gap MappingBuilder's own
                                    #   comment didn't (it builds a mapping
                                    #   file, it doesn't run a check).
  diagnostics/
    InspectElements.pushbutton/     # ONE-OFF Revit-machine diagnostic (not part
                                    #   of the frozen extension) - dumps a
                                    #   selection's full identity/parameter set to
                                    #   JSON. See diagnostics/README.md.
  tests/
    RevitCheck.Core.Tests/          # xUnit, 170 tests
      Fixtures/                     #   synthetic-IR builders + the real-capture
                                     #   parity fixture (see below)
    RevitCheck.MappingBuilder.Tests/ # xUnit, 7 tests
```

RevitCheck.Addin has no test project, deliberately - like `adapters/revit_source.py` on the
Python side, its Revit-API-calling code (`Adapters/`, `Commands/`,
`RevitCheckApplication.cs`) cannot be exercised without a real `Document`, so
no test will ever touch it. `dotnet build` (0 warnings/errors) is the only
automated check it gets; real verification happens on the Revit machine
(see "What's not done" below).

## Building and testing

Requires the .NET 8 SDK. If it's not on PATH:

```
export PATH="$HOME/.dotnet:$PATH"   # if installed via dotnet-install.sh to ~/.dotnet
```

```
cd native
dotnet build          # whole solution, including the net48 Addin (RevitAPI
                       # comes from the Nice3point.Revit.Api.* NuGet packages -
                       # no Revit install needed to compile, only to run)
dotnet test           # 170 + 7 = 177 tests, zero Revit involved
```

Both are expected to be clean (0 warnings, 0 errors; all tests passing) at all
times - this mirrors the Python side's `pytest` loop running in a tenth of a
second with no Revit present.

### Running the mapping-build tool

```
dotnet run --project tools/RevitCheck.MappingBuilder -- \
    <capture.json> <reference.csv> <key-parameter-name> <output-mapping.json>
```

Writes a starter `ParameterMapping` JSON file: exact (case-insensitive)
column/parameter-name matches are auto-filled; anything else is left out of
`fields` and printed to the console instead, along with the real parameter
names present per distinct (category, family) pair in the capture - so a
human resolves the hard, family-varying cases against a curated shortlist,
not a blind guess. **The tool never writes an `overrides` entry itself.**
Every auto-matched numeric field is deliberately left with no `tolerance_mm`,
which means `ParameterMappingSerializer.Load` will refuse to load the file
until a human has actually reviewed it and filled that in - a real gate, not
just a comment asking nicely.

### Running the dimension checks against a capture, off-Revit

```
dotnet run --project tools/RevitCheck.CheckRunner -- <capture.json> \
    [--json out.json] [--csv out.csv] [--bcf out-dir] [--all-views] [--rule rule-id]...
```

Runs `revit.dimension_provenance` + `revit.dimension_override_consistency`
(plus `revitcheck.capture_coverage`) against any `.capture.json` - one
written by `Capture Model`, or the JSON a check command itself writes, which
is a superset - and prints the same shape of summary the ribbon buttons'
`TaskDialog` shows, plus the list of fully-drafted views
(`DimensionProvenanceCheck.DraftedViews`). `--json`/`--csv`/`--bcf` write the
same three formats `IssueOutput.WriteNextToModel` does. Built 2026-08-25 to
close a real gap: `RevitCheck.MappingBuilder` above only builds a mapping
file, so until this existed there was no way to see what the dimension
checks say about a real capture without a trip back to the Revit machine -
exactly the two-machine friction `scripts/check_capture.py` exists to remove
on the Python side. First real run, against the real capture the user
uploaded the same day (`samples/T2DPAA-T2D-C3S-BR-M3D-100302.capture.json`,
59 sheets/833 views/538 dimensions/0 extraction errors) - see PLANNING.md
§14 for the counts and two override findings worth a second look.

## What's built

### Metadata reconciliation (Phases 0-6)

Joins captured model elements against an external CSV of reference data via a
key parameter and a persisted mapping file that resolves family-variant
parameter names. `MetadataReconciliationCheck` implements the full join/
compare logic: numeric-with-tolerance and case-insensitive-exact-string
comparisons, family/category-based parameter-name overrides, a nested
sub-component reconciled completely independently of its host, an element
with a key but no CSV match reported by default (the "missing item" signal),
a genuinely blank model value against CSV data reported as a first-class
mismatch (the "incorrectly filled" signal) rather than a coverage note, and
zero noise from a 1000+ row whole-of-project CSV against a 20-30 element
model.

**Element sweep is scoped to a curated view, not the whole document** -
found necessary on the first real Revit-machine run (2026-08-24): the
category scope alone (Floors/Generic Models/Structural
Connections/Foundations/Framing) matched far more of the model than the
intended trackable set, since those categories exist all over a real
project. `ParameterMapping.ScopeViewName` names a view (both real mappings
use `"NavisworksExport"`, the view the user's other tools already treat as
the source of truth for which elements are tracked) whose visible elements
`RevitMetadataElementSource.Collect` sweeps instead - `FilteredElementCollector(doc, view.Id)`. Left unset, a mapping falls back to the
original whole-document-within-category-scope sweep; the adapter fails
loudly (not a silent fallback) if a named view doesn't exist.

### Dimension checks, ported from Python (`checks/dimensions.py`)

`revit.dimension_provenance` and `revit.dimension_override_consistency`,
ported rule-by-rule against the existing Python behaviour as the spec to
match (PLANNING.md §12's own instruction), including the IR they run on
(`SheetInfo`/`ViewInfo`/`DimensionInfo`/`ReferenceInfo`/`Provenance`) and
`RuleConfig`. All 81 Python test functions (`test_dimension_provenance.py` +
`test_dimension_overrides.py`) were ported test-by-test, not reinvented.

**Verified against real data, not just synthetic tests**: `RealCaptureParityTests`
runs both ported rules against the one real capture committed to the repo
(`samples/T2DPAA-T2D-C3S-BR-M3D-100304_Peter.capture.json`) and asserts the
result against a fixture (`Fixtures/real_capture_expected_issue_ids.txt`)
captured once from the actual Python engine's output on the same file
(`python3 scripts/check_capture.py <file> --json`) — **every one of 957
issue_ids matches exactly**, not just the aggregate counts. Getting there
found and fixed two real translation bugs neither synthetic tests nor a
human read-through caught:

- An em-dash (—) silently ported as a plain hyphen (-) in several
  description strings — same text to a human eye at a glance, different
  SHA-256 identity hash.
- A naive string-quoting substitute for Python's `repr()` in the unparsed-
  override-forms coverage message, which would have embedded a raw invisible
  Unicode character (a real override on this capture is a lone U+200E) into
  the description instead of escaping it visibly the way Python's repr does
  (`PythonRepr` in `DimensionOverrideConsistencyCheck.cs`).

This is exactly the translation risk PLANNING.md §12 named up front (float
formatting, iteration order, string handling) — real data caught what
review alone didn't.

### The Addin project (net48) genuinely compiles on this Mac

Against real `Autodesk.Revit.DB`/`.UI` types, with no Revit installed, via
the `Nice3point.Revit.Api.*` NuGet packages — confirmed with a throwaway
sanity file during the build, not just assumed.

## Wired up and proven on the Revit machine (2026-08-24)

The metadata-reconciliation path is fully wired end to end, deployed, and
has run for real against the full model - see "First real runs found real
bugs" below for what that surfaced and fixed.

- **`Adapters/RevitMetadataElementSource.cs`** - the metadata adapter.
  Category scope, key-parameter identity, and nested-sub-component shape
  (all three of the former "open questions" below) came from real data via
  `diagnostics/InspectElements.pushbutton`'s 71-element export, not a
  guess - see the class's own remarks for specifics.
- **`Commands/MetadataReconciliationCommand.cs`** - the `IExternalCommand`.
  Prompts for the mapping file and the reference CSV **on every run**
  (`Microsoft.Win32.OpenFileDialog`), confirmed with the user 2026-08-24:
  even within one client, individual models vary enough that a per-run
  picker is what actually works across all of them, rather than a path
  baked into config that quietly stops matching one project. Neither file
  is written back into this repo - both are client asset data (the old
  open question #4, below). Results are grouped (`IssueGrouping`, below)
  then written as both JSON (`IssueJsonWriter`, the complete record) and
  CSV (`IssueCsvWriter`, for reviewing in a spreadsheet - added 2026-08-24
  at the user's request) next to the model file, plus a `TaskDialog`
  summary.
- **`Commands/CaptureModelCommand.cs`** - writes the metadata sweep to a
  `CaptureSerializer` JSON file (a point-in-time snapshot, not a live
  sync). This add-in's counterpart to the Python side's Capture Model
  button and its whole dev-loop role: build output/reporting logic (e.g.
  grouping by family/type/field/values) against real element diversity off
  the Revit machine, without a round trip for every change. Prompts for a
  mapping file the same way `MetadataReconciliationCommand` does, but only
  to read its `ScopeViewName` - fields and any CSV aren't used here.
- **`RevitCheckApplication.cs`** - `IExternalApplication.OnStartup`,
  creates the "RevitCheck" ribbon tab/panel with both buttons (the icons
  under `Resources/Icons/`).
- **`RevitCheck.addin`** - the manifest. Deploys to the shared
  `Addins\2024\` folder itself, while the DLL and its dependencies go one
  level down in their own `RevitCheck\` subfolder - see "Next: prove it on
  the Revit machine" below for why that split, not everything in one
  place.

`dotnet build` is clean (0 warnings/errors) including all of the above,
against the real `Autodesk.Revit.DB`/`.UI` types via the Nice3point
packages. **Nothing here has run inside actual Revit yet** - that's the
real gate (see "Next: prove it on the Revit machine" below), not the
compile.

Deliberately scoped to *one* button: the dimension/sheet/view adapter (for
`revit.dimension_provenance` / `revit.dimension_override_consistency`) is a
separate, later phase - proving the wiring itself on the simpler case
first, rather than shipping every check's plumbing at once and debugging
all of it on the Revit machine simultaneously.

### Deploying to a Revit machine

Revit only scans `*.addin` files directly in
`%APPDATA%\Autodesk\Revit\Addins\2024\` (not subfolders), and that folder
already holds other add-ins' manifests - so the deploy is split in two,
confirmed with the user 2026-08-24:

1. Copy `RevitCheck.addin` itself directly into
   `%APPDATA%\Autodesk\Revit\Addins\2024\`, alongside whatever's already
   there.
2. Copy everything else from `bin/Debug/net48/` - `RevitCheck.Addin.dll`,
   `RevitCheck.Core.dll`, `CsvHelper.dll`, the `System.Text.Json` family,
   `Resources\Icons\` - into a new `RevitCheck\` subfolder one level under
   that (`...\Addins\2024\RevitCheck\`). Leave out the RevitAPI/RevitAPIUI
   assemblies if present; Revit supplies those itself. The manifest's
   `<Assembly>` path (`.\RevitCheck\RevitCheck.Addin.dll`) already points
   there.
3. Launch Revit, confirm the "RevitCheck" tab/"Checks" panel appears with
   both buttons showing their icons.

### First real runs found real bugs, all since fixed

Running Metadata Reconciliation against the full real model (both real
mappings) found three genuine bugs, not just theoretical risk - real data
finding real problems is exactly why this deploy step exists rather than
trusting the compile:

1. **Category-only scoping swept far more than intended** (1644+ elements
   vs. the curated view's 861) - fixed by `ParameterMapping.ScopeViewName`,
   scoping to a curated view instead of the whole document.
2. **`ComparisonType.ContainsCsvValue`** - a model value that's
   legitimately a semicolon-separated list (an element belonging to more
   than one group) was being compared for exact equality against a CSV
   that only ever records one entry.
3. **`ReconciliationConfig.BlankKeySentinels`** - a key parameter literally
   holding `"N/A"` was being looked up as a real key instead of treated
   like blank.

After those fixes, **both real mappings' issue counts are understood and
real** (Location Referencing 296, Asset Classification 151) - not tool
bugs. One investigation into Asset Classification's cluster looked like it
needed a fourth fix (`ParameterMapping.DisambiguationField`, for CSVs that
carry multiple rows per key) before checking the real CSV directly showed
that wasn't the actual cause - see that field's own docstring and the
mapping file's `_note` for the real one (a genuine wrong-identifier error
in the model). Worth remembering generally: **a large mismatch count on a
real run is not automatically a bug in the check** - verify a fix against
the real data before shipping it, not just against how plausible it
sounds.

### A second real run found a fourth bug: cloud-worksharing paths (2026-08-25)

Running Metadata Reconciliation against a real **cloud-shared** model
(Revit Cloud Worksharing - BIM 360 / Autodesk Construction Cloud /
Autodesk Docs) crashed writing the output file: `NotSupportedException:
The given path's format is not supported.` Cause: `Document.PathName` for
a cloud model isn't a filesystem path at all - it's a URN-shaped string
(`"BIM 360://ProjectName/Model.rvt"`, or similarly for Autodesk Docs). The
existing code only guarded against `PathName` being *empty* (a doc never
saved locally); a cloud model's `PathName` is non-empty and genuinely
unparseable. .NET Framework's `Path.GetDirectoryName`/
`GetFileNameWithoutExtension` throw on the `"://"` - any colon outside the
drive-letter position is invalid to the classic Windows `Path`
implementation the add-in actually runs under (net48 inside Revit); this
did **not** reproduce on this Mac's .NET 8, whose cross-platform `Path`
implementation is far more lenient about the same string - a real
platform-behaviour gap worth knowing before trusting a `Path.*` sanity
check run anywhere but the actual net48/Windows target.

**First fix attempt didn't clear it.** The first `DocumentPaths.cs`
wrapped `Path.GetDirectoryName`/`GetFileNameWithoutExtension` in a
try/catch, but read `doc.PathName` itself as a bare, unguarded argument
expression at the call site. Deployed and re-run: identical error, on
Metadata Reconciliation *and* the brand-new Dimension Provenance/
Overrides (which had never touched this code before - rules out a
stale-build explanation). The real failure sits one layer earlier than
assumed: either `PathName`'s property getter itself throws for a cloud
model, or something downstream of the parse that wasn't wrapped either -
genuinely couldn't pin down which without a Windows/net48 machine to test
against directly.

**Fixed properly (second pass): sidestep the question entirely.** Rather
than keep guessing which exact call throws, the three check commands
(Metadata Reconciliation, Dimension Provenance, Dimension Overrides) now
**prompt for a save location via dialog** - the same `SaveFileDialog` UX
`CaptureModelCommand` already had. Requested by the user directly, having
seen Capture Model's dialog and asked why the others didn't have one - it
turned out to also be the more robust fix, since it never needs to touch
`doc.PathName` at all. `DocumentPaths.cs` now only supplies a *suggested*
filename for that dialog, with every property read (`PathName`, `Title`)
wrapped individually. `dotnet build` clean, all 243 tests still pass
(Addin-side, untestable off Revit like the rest of that layer) - still
needs the Revit machine to confirm this clears the real cloud-model run,
but the design no longer depends on correctly guessing Revit's cloud-path
behaviour at all.

### Output/reporting: grouping - built and validated against real data

`Core/Reporting/IssueGrouping.cs` collapses many mismatch issues that
share the same (category, family, type, field, model value, csv value)
into one, with an affected-element count and a truncated id sample - the
same "a wholly-drafted view is one finding, not twenty" precedent
`revit.dimension_provenance` already established, confirmed as a
requirement by the user 2026-08-24. Deliberately a separate reporting
step, not built into `MetadataReconciliationCheck.Run` itself - the
check's own contract stays one issue per (element, field) finding (what
every existing test assumes, and what a single-occurrence element anchor
still needs); `MetadataReconciliationCommand` applies grouping only to
what it writes out.

Validated against real data via `Commands/CaptureModelCommand.cs`'s
capture + the real mapping/CSV files, run locally (no Revit machine
needed for this iteration) - reproduced the user's real counts exactly,
then grouped: **Location Referencing 296 → 19**, **Asset Classification
151 → 15**. 207 tests passing (200 + 7 covering `IssueGrouping`
specifically).

### Next

**The dimension/sheet/view adapter (Track A of PLANNING.md §14's plan) is
now built (2026-08-25)** — `Adapters/RevitDimensionSource.cs` (a
line-for-line port of `revit_source.py`'s `_collect_dimensions`/
`_collect_sheets_and_views`/`read_model`, including the documented
`OwnerViewId` per-view-collection fix), `Commands/DimensionProvenanceCommand.cs`
+ `Commands/DimensionOverrideConsistencyCommand.cs`, both ribbon buttons
wired in the confirmed order (Capture Model → Dimension Provenance →
Dimension Overrides → Metadata Reconciliation), and `CaptureModelCommand`
extended to capture sheets/views/dimensions alongside metadata in one file.
`dotnet build` is clean (0 warnings/errors) and all 218 existing tests
still pass.

**The adapter is now validated against a real document (2026-08-25, same
day)** — the user ran the updated Capture Model button against the real
cloud-worksharing model: 59 sheets, 833 views, 538 dimensions, 0
extraction errors. `RevitDimensionSource.cs` collecting real data cleanly
on the first try is the specific thing this section previously flagged as
unproven. What that run does *not* cover: it predates, and so doesn't
test, the §15 second-pass save-dialog fix (above) in the other three
commands — Capture Model already had a working `SaveFileDialog`, so this
run never touched the new dialog path. See PLANNING.md §14 for the full
detail, and "Running the dimension checks against a capture, off-Revit"
above for what the two dimension checks actually found running against it
locally via the new `RevitCheck.CheckRunner` tool.

**BCF export is now wired into all three check-producing commands
(2026-08-25)** — `Core/Reporting/IssueBcfWriter.cs` is a line-for-line
port of the Python engine's `bcf.py` (same deterministic-Topic-Guid
scheme via a hand-rolled RFC 4122 UUIDv5, since `System.Guid` has no
built-in equivalent to `uuid.uuid5` — verified byte-for-byte against real
`python3 -c "import uuid; ..."` output, not just against the RFC).
Genuinely no Revit needed to build *or* verify this one: 25 tests ported
from `test_bcf.py` (found and didn't reproduce a latent indexing bug in
two of that suite's own tests along the way — see
`IssueBcfWriterTests`'s remarks) plus 3 UUIDv5 parity fixtures, all
passing off-Revit. `Commands/IssueOutput.cs` (factored out of the three
commands' near-identical JSON+CSV writers, the same "shared helper once a
third caller needs it" call `ExceptionMessage` already made) now writes
JSON, CSV, and BCF side by side for every run of Metadata Reconciliation,
Dimension Provenance, and Dimension Overrides — `dotnet build` clean,
236 Core tests passing. **Still needs the Revit machine to confirm the
BCF files it produces actually import into Forma**, same as everything
else added since the metadata-reconciliation round trip was proven
(PLANNING.md §12); the wiring itself needed no live document to write or
test.

**`revitcheck.pile_model_schedule_consistency` — the model-vs-schedule pile
check named in PLANNING.md §14 is now built and tested (2026-08-26, one
same-day correction), Core-side only.** `InspectPileSetout.pushbutton`'s
real run answered the five open questions (PLANNING.md §14 update,
2026-08-26): the join is `ElementMetadata.Parameters["DIT_SiteID"]` against
the schedule's `SITE ID` column. **Two real corrections landed on top of
each other before this was right, both worth keeping in mind for future
similar checks:**

1. An earlier read of the same diagnostic output treated `DIT_StartEasting`/
   `DIT_StartNorthing` (identical across every pile sampled) as stale
   per-pile data — the user corrected this directly: it's a deliberate
   convention giving the *bridge's own centre point* (matching the sheet
   title-blocks' lat/long), not a pile position.
2. The fix for #1 pointed the check at `XYZ_Easting`/`XYZ_Northing`
   instead — which turned out to be a second, more consequential mistake.
   Those parameters are themselves written by the *same* Dynamo script
   that (re)writes the schedule, from the insertion point at the time it
   last ran. Comparing the schedule against them is comparing the same
   stale value to itself: move a pile without rerunning Dynamo and both
   sides stay frozen in agreement, exactly the failure this check exists
   to catch. The user caught this too, directly. **So this check does
   need a geometry-API call after all** — the "no adapter geometry work
   needed" framing an earlier version of this section had was wrong.

Fixed: `ElementMetadata` gained `ProjectPositionEastingMm`/`NorthingMm`/
`ElevationMm` (new Core IR, populated only by a live `ProjectLocation.
GetProjectPosition` call at capture time — real, still-unbuilt Addin-side
geometry work, the genuinely new territory PLANNING.md §14's Track B
always expected here), and the check compares those against the schedule
instead of any parameter. `RuleConfig.PileEastingParameterName`/
`PileNorthingParameterName` were removed rather than left as a misleading
unused knob. `Checks/PileModelScheduleConsistencyCheck.cs` is otherwise
structured like `MetadataReconciliationCheck`'s join (ambiguous/missing/
blank-key cases all their own coverage finding, never guessed) but
comparing two fixed numeric fields with a planar-distance tolerance
instead of an arbitrary mapped field set. A new regression test proves
the fix directly: a pile whose live position has moved 500mm while its
`XYZ_Easting`/`XYZ_Northing` (and the schedule, which agrees with them,
exactly as it would for real) stayed frozen at the old position is still
correctly flagged. 9 pile tests, real-number-shaped (the actual
`PIL232132`/`PIL232133` figures from the diagnostic run, not invented
ones) — `dotnet test` clean, 245 Core tests passing.

**Not yet built:** the Addin-side adapter — now genuinely needs to call
`ProjectLocation.GetProjectPosition(pile.Location.Point)` per pile (not
just read parameters, per the correction above), plus `DIT_SiteID` via the
existing `RevitMetadataElementSource` pile-category scope, plus a new
schedule-reading piece handling the real header/blank-row quirk
`GetCellText(SectionType.Body, ...)` has on this project — and its ribbon
command/button, per-element-type, own button, not folded into an existing
one (PLANNING.md's Track B button-per-type direction). Needs the Revit
machine to build and confirm, same as every Addin-side piece.

Also next: Track B's other, harder half — verifying a *drawing-vs-schedule*
pile check (PLANNING.md §14, CLAUDE.md's "Next"), the direct port of the
old PDF/DWG pipeline's `geometry.setout_reconstruction` bearing/
dimension-chain walk. A same-day `InspectDimensionGeometry.pushbutton`
re-run confirmed numerically why this one can't take the *model*-vs-schedule
check's shortcut (a single `GetProjectPosition` call per pile, no dimension
data involved at all): 87 of 92 references across the whole pile layout
view resolve to `AnnotationSymbol` (tags), 5 to `Grid`, zero to any model
geometry — pile setout dimensions here measure tag-to-tag, so there's no
`CUT_EDGE`/model reference to read a position from directly, and the
reconstruction algorithm (PLANNING.md §5b, ARCHIVE-pdf-dwg.md) is the
proven way to derive one anyway.

**A third, more direct approach was also raised the same day (2026-08-26)
and is worth building instead of, or ahead of, the full §5b port:** since
`ReferenceInfo` already carries `ElementId`/`Category`/`ViewSpecific` per
dimension reference, extending it with the reference's own resolved
location and matching it to the nearest real `Pile` element (2D
plan-distance only — a real check of the committed diagnostic data found
an `AnnotationSymbol` reference's Z sitting at a symbolic ~200,000mm
annotation-plane value against a real pile Z of ~18,500mm, ruling out 3D
matching) would let a dimension's *own* stated value be checked directly
against the measured distance between two real piles - no schedule, no
bearing/DMS parsing, no chain walk. Two real risks flagged, neither
validated yet: whether the nearest pile to a tag's location is actually
the *right* pile, and whether a tag is placed at/near its own pile at all
rather than leader-offset (which would make tag-to-tag and pile-to-pile
distances disagree even when everything is correct - the same failure
shape `InspectDimensionGeometry` run 4 already hit once). **Built the same
day (2026-08-26):** `InspectDimensionGeometry.pushbutton` extended with
`_collect_piles`/`_nearest_pile` (2D X/Y only, per the real Z gap above)
and a per-dimension `pile_match` block reporting real pile-to-pile
distance next to the dimension's own value. **Refined the same day, before
the first run, per the user's own suggestion:** `_collect_piles` is
scoped to the active view (`FilteredElementCollector(doc, view.Id)`), not
a document-wide sweep - avoids false-matching against foundation
instances from unrelated structures elsewhere in the model, and reuses
the same per-view scoping the dimension collection already relies on.
Not yet run - needs the Revit machine.

**Stage 3 (reconciliation + export) design started, same day, ahead of the
check above being run - the mechanism itself doesn't need real client
data to be correct.** `Core/Reporting/InvestigationReconciliation.cs`
(new, tested) prunes dimension triage
(`revit.dimension_provenance`/`revit.dimension_override_consistency`)
against a per-dimension investigation check's verdicts, so BCF export
(once wired up) only ever carries confirmed problems. Real design points
worth knowing:
- The investigated-scope has to be a parameter separate from the
  investigation issues list, not derived from it - a check reports
  "clean" by emitting nothing, identical to "never examined," so treating
  absence-of-issue as confirmed-clean would silently suppress an
  unverified triage finding.
- Most real triage volume is `DimensionProvenanceCheck`'s view-rollup
  issue, anchored on the view's ElementId, which can never match a
  per-dimension verdict directly - needed one small additive fix first
  (`ViewRollupIssue`'s `SuggestedFix` now carries `drafted_dimension_ids`,
  not just counts) so reconciliation can "un-roll" it and only drop the
  whole rollup once every one of its underlying dimensions has been
  examined.
- **Three outcomes, not two - refined the same day per the user's own
  direction.** `Reconcile` returns a `ReconciliationResult`
  (`ConfirmedProblems`/`NeedsManualReview`/`StillOpenTriage`), not one
  flat list. Some dimensions genuinely need drawing interpretation a
  script can't do (an ambiguous nearest-pile match, a tag too far from any
  real pile to trust) - a future investigation check marks those with
  `Category = InvestigationReconciliation.ManualReviewCategory` instead
  of being forced to guess clean-or-problem. Only `ConfirmedProblems` is
  meant for automatic BCF export; `NeedsManualReview` is a separate,
  shorter list a human decides on. The rollup un-rolling logic needed no
  change for this - it already only asked "was this dimension examined,"
  not "was it found clean," so a manual-review verdict counts the same as
  clean or problem for resolving a rollup.

`revitcheck.pile_model_schedule_consistency` deliberately doesn't
participate - keyed on Pile ElementIds, never a Dimension's, so it
naturally never matches anything here; it should get its own
`writeBcf: true` command once wired up, not route through this.

**The pile-proximity investigation check is no longer just diagnostic-only
- a view-scoping fix unblocked a real re-run (2026-08-26), and the result
decisively validated the whole approach.** `_collect_piles` had been
sweeping the whole document (281 "piles" found against a real ~47) -
fixed per the user's own suggestion to `FilteredElementCollector(doc,
view.Id)`, matching real data down to 43. Re-run on the real pile layout
view: **31 of 32 matched dimensions agree with real pile geometry to
sub-millimetre precision** (many literally floating-point-exact),
decisively confirming both that the nearest-pile match is correct and
that tags sit at their own pile's location, not leader-offset. The one
real outlier turned out to be dimensioned to a setout-point marker, not a
pile - confirmed directly by the user, not a drafting bug, and directly
shaped `RuleConfig.PileTagMatchToleranceMm`'s real calibration (a ~100000x
real margin between clean ~0mm matches and confirmed-bad ~1300mm ones).

**Proposed by the user directly: reconstruct each pile chain's own
bearing from live geometry and compare it to the drafted bearing call -
built the same day, `revitcheck.pile_chain_bearing_consistency`, no
manual-review fallback needed since the results were decisive.** All 4
real chains reconstructed from the real view matched their real printed
bearing call to within a third of an arcsecond - see PLANNING.md §14 for
the full table. This is a simpler, stronger mechanism than the originally-
planned drawing-vs-schedule §5b DXF-chain-walk port: no dimension-chain
traversal, no witness-point matching, no DMS parsing as an input to the
reconstruction (only as the comparison target) - just the already-
reconstructed chain and point-to-point bearing math. Built:
`Checks/PileChainReconstruction.cs` (graph algorithm: confident
tag-to-pile edges → connected components → simple-path chains, a branch
or cycle reported separately rather than guessed at), `BearingText.cs`/
`BearingMath.cs` (DMS parsing + azimuth/reciprocal/angular-difference
math), `PileChainBearingConsistencyCheck.cs` (ties it together, matching
each chain to its nearest parsed `TextNote` within
`PileChainNoteMaxDistanceMm`). New IR: `ReferenceInfo.LocalPoint`/
`ElementMetadata.LocalPoint` (local, not survey-adjusted - proximity
matching doesn't need `GetProjectPosition`, bearing math does, so piles
carry both) and `Ir/TextNoteInfo.cs`. 32 new tests, one built from the
literal real PIL232115/PIL232116/note-8802924 numbers.

**Not wired to any command yet, either check** - both are Core-only, same
discipline as `pile_model_schedule_consistency`. The bearing check's
Addin adapter needs real new work beyond the other two: populating
`ReferenceInfo.LocalPoint`/`ElementMetadata.LocalPoint` (cheap,
`Location.Point`, no `GetProjectPosition` call) and capturing `TextNote`s
into the new `TextNotes` list. `dotnet build`/`dotnet test` clean, 289
Core tests passing.

**The interactive checking workflow tying all of this together - designed
2026-08-26, Stage 1 built 2026-08-28, Stage 2 built the same day.** Full
plan at `~/.claude/plans/an-idea-of-how-floating-peacock.md` (also
summarized in PLANNING.md §16/CLAUDE.md) - combines the two triage buttons
into one, adds a checklist window listing views needing investigation, and
marks each view resolved/flagged as the relevant investigation button gets
run against it while it's open, ending in a reconciled BCF export. Needs
cross-command session state, a custom (code-behind-only, no XAML/SDK
change) WPF window, and the `ExternalEvent` pattern - none of which exist
anywhere in this codebase yet (still Stage 3's job).

- **Stage 1 (pure Core) built and tested 2026-08-28**:
  `InvestigationReconciliation.ExpandByElementIdList` (the real correctness
  fix needed before `PileChainBearingConsistencyCheck`'s pile-keyed issues
  can safely feed `Reconcile` at all), `Core/Reporting/CheckingSession.cs`,
  `Core/Reporting/CheckingSessionSerializer.cs`. 313 Core tests passing.
  See PLANNING.md §16 for a handful of interpretation calls the plan's
  prose left open, resolved during implementation.
- **Stage 2 (the two pile checks' first real Addin commands) built
  2026-08-28, same day - not yet run on the Revit machine.**
  `Commands/PileModelScheduleConsistencyCommand.cs`/
  `Commands/PileChainBearingConsistencyCommand.cs`, both whole-model and
  standalone (not yet the Stage-3 dual-mode session integration), both
  `writeBcf: true` since their findings are already verdicts, not triage.
  The Addin-side geometry work both checks were blocked on is now built
  too: `RevitMetadataElementSource.Collect` gained an opt-in
  `populateLivePosition` flag (a live `GetProjectPosition` call plus
  `Location.Point`, per element, off by default for API cost - on only for
  these two commands' pile-scoped sweep); `RevitDimensionSource` now
  populates `ReferenceInfo.LocalPoint` per reference and collects
  `TextNote`s into a new `TextNotes` list, both in the same sheeted-view
  scope dimensions already use; a new `Adapters/RevitScheduleSource.cs`
  reads every `ViewSchedule` in the document (headers via
  `Definition.GetField`, body rows via `GetCellText`), skipping the real
  two-row header artifact PLANNING.md line 695 found - not via a hardcoded
  "skip 2," but by matching each row against its own schedule's resolved
  headers (blank, or textually identical to them), so it generalizes to a
  schedule with a different artifact count or none. `dotnet build` clean
  across the whole solution including the net48 Addin - this is compile-only
  verification, the same discipline every other Revit-API-touching change in
  this codebase gets before a real machine run; **validate both buttons for
  real before starting Stage 3**, matching this project's own established
  pattern that every real correction so far has come from an actual run,
  not from guessing ahead of one.
- **Correction, 2026-08-28 (real machine run, same day): both commands were
  whole-document, and shouldn't have been.** The user ran both against the
  real model: Pile Model/Schedule reported 281 piles, 0 captured schedules,
  62 extraction errors; Pile Chain Bearing reported 281 piles, 1297
  dimensions, 3790 text notes - and pointed out directly that these tools
  are meant to run on the view someone has open, not process the whole
  drawing set as noise. 281 is the exact same document-wide over-collection
  number `InspectDimensionGeometry.pushbutton` already found and fixed once
  this session (real pile count ~43-47) - a real miss carrying that lesson
  forward into the new commands, not an ambiguity in the plan text (which
  said "whole-model, standalone," correctly meaning *not yet Stage 3's
  session integration*, not *unscoped from any view at all*). Fixed the
  same day: `RevitMetadataElementSource.Collect`/`RevitDimensionSource
  .Collect` both gained a `scopeView` parameter (a live `View`, not a name
  to re-resolve) scoping the expensive per-element/per-view collector calls
  to one view; both pile commands now pass `ActiveView` and fail cleanly if
  none is open. `sheetedViewsOnly` is deliberately bypassed when `scopeView`
  is given, so a caller's one named view is never silently skipped for not
  being placed on a sheet. Schedule collection stays whole-document on
  purpose - a schedule isn't "in" a plan view, and the `DIT_SiteID` join
  already narrows per pile regardless of schedule count. Separately: 0
  captured schedules where 2 real ones exist, with 62 extraction errors
  nobody could actually read (no error *text* was ever surfaced anywhere,
  only a bare count) - new `Commands/ExtractionErrorSample.cs` fixes that
  visibility gap for the next run, but **the root cause of the 0-schedules
  result is still unknown** - every Revit API call in `RevitScheduleSource`
  was copied from `InspectPileSetout.pushbutton`'s confirmed-working
  diagnostic, but that diagnostic only ever exercised full cell-reading
  against name-filtered "pile" schedules, never the ~60 other real
  `ViewSchedule`s (revision schedules, sheet lists, takeoffs, key
  schedules, ...) this adapter now sweeps unfiltered - needs the actual
  error text from a re-run to diagnose for real, not another guess ahead
  of one. `dotnet build` clean, 313 Core tests unaffected. **Needs one
  more real machine run.**
- **Same-day follow-up, before that run happened: narrowed schedule reading
  per the user's own suggestion** - grab the piles in the view, only look
  at schedules that could actually contain them, rather than sweeping every
  `ViewSchedule` in the document. This maps onto a filter that already
  exists one layer up: `PileModelScheduleConsistencyCheck` only ever uses
  schedules whose headers resolve *all three* of an id/Easting/Northing
  candidate. `RevitScheduleSource.Collect` now takes those same three
  candidate lists and only attempts the expensive body-cell read for
  schedules that pass - not a new judgement, the identical one the check
  already makes, just made before the risky/expensive operation instead of
  after it. Headers are still read for every schedule regardless (cheap,
  confirmed to work across every real schedule kind by the diagnostic's own
  unconditional field dump). `PileModelScheduleConsistencyCommand` now
  passes `config.PileScheduleIdHeaders`/`PileScheduleEastingHeaders`/
  `PileScheduleNorthingHeaders`. **Still not confirmed against real error
  text** - a well-justified fix for the most likely cause, not a proven
  diagnosis. `dotnet build` clean, 313 Core tests unaffected.
- **Real machine re-run, 2026-08-28: Pile Chain Bearing is fully validated;
  Pile Model/Schedule's real root cause is diagnosed and fixed.** Pile
  Chain Bearing: 43 piles, 46 dimensions, 31 text notes in the pile layout
  view, 0 issues - 46 dimensions is the exact figure this document already
  recorded from the manual diagnostic re-run on this same view. **This
  button is done.** Pile Model/Schedule: the header filter worked exactly
  as designed (62 errors down to 2, both on the two real named schedules,
  everything else correctly skipped) - and the real error text finally
  answered the root-cause question the previous entry could only guess at:
  `Illegal attempt to modify document. Reason: Changes are disabled for
  the active document!`, not a schedule-kind mismatch. `ViewSchedule
  .GetTableData()`/`GetCellText` can internally require document-modify
  permission (to regenerate cached table data) even though it's
  conceptually a read, and the command's `TransactionMode.ReadOnly` blocked
  that. Fixed: `TransactionMode.Manual`, with the schedule read wrapped in
  its own `Transaction` that's always `RollBack()`'d, never committed -
  satisfies the API without persisting anything. Not yet confirmed - needs
  one more real run.
- **Same-day follow-up: reads schedule rows off the schedule's own backing
  elements, not rendered table text.** The user asked whether a schedule
  keeps a link back to its real elements - yes, via `FilteredElementCollector
  (doc, schedule.Id)`, and that pointed at the real fix rather than another
  patch: stop reading `GetCellText`'s formatted display text at all (format-
  fragile, the same class of bug ARCHIVE-pdf-dwg.md already warns against)
  and resolve each candidate column's real bound parameter
  (`ScheduleField.ParameterId`) to read directly off each backing element -
  the same pure parameter read already used for piles. Every Revit API
  member used was verified against the real `RevitAPI.dll` via
  `System.Reflection.MetadataLoadContext` before writing any code, not
  guessed. `RevitScheduleSource.TryReadDataRowsFromElements` is now the
  primary path; the original `GetCellText` read is kept as
  `ReadDataRowsFromCellText`, a fallback for calculated/combined columns.
  `dotnet build` clean. **Needs one more real run to confirm.**
- **Real re-run, 2026-08-28 (later the same day): the transaction fix
  worked, but every pile (43/43) now fails to match its schedule row.**
  Real issue descriptions confirmed it's a flat zero-match join for every
  pile, not "ambiguous" and not a numeric mismatch - a systematic bug
  (4 real piles already confirmed sub-mm agreement, so 100% failure can't
  be real drift). `PileModelScheduleConsistencyCommand` gained a permanent
  `ScheduleDiagnostics` summary section (the exact `candidateSchedules`
  filter the check already applies, made visible): each candidate
  schedule's name, real captured row count, and its first row's literal id
  value - enough to tell "the row-skip heuristic ate every real row" from
  "rows were captured but ids don't textually match" without dumping real
  coordinates into a dialog. `dotnet build` clean. **Needs one more real
  run.**

### Former open questions - now answered from real data

1. ~~The actual key-parameter name/identity~~ - `ATM_Asset_Identifier`,
   confirmed via the real CSVs and the `InspectElements` export.
2. ~~How nested sub-components actually work~~ - `FamilyInstance.
   GetSubComponentIds()`, each with its own `ElementId`/`UniqueId` and
   independently-editable parameters (44 of 71 elements in the real export
   had one). `RevitMetadataElementSource` walks them exactly this way.
3. ~~Element category/class scoping~~ - Floors, Generic Models, Structural
   Connections, Structural Foundations, Structural Framing (the real
   spread in the export) - `RevitMetadataElementSource.DefaultCategories`.
4. ~~Where a real mapping file and CSV should live~~ - neither: both are
   picked per run (see "Wired up" above), never committed to this repo.
