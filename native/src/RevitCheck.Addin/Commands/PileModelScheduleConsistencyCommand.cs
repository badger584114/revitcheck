using System.Globalization;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitCheck.Addin.Adapters;
using RevitCheck.Core.Checks;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Reporting;

namespace RevitCheck.Addin.Commands;

/// <summary>
/// Wires <c>revitcheck.pile_model_schedule_consistency</c> up to a real
/// Revit document - the first real ribbon button for either pile check
/// (PLANNING.md §16 Stage 2; the Core-side check itself was built and
/// tested 2026-08-26, PLANNING.md §14). Standalone: unlike the interactive
/// checking workflow's dual-mode session integration (PLANNING.md §16
/// Stage 3, not yet built), this always writes results directly - same
/// shape as <see cref="MetadataReconciliationCommand"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>writeBcf: true</c> - this check's findings are already verdicts, not
/// triage (a pile's live position either agrees with its schedule row or it
/// doesn't), the same reasoning <see cref="MetadataReconciliationCommand"/>
/// and <c>InvestigationReconciliation</c>'s own remarks already establish
/// for why this check "stands alone" and should never be routed through
/// dimension-triage reconciliation.
/// </para>
/// <para>
/// <b>Pile collection is scoped to the active view, not the whole
/// document - a real bug fixed 2026-08-28, the day after this command was
/// first built.</b> The first version swept the whole document by category
/// alone and pulled in 281 "piles" on the real model this project develops
/// against - the exact same over-collection number
/// <c>InspectDimensionGeometry.pushbutton</c> already found and fixed the
/// same way (CLAUDE.md's "Notes worth not rediscovering": real count is
/// ~43-47, the difference being piles/foundations belonging to unrelated
/// structures elsewhere in a large model). This command's job is checking
/// the pile layout someone has open, not every foundation element in the
/// document - <see cref="RevitMetadataElementSource.Collect"/>'s
/// <c>scopeView</c> parameter now does that. Schedule collection stays
/// whole-document either way (see below) - a schedule isn't "in" a plan
/// view the way a pile element is, and the id-based join already narrows
/// to the one row that matters per pile regardless of how many schedules
/// exist.
/// </para>
/// </remarks>
[Transaction(TransactionMode.Manual)]
public class PileModelScheduleConsistencyCommand : IExternalCommand
{
    private const int MaxListedErrors = 5;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiDoc = commandData.Application.ActiveUIDocument;
        var doc = uiDoc?.Document;
        if (doc is null)
        {
            message = "No active document.";
            return Result.Failed;
        }

        var activeView = uiDoc!.ActiveView;
        if (activeView is null)
        {
            message = "No active view - open the pile layout view to check before running this.";
            return Result.Failed;
        }

        var config = new RuleConfig();

        MetadataCollectionResult piles;
        try
        {
            // Scoped to the active view (see class remarks - a real
            // over-collection bug this fixes) and the pile category alone,
            // not RevitMetadataElementSource's full DefaultCategories set -
            // populateLivePosition costs a real GetProjectPosition call per
            // element, worth paying only for the category this check
            // actually reads.
            piles = RevitMetadataElementSource.Collect(
                doc,
                categories: new[] { BuiltInCategory.OST_StructuralFoundation },
                populateLivePosition: true,
                scopeView: activeView);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitCheck - Pile Model/Schedule", $"Could not collect piles:\n\n{ExceptionMessage.Full(ex)}");
            return Result.Failed;
        }

        var scheduleErrors = new List<string>();
        List<ScheduleInfo> schedules;
        try
        {
            // Header-filtered, not an unconditional full-document body read
            // (a real bug fixed 2026-08-28 - see RevitScheduleSource's own
            // remarks): only schedules whose headers resolve every one of
            // PileModelScheduleConsistencyCheck's own id/Easting/Northing
            // candidates get their body cells read at all.
            //
            // Wrapped in a transaction that is always rolled back, never
            // committed - a real, confirmed 2026-08-28 Revit API gotcha:
            // ViewSchedule.GetTableData()/GetCellText threw "Illegal
            // attempt to modify document. Reason: Changes are disabled for
            // the active document!" under this command's original
            // TransactionMode.ReadOnly, on both of this model's two real
            // pile schedules. Reading a schedule's cell text can trigger
            // Revit to internally regenerate/compute cached table data,
            // which - despite this being conceptually a read - needs an
            // open transaction to satisfy the API's own modifiability
            // check. RollBack (not Commit) guarantees nothing this command
            // does is ever actually persisted to the document, keeping the
            // "a check that silently edited the model while reporting on
            // it is exactly the kind of black box CLAUDE.md rules out"
            // guarantee RevitDimensionSource's own remarks already state -
            // this is a mechanical API-satisfaction step, not a real edit.
            using var scheduleReadTransaction = new Transaction(doc, "RevitCheck - read pile schedules (rolled back)");
            scheduleReadTransaction.Start();
            try
            {
                schedules = RevitScheduleSource.Collect(
                    doc,
                    scheduleErrors,
                    config.PileScheduleIdHeaders,
                    config.PileScheduleEastingHeaders,
                    config.PileScheduleNorthingHeaders);
            }
            finally
            {
                scheduleReadTransaction.RollBack();
            }
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitCheck - Pile Model/Schedule", $"Could not collect schedules:\n\n{ExceptionMessage.Full(ex)}");
            return Result.Failed;
        }

