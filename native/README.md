# RevitCheck native add-in

The C# native Revit add-in. `extensions/RevitCheck.extension` (pyRevit) is
frozen — no further feature work there (PLANNING.md §12) — this is where new
work happens. The first feature built here is **metadata reconciliation**:
joining a model's captured element parameters against an external CSV of
reference data via a project-specific mapping file, and flagging the handful
of items that are missing or have a wrong/blank field among 30-40 — the thing
that's genuinely hard to catch by eye in a Revit schedule.

See the plan this was built from for the full design reasoning:
`~/.claude/plans/the-first-button-we-abundant-platypus.md` (or ask for it to
be copied into this repo if that path won't be around later).

## Layout

```
native/
  RevitCheck.sln
  Directory.Build.props           # shared LangVersion/Nullable across every project
  src/
    RevitCheck.Core/               # netstandard2.0 - NO Revit API reference, no
                                    #   runtime dependency. Everything here is
                                    #   plain, pure, and testable with zero Revit.
      Ir/                          # ElementMetadata, ParameterValue, RevitModel
      Capture/                     # JSON save/load - mirrors capture.py
      Issues/                      # Issue + IssueSorting - mirrors issue.py
      Catalog/                     # Catalog + CheckRegistry - mirrors catalog.py
      Mapping/                     # ParameterMapping ("the export file taken
                                    #   once and saved") + its serializer
      Csv/                         # CSV-only reference-data reader (CsvHelper)
      Checks/                      # MetadataReconciliationCheck,
                                    #   CaptureCoverageCheck, ReconciliationConfig
      Polyfills/                   # IsExternalInit / required-member shims
                                    #   netstandard2.0 needs for modern C#
    RevitCheck.Addin/               # net48 (Revit 2024) - the ONLY project that
                                    #   references the Revit API. Not yet built
                                    #   beyond the project scaffold - needs the
                                    #   Revit machine (see "What's not done" below).
  tools/
    RevitCheck.MappingBuilder/      # net8.0 console app, no Revit refs. Builds a
                                    #   starter mapping file from a real capture +
                                    #   a real CSV's headers - off-machine, the
                                    #   analogue of scripts/check_capture.py.
  tests/
    RevitCheck.Core.Tests/          # xUnit, 51 tests
    RevitCheck.MappingBuilder.Tests/ # xUnit, 6 tests
```

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
dotnet test           # Core + MappingBuilder test suites, zero Revit involved
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

## What's built (Phases 0-6 of the plan)

Everything above compiles and is tested - **51 + 6 = 57 tests, all passing**,
entirely without Revit. Notably:

- `MetadataReconciliationCheck` implements the full join/compare logic:
  numeric-with-tolerance and case-insensitive-exact-string comparisons,
  family/category-based parameter-name overrides (the actual "different
  families expose the same field under different parameter names" problem
  this tool exists to solve), a nested sub-component reconciled completely
  independently of its host, an element with a key but no CSV match reported
  by default (the "missing item" signal), a genuinely blank model value
  against CSV data reported as a first-class mismatch (the "incorrectly
  filled" signal) rather than a coverage note, and **zero noise** from a
  1000+ row whole-of-project CSV against a 20-30 element model - all of these
  are explicit, named regression tests, not incidental behaviour.
- One real bug was found and fixed during this build via the end-to-end
  integration test (`EndToEndReconciliationTests`): CSV header/column-name
  lookups were case-sensitive, which would have silently turned almost every
  auto-defaulted `csv_column` (a lowercase canonical field key vs. a
  Title-Case spreadsheet header) into a dropped, unreported field. Fixed in
  `CsvReader`/`MetadataReconciliationCheck` to be case-insensitive on column
  *names* (key **values** stay case-sensitive - an asset ID is data, a
  header is structure). Covered by a dedicated regression test
  (`CsvReaderTests.ColumnLookup_IsCaseInsensitive`), not just the integration
  test that caught it.
- The `RevitCheck.Addin` project (net48) builds cleanly against real
  `Autodesk.Revit.DB`/`Autodesk.Revit.UI` types on this Mac, with no Revit
  installed, via the `Nice3point.Revit.Api.*` NuGet packages - confirmed with
  a throwaway sanity-check file during the build, not just assumed from the
  package existing.

## What's not done (Phases 7-8 - need the Revit machine)

- `Adapters/RevitMetadataElementSource.cs` - the only Revit-API-touching file
  in this feature. Not written yet: it needs real answers to open questions
  the Mac side can't resolve (see below), and per PLANNING.md §12's own
  expectation, more iteration at the Revit machine than the Python adapter
  needed.
- `IExternalCommand`s, ribbon wiring, the `.addin` manifest, first real
  capture, first real mapping-file build against real families, first real
  reconciliation run.

### Open questions that block Phase 7, not resolvable from this side

1. **The actual key-parameter name/identity** used across the team's
   metadata-input tools.
2. **How nested sub-components actually work in these families** - whether
   the items needing independent data (e.g. a fixing bracket nested in a
   panel) are "shared" nested families with their own `ElementId` and
   independently editable instance parameters (which would make
   `FamilyInstance.GetSubComponentIds()`/`SuperComponent` the right API
   surface), or something else the existing metadata-input tools already
   have a working convention for.
3. Element category/class scoping for the adapter's collection sweep -
   which categories actually carry the 30-40 tracked parameters.
4. Where a real mapping file and a real project's CSV should live - not
   committed to this repo (client asset data), by the same caution
   `capture.py` already applies to a real model capture.
