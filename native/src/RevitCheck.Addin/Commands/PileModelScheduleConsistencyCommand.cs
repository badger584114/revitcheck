using RevitCheck.Core.Issues;
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
/// tested 2026-08-26, PLANNING.md §14).
/// </summary>
/// <remarks>
/// <para>
/// <b>Dual-mode, added PLANNING.md §16 Stage 3.</b> If
/// <see cref="CheckingSessionHost.Session"/> is null, this always writes
/// results directly - the original, standalone behaviour, same shape as
/// <see cref="MetadataReconciliationCommand"/>, <c>writeBcf: true</c> (this
/// check's findings are already verdicts, not triage - a pile's live
/// position either agrees with its schedule row or it doesn't). If a
/// session is active, results are appended to that view's
/// <c>OtherInvestigationFindings</c> instead
/// (<see cref="CheckingSession.RecordInvestigation"/>'s <c>otherFindingsRuleId</c>
/// path) - this check is keyed on pile ElementIds, not dimension ids, so
/// it "stands alone": never reconciled against dimension triage (there's
/// nothing dimension-shaped to reconcile it against), but already a
/// verdict, so it counts toward the view being <c>Flagged</c> and flows
/// into the session's own BCF export exactly like a reconciled confirmed
/// problem does - see <c>ViewChecklistEntry.OtherInvestigationFindings</c>'s
/// own remarks.
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

        // Per-model config if this project has one, compiled defaults
        // otherwise - either way the run's own output says which
        // (RuleConfigSource's remarks).
        var (config, configDescription) = RuleConfigSource.Resolve(doc);

        // Which categories to sweep is config, not a constant: piles are
        // Structural Foundations on one real model and Generic Models on
        // another, and an element in no swept category never reaches the
        // check at all (RuleConfig.PileCollectionCategoryNames).
        var (collectionCategories, allModelCategories, unresolvedCategories) = CategoryScope.Resolve(config);

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
                categories: collectionCategories,
                populateLivePosition: true,
                scopeView: activeView,
                allModelCategories: allModelCategories);
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
                    // No id-header requirement: rows carry their own element
                    // (ScheduleRow.ElementId), so a schedule without a
                    // recognised id column is still perfectly usable. A real
                    // 2026-09-07 run on a second model captured zero rows
                    // purely because its id column was headed differently.
                    idHeaderCandidates: null,
                    eastingHeaderCandidates: config.PileScheduleEastingHeaders,
                    northingHeaderCandidates: config.PileScheduleNorthingHeaders);
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

        var summary = Summarise(model, issues, config, piles.Elements.Count, activeView.Name, doc, unresolvedCategories);

        if (CheckingSessionHost.Session is { } session)
        {
            var viewId = activeView.Id.Value;
            // A row is created if triage never raised anything here, so a
            // real pile-versus-schedule mismatch in an otherwise-clean view
            // still reaches the checklist and the reconciled BCF export
            // instead of living only in this command's own JSON/CSV
            // (see CheckingSession.EnsureView).
            // No sheet number available here - this command collects piles
            // and schedules, not views. Null rather than guessed.
            session.EnsureView(viewId, activeView.Name, sheetNo: null);
            // No dimension linkage to expand or reconcile - see this
            // class's remarks and InvestigationReconciliation's own on why
            // this check "stands alone". investigatedElementIds is unused
            // on this path (RecordInvestigation ignores it whenever
            // otherFindingsRuleId is set), passed empty rather than
            // recomputed for nothing.
            session.RecordInvestigation(viewId, Array.Empty<long>(), issues, PileModelScheduleConsistencyCheck.RuleId);

            // A row always exists now (EnsureView above), so a real
            // mismatch in a view triage never flagged still reaches the
            // checklist and the reconciled BCF export.
            var sessionNote =
                "\n\nRecorded in the checklist's Other Findings column. This check does not resolve " +
                "Dimension Triage items - Pile Chain Bearing is the one that does.";

            try
            {
                CheckingSessionHost.Autosave();
            }
            catch (Exception ex)
            {
                sessionNote += $"\n\nThe session could not be saved to disk:\n\n{ExceptionMessage.Full(ex)}";
            }

            CheckingSessionHost.Window?.Refresh();

            TaskDialog.Show("RevitCheck - Pile Model/Schedule", summary + sessionNote);
            return Result.Succeeded;
        }

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
    /// <summary>
    /// What the run actually established, and nothing else: how many piles
    /// were checked, which schedules they were compared against, and which
    /// piles disagree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Cut back hard on 2026-09-09, per the user - the dialog was "very
    /// confusing".</b> It had accumulated a candidate-schedule breakdown
    /// with per-schedule row and element-id counts, a character-level hex
    /// dump comparing a pile key against a schedule id, an extraction-error
    /// sample, the full config path with every setting it pinned, and a
    /// paragraph explaining what the check does not do. The one thing a
    /// reader wants - which pile is wrong, and by how much - was the
    /// hardest thing in it to find.
    /// </para>
    /// <para>
    /// Nothing is lost: every finding, coverage note included, is in the
    /// JSON/CSV/BCF written beside the model, and in the checklist. The
    /// dialog keeps a one-line count of the notes it no longer prints, so
    /// CLAUDE.md's "report a coverage indicator, never fail silently"
    /// still holds - what changed is that a coverage note is now counted
    /// here and read there, rather than recited in full.
    /// </para>
    /// </remarks>
    private static string Summarise(
        RevitModel model,
        List<Issue> issues,
        RuleConfig config,
        int pileCount,
        string viewName,
        Document doc,
        List<string> unresolvedCategories)
    {
        var compared = PileModelScheduleConsistencyCheck.ComparedSchedules(model, config);

        var lines = new List<string>
        {
            $"{pileCount} pile(s) in view '{viewName}'.",
            compared.Count == 0
                ? "Compared against: nothing - no captured schedule has readable Easting/Northing columns."
                : "Compared against: " + string.Join(", ", compared.Select(s => $"'{s.Name}'")) + ".",
        };

        var mismatches = issues
            .Where(i => string.Equals(i.Category, "geometry", StringComparison.OrdinalIgnoreCase))
            .ToList();

        lines.Add(string.Empty);
        if (mismatches.Count == 0)
        {
            lines.Add("No mismatches.");
        }
        else
        {
            lines.Add($"{mismatches.Count} pile(s) mismatched:");
            lines.AddRange(mismatches.Select(MismatchLine));
        }

        // The three indicators that must survive the cut, each shown only
        // when it has something to say - a clean run stays short, and none
        // of these can be silent when it matters. Extraction errors in
        // particular appear nowhere else on this command's path.
        var notes = issues.Count - mismatches.Count;
        var footnotes = new List<string>();

        if (notes > 0)
        {
            footnotes.Add($"{notes} note(s) on what could not be checked - see the results file.");
        }

        if (model.ExtractionErrors.Count > 0)
        {
            footnotes.Add($"{model.ExtractionErrors.Count} element(s) could not be read at all.");
        }

        var pinned = PinnedSettingCount(doc);
        if (pinned > 0)
        {
            footnotes.Add(
                $"This model's config pins {pinned} setting(s) away from the built-in defaults - " +
                "Rule Config shows which.");
        }

        if (footnotes.Count > 0)
        {
            lines.Add(string.Empty);
            lines.AddRange(footnotes);
        }

        return string.Join("\n", lines) + CategoryScope.Note(unresolvedCategories);
    }

    /// <summary>
    /// How many settings this model's config holds away from the current
    /// defaults - worth one line when non-zero, since a config that pins a
    /// superseded tolerance is invisible otherwise and has silently decided
    /// two real runs (PLANNING.md §22, §23).
    /// </summary>
    private static int PinnedSettingCount(Document doc)
    {
        try
        {
            var json = RuleConfigSource.ReadRaw(doc);
            return json is null ? 0 : RuleConfigSerializer.DescribeOverrides(json).Count;
        }
        catch
        {
            // Never let a reporting nicety fail the run that produced the
            // findings - Rule Config reports properly on this file anyway.
            return 0;
        }
    }

    /// <summary>One mismatch, as "5506399 - 50mm" - the id a reviewer types into Select by ID, and how far out it is.</summary>
    private static string MismatchLine(Issue issue)
    {
        var delta = issue.SuggestedFix is not null &&
                    issue.SuggestedFix.TryGetValue("delta_mm", out var raw) &&
                    raw is not null &&
                    double.TryParse(
                        raw.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var mm)
            ? $" - {mm.ToString("0.#", CultureInfo.InvariantCulture)}mm"
            : string.Empty;

        var key = issue.SuggestedFix is not null &&
                  issue.SuggestedFix.TryGetValue("pile_key", out var rawKey) &&
                  rawKey?.ToString() is { Length: > 0 } k
            ? $"{k} "
            : string.Empty;

        return $"  {key}({issue.ElementId?.ToString(CultureInfo.InvariantCulture) ?? "no element"}){delta}";
    }
}