        var model = new RevitModel
        {
            DocTitle = doc.Title,
            Elements = piles.Elements,
            Schedules = schedules,
            ExtractionErrors = piles.ExtractionErrors.Concat(scheduleErrors).ToList(),
        };

        var issues = PileModelScheduleConsistencyCheck.Run(model, config);

        var summary = $"{issues.Count} issue(s) found ({piles.Elements.Count} pile(s) in view '{activeView.Name}', " +
            $"{schedules.Count} captured schedule(s) checked)" +
            (model.ExtractionErrors.Count > 0 ? $", {model.ExtractionErrors.Count} extraction error(s)" : "") +
            "." +
            ExtractionErrorSample.Format(model.ExtractionErrors) +
            ScheduleDiagnostics(piles.Elements, schedules, config);

        string? outputPath;
        try
        {
            outputPath = IssueOutput.WriteNextToModel(doc, issues, "pile_model_schedule_consistency", "RevitCheck - Pile Model/Schedule");
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitCheck - Pile Model/Schedule",
                $"{summary}\n\nBut the results file could not be written:\n\n{ExceptionMessage.Full(ex)}");
            return Result.Succeeded;
        }

        if (outputPath is null)
        {
            TaskDialog.Show("RevitCheck - Pile Model/Schedule", $"{summary}\n\nSave cancelled - nothing written.");
            return Result.Succeeded;
        }

        TaskDialog.Show("RevitCheck - Pile Model/Schedule",
            $"{summary}\n\nWritten to (JSON, CSV and BCF, same folder):\n{outputPath}");

        return Result.Succeeded;
    }

    /// <summary>
    /// Added 2026-08-28 after a real run flagged all 43/43 piles as
    /// "no matching row was found in any captured pile schedule" - a
    /// systematic bug, not real drift (PLANNING.md §14 already confirmed
    /// sub-millimetre agreement on 4 real piles). The per-pile issue list
    /// alone can't say *why* the join found nothing - it needs a look at
    /// what was actually captured. Reports, for every schedule whose
    /// headers resolve all three of the check's own id/Easting/Northing
    /// candidates (the identical <c>candidateSchedules</c> filter
    /// <c>PileModelScheduleConsistencyCheck.Run</c> applies - this is not a
    /// new judgement, just made visible): its name, how many rows were
    /// actually captured, and the literal id-column value of its first row
    /// - directly comparable against a real pile's own key (e.g.
    /// "PIL232126", visible in the issue descriptions already) without
    /// dumping every row's real Easting/Northing coordinates into a dialog.
    /// A permanent part of the summary, not throwaway diagnostic scaffolding
    /// - which schedule(s) actually qualified as candidates and how many
    /// rows they carried is useful coverage information on every run, not
    /// just this one.
    /// </summary>
    private static string ScheduleDiagnostics(List<ElementMetadata> piles, List<ScheduleInfo> schedules, RuleConfig config)
    {
        var candidates = schedules.Where(s =>
            s.ResolveHeader(config.PileScheduleIdHeaders) is not null &&
            s.ResolveHeader(config.PileScheduleEastingHeaders) is not null &&
            s.ResolveHeader(config.PileScheduleNorthingHeaders) is not null)
            .ToList();

        if (candidates.Count == 0)
        {
            return "\n\nNo captured schedule's headers resolved all of id/Easting/Northing - nothing was a candidate to join against.";
        }

        var lines = candidates.Select(s =>
        {
            var idHeader = s.ResolveHeader(config.PileScheduleIdHeaders)!;
            var firstRowId = s.Rows.Count > 0 && s.Rows[0].TryGetValue(idHeader, out var value)
                ? $"'{value}'"
                : "(no rows captured)";
            return $"- '{s.Name}': {s.Rows.Count} row(s) captured, id header '{idHeader}', first row's id = {firstRowId}";
        });

        return "\n\nCandidate schedule(s):\n" + string.Join("\n", lines) + CharacterCheck(piles, candidates, config);
    }

    /// <summary>
    /// Added 2026-08-28, a safety net alongside the AsString()-vs-
    /// AsValueString() fix (see RevitScheduleSource.ReadParameterText's own
    /// remarks): a real run showed a schedule row with an id textually
    /// identical, by eye, to a failing pile's own key - visible text alone
    /// can't rule out a hidden-character/normalization mismatch a person
    /// reading a dialog or a JSON file would never spot. Finds one pile
    /// whose key loosely (case-insensitive) matches one candidate
    /// schedule's first row id, then reports both strings' exact length
    /// and per-character hex code points side by side - if the AsString()
    /// fix above is what was actually wrong, this should now show an exact
    /// match; if it still doesn't, the code points say exactly why.
    /// </summary>
    private static string CharacterCheck(List<ElementMetadata> piles, List<ScheduleInfo> candidates, RuleConfig config)
    {
        foreach (var schedule in candidates.Where(s => s.Rows.Count > 0))
        {
            var idHeader = schedule.ResolveHeader(config.PileScheduleIdHeaders);
            if (idHeader is null || !schedule.Rows[0].TryGetValue(idHeader, out var rowId))
            {
                continue;
            }

            var matchingPile = piles.FirstOrDefault(p =>
                p.Parameters.TryGetValue(config.PileKeyParameterName, out var v) &&
                string.Equals((v.RawString ?? v.DisplayString)?.Trim(), rowId.Trim(), StringComparison.OrdinalIgnoreCase));

            if (matchingPile is null)
            {
                return $"\n\nCharacter check: no pile's own key even loosely (case-insensitive) matches " +
                    $"'{rowId}' from '{schedule.Name}' - the join may be comparing the wrong scope, not just a formatting difference.";
            }

            var pileValue = matchingPile.Parameters[config.PileKeyParameterName];
            var pileKey = (pileValue.RawString ?? pileValue.DisplayString)!.Trim();
            var scheduleId = rowId.Trim();
            var exact = string.Equals(pileKey, scheduleId, StringComparison.Ordinal);

            return $"\n\nCharacter check: pile key '{pileKey}' ({pileKey.Length} char(s): {CodePoints(pileKey)}) vs. " +
                $"schedule id '{scheduleId}' ({scheduleId.Length} char(s): {CodePoints(scheduleId)}) - exact match: {exact}." +
                PositionCheck(matchingPile, schedule, config);
        }

        return "";
    }

    private static string CodePoints(string value) => string.Join(" ", value.Select(c => ((int)c).ToString("X4")));

    /// <summary>
    /// Added 2026-08-28 alongside <see cref="CharacterCheck"/> - the id
    /// join is now confirmed exact-matching for real data, but the issue
    /// count on that same real run didn't drop, meaning something in the
    /// Easting/Northing side is now the live failure point instead
    /// (a genuine mismatch, or - the leading suspect - a units/formatting
    /// mismatch: <c>ReadParameterText</c> reads a numeric column via
    /// <c>AsValueString()</c>, which applies the *project's own display
    /// unit* and may include a unit suffix, while
    /// <c>PileModelScheduleConsistencyCheck.TryParseMetresToMm</c> expects
    /// a bare metres number and silently fails to parse anything else).
    /// Reports, for the same matched pile/schedule-row pair
    /// <see cref="CharacterCheck"/> already found: the row's raw captured
    /// Easting/Northing text, whether it parses as a bare number, and the
    /// pile's own live <c>GetProjectPosition</c> value - enough to tell a
    /// parse failure from a genuine (or spurious) position mismatch
    /// without a fourth diagnostic round.
    /// </summary>
    private static string PositionCheck(ElementMetadata pile, ScheduleInfo schedule, RuleConfig config)
    {
        var eastingHeader = schedule.ResolveHeader(config.PileScheduleEastingHeaders);
        var northingHeader = schedule.ResolveHeader(config.PileScheduleNorthingHeaders);
        var row = schedule.Rows.FirstOrDefault(r =>
            eastingHeader is not null && r.TryGetValue(eastingHeader, out _) &&
            northingHeader is not null && r.TryGetValue(northingHeader, out _));

        if (eastingHeader is null || northingHeader is null || row is null)
        {
            return "\n\nPosition check: could not find the matched pile's own row again to inspect Easting/Northing.";
        }

        row.TryGetValue(eastingHeader, out var rawEasting);
        row.TryGetValue(northingHeader, out var rawNorthing);
        var eastingParses = double.TryParse((rawEasting ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var eastingMetres);
        var northingParses = double.TryParse((rawNorthing ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var northingMetres);

        return $"\n\nPosition check: schedule row raw Easting = '{rawEasting}' (parses as a bare number: {eastingParses}" +
            (eastingParses ? $" -> {(eastingMetres * RuleConfig.ScheduleMetresToMm):0.###}mm" : "") +
            $"), raw Northing = '{rawNorthing}' (parses: {northingParses}" +
            (northingParses ? $" -> {(northingMetres * RuleConfig.ScheduleMetresToMm):0.###}mm" : "") +
            $"). Pile's own live position: Easting = {pile.ProjectPositionEastingMm?.ToString("0.###") ?? "(null)"}mm, " +
            $"Northing = {pile.ProjectPositionNorthingMm?.ToString("0.###") ?? "(null)"}mm.";
    }
}
