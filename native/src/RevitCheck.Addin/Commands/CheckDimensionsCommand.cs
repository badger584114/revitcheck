using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitCheck.Addin.Adapters;
using RevitCheck.Core.Checks;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Issues;

namespace RevitCheck.Addin.Commands;

/// <summary>
/// Runs every dimension check that applies to the active view, and
/// reconciles them against that view's triage in one pass.
/// </summary>
/// <remarks>
/// <para>
/// <b>Added 2026-09-11, and it is the shape the workflow was always meant
/// to have</b> - per the user: "the intention was for the user to open the
/// views and run the button anyway so that they can do manual verification
/// if needed and clear a view before moving to the next view." One button,
/// one view, everything applicable, then the view is settled and the
/// reviewer moves on.
/// </para>
/// <para>
/// <b>Why one button rather than a fourth.</b> The Dimension Checking panel
/// already had the problem §18 named: a reviewer has to know which
/// dimension shapes a view contains before knowing which button to press,
/// which is backwards - the tool is supposed to tell them that.
/// <see cref="DimensionResolution"/> now knows which check can settle which
/// dimension, so the button works it out and says so in its summary.
/// Pile Chain Bearing and Spot Elevation stay as they are for now: nothing
/// anyone relies on changes while this proves itself.
/// </para>
/// <para>
/// <b>Per-view by necessity as well as by design.</b> The drafted-dimension
/// check needs a witness-geometry search - projecting each anchor onto
/// every face of the elements the view shows - which is real per-face cost
/// and needs a <c>View</c> to scope to, exactly like Spot Elevation's shelf
/// search. Whole-document is neither possible nor wanted.
/// </para>
/// <para>
/// Which checks run, and which of them may count a dimension as
/// investigated, lives in <see cref="DimensionInvestigation"/> in Core -
/// moved there after this command, as first built, recorded Pile Chain
/// Bearing's topology-only scope and never ran the pile dimension check
/// (PLANNING.md §27's trap, reintroduced). A command cannot be tested;
/// that decision now can.
/// </para>
/// </remarks>
[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public class CheckDimensionsCommand : IExternalCommand
{
    private const string Title = "RevitCheck - Check Dimensions";

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            return Run(commandData, ref message);
        }
        catch (Exception ex)
        {
            message = ExceptionMessage.Full(ex);
            return Result.Failed;
        }
    }

    private static Result Run(ExternalCommandData commandData, ref string message)
    {
        var uiDoc = commandData?.Application?.ActiveUIDocument;
        var doc = uiDoc?.Document;
        if (doc is null)
        {
            message = "No active document.";
            return Result.Failed;
        }

        var activeView = uiDoc!.ActiveView;
        if (activeView is null)
        {
            message = "No active view - open the view you want to check before running this.";
            return Result.Failed;
        }

        var (config, _) = RuleConfigSource.Resolve(doc);

        DimensionCollectionResult collected;
        try
        {
            // Both geometry searches at once: the shelf walk Spot Elevation
            // needs, and the witness projection the drafted-dimension check
            // needs. Both are real per-element cost and both are scoped to
            // this one view.
            collected = RevitDimensionSource.Collect(
                doc,
                scopeView: activeView,
                populateNearbyShelfFaces: true,
                shelfSearchRadiusMm: config.SpotElevationShelfSearchRadiusMm,
                populateWitnessPoints: true,
                witnessSearchRadiusMm: config.DrawnDimensionSearchRadiusMm,
                witnessMaxCandidates: config.DrawnDimensionMaxCandidates);
        }
        catch (Exception ex)
        {
            TaskDialog.Show(Title, $"Could not collect the view:\n\n{ExceptionMessage.Full(ex)}");
            return Result.Failed;
        }

        var unresolvedCategories = new List<string>();
        var piles = CollectPiles(doc, activeView, config, unresolvedCategories, out var pileError);
        if (pileError is not null)
        {
            collected.ExtractionErrors.Add(pileError);
        }

        var model = new RevitModel
        {
            DocTitle = doc.Title,
            RevitVersion = commandData!.Application.Application.VersionNumber,
            CapturedAt = DateTime.UtcNow.ToString("O"),
            Sheets = collected.Sheets,
            Views = collected.Views,
            Dimensions = collected.Dimensions,
            TextNotes = collected.TextNotes,
            Elements = piles,
            ExtractionErrors = collected.ExtractionErrors,
        };

        var viewInfo = collected.Views.FirstOrDefault(v => v.ElementId == activeView.Id.Value);

        // Every check that can settle a dimension, run together - see
        // DimensionInvestigation for which of them may count a dimension as
        // investigated, and why Pile Chain Bearing is not one of them. Each
        // reports its own coverage when nothing in the view is its shape, so
        // running them all costs a note rather than a wrong answer.
        var (issues, investigated) = DimensionInvestigation.Run(
            model, config, activeView.Id.Value, activeView.Name, viewInfo?.SheetNo);

        var summary = BuildSummary(model, issues, activeView, viewInfo, doc, unresolvedCategories);
        return Report(doc, activeView, viewInfo, issues, investigated, summary);
    }

    /// <summary>
    /// What is in this view, what can settle it, and what disagrees - see
    /// <see cref="RunSummary"/>.
    /// </summary>
    private static string BuildSummary(
        RevitModel model,
        List<Issue> issues,
        View activeView,
        ViewInfo? viewInfo,
        Document doc,
        List<string> unresolvedCategories)
    {
        return RunSummary.Build(
            new[]
            {
                $"{model.Dimensions.Count} dimension(s) in view '{activeView.Name}'.",
                // The answer to "which button applies", which the reviewer
                // previously had to work out for themselves (§18).
                "By dimension type: " + DimensionResolution.Describe(model.Dimensions, viewInfo) + ".",
            },
            issues,
            i => $"  {RunSummary.Label(i)}{RunSummary.Amount(i, "delta_mm", "mm")}",
            "dimension",
            model.ExtractionErrors,
            doc,
            unresolvedCategories);
    }

    private static Result Report(
        Document doc,
        View activeView,
        ViewInfo? viewInfo,
        List<Issue> issues,
        List<long> investigated,
        string summary)
    {
        if (CheckingSessionHost.Session is { } session)
        {
            var viewId = activeView.Id.Value;
            session.EnsureView(viewId, activeView.Name, viewInfo?.SheetNo);
            session.RecordInvestigation(viewId, investigated, issues);

            var note = "\n\nRecorded against the active checking session - see the checklist window.";
            try
            {
                CheckingSessionHost.Autosave();
            }
            catch (Exception ex)
            {
                note += $"\n\nThe session could not be saved to disk:\n\n{ExceptionMessage.Full(ex)}";
            }

            CheckingSessionHost.Window?.Refresh();
            TaskDialog.Show(Title, summary + note);
            return Result.Succeeded;
        }

        string? outputPath;
        try
        {
            outputPath = IssueOutput.WriteNextToModel(doc, issues, "check_dimensions", Title);
        }
        catch (Exception ex)
        {
            TaskDialog.Show(Title, $"{summary}\n\nBut the results file could not be written:\n\n{ExceptionMessage.Full(ex)}");
            return Result.Succeeded;
        }

        TaskDialog.Show(
            Title,
            outputPath is null
                ? $"{summary}\n\nSave cancelled - nothing written."
                : $"{summary}\n\nWritten to (JSON, CSV and BCF, same folder):\n{outputPath}");
        return Result.Succeeded;
    }

    /// <summary>
    /// The piles in this view, for the chain bearing check. Category scope
    /// is config, never hardcoded, and an unresolvable name is reported
    /// rather than dropped (§19).
    /// </summary>
    private static List<ElementMetadata> CollectPiles(
        Document doc, View activeView, RuleConfig config, List<string> unresolved, out string? error)
    {
        error = null;
        try
        {
            var (categories, allModelCategories, unresolvedNames) = CategoryScope.Resolve(config);
            unresolved.AddRange(unresolvedNames);
            var result = RevitMetadataElementSource.Collect(
                doc,
                categories: categories,
                populateLivePosition: true,
                scopeView: activeView,
                allModelCategories: allModelCategories);
            return result.Elements;
        }
        catch (Exception ex)
        {
            error = $"pile collection: {ExceptionMessage.Full(ex)}";
            return new List<ElementMetadata>();
        }
    }
}
