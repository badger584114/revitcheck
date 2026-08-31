using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitCheck.Addin.Adapters;
using RevitCheck.Addin.UI;
using RevitCheck.Core.Checks;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Issues;
using RevitCheck.Core.Reporting;

namespace RevitCheck.Addin.Commands;

/// <summary>
/// The combined dimension triage button (PLANNING.md §16 Stage 3) -
/// replaces <c>DimensionProvenanceCommand</c>/<c>DimensionOverrideConsistencyCommand</c>
/// (deleted alongside this file, per the user's own "combine the dimension
/// buttons into a single button" wording, read as replacement not
/// addition). Runs both underlying checks unchanged, but opens the
/// interactive checking-session checklist window instead of writing a
/// static one-shot JSON/CSV report - triage now feeds a live session a
/// reviewer works through, rather than a file nobody comes back to.
/// </summary>
/// <remarks>
/// Whole-document, not view-scoped - unchanged from the two commands this
/// replaces: surveying the whole drawing set for triage is the entire
/// point of this button (see <see cref="RevitDimensionSource"/>'s own
/// remarks on why its two callers "never pass" <c>scopeView</c>). The two
/// pile investigation commands are what scope to the active view.
/// </remarks>
[Transaction(TransactionMode.ReadOnly)]
public class DimensionTriageCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var doc = commandData.Application.ActiveUIDocument?.Document;
        if (doc is null)
        {
            message = "No active document.";
            return Result.Failed;
        }

        DimensionCollectionResult collected;
        try
        {
            collected = RevitDimensionSource.Collect(doc);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitCheck - Dimension Triage", $"Could not collect the model:\n\n{ExceptionMessage.Full(ex)}");
            return Result.Failed;
        }

        var model = new RevitModel
        {
            DocTitle = doc.Title,
            RevitVersion = commandData.Application.Application.VersionNumber,
            CapturedAt = DateTime.UtcNow.ToString("O"),
            Sheets = collected.Sheets,
            Views = collected.Views,
            Dimensions = collected.Dimensions,
            ExtractionErrors = collected.ExtractionErrors,
            ExcludedWorksets = collected.ExcludedWorksets,
        };

        var config = new RuleConfig();
        var issues = DimensionProvenanceCheck.Run(model, config)
            .Concat(DimensionOverrideConsistencyCheck.Run(model, config))
            .ToList();

        var sessionPath = CheckingSessionHost.SessionFilePathFor(doc);
        var session = ResolveSession(sessionPath, issues, config);

        CheckingSessionHost.Session = session;
        CheckingSessionHost.SessionFilePath = sessionPath;

        try
        {
            CheckingSessionHost.Autosave();
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitCheck - Dimension Triage",
                $"Session built ({session.Views.Count} view(s) needing attention), but could not be saved to " +
                $"disk:\n\n{ExceptionMessage.Full(ex)}\n\nIt will not survive a Revit restart until this succeeds.");
        }

        if (CheckingSessionHost.Window is null)
        {
            CheckingSessionHost.Window = new ChecklistWindow();
            CheckingSessionHost.Window.Show();
        }
        else
        {
            CheckingSessionHost.Window.Refresh();
            CheckingSessionHost.Window.Activate();
        }

        return Result.Succeeded;
    }

    /// <summary>
    /// Offers to resume a saved session from a prior Revit run of this
    /// same document rather than silently discarding accumulated
    /// investigation/dismissal work every time this command runs.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT re-validate a resumed session against the
    /// model state just collected - no model-change fingerprinting is
    /// built in this pass (PLANNING.md §16 Stage 3, a real, deliberately
    /// deferred limitation). A resumed session's triage/investigation
    /// state is only as current as whenever it was last saved; the dialog
    /// says so plainly rather than glossing over it.
    /// </remarks>
    private static CheckingSession ResolveSession(string sessionPath, List<Issue> freshIssues, RuleConfig config)
    {
        if (!File.Exists(sessionPath))
        {
            return CheckingSession.Start(freshIssues, config);
        }

        var dialog = new TaskDialog("RevitCheck - Dimension Triage")
        {
            MainInstruction = "A saved checking session exists for this document.",
            MainContent =
                "Resuming picks up exactly where the saved session left off (including any manual dismissals " +
                "and investigation results already recorded) - it is not re-validated against the model as it " +
                "stands right now. Starting fresh discards it and builds a new session from this run's triage.",
            CommonButtons = TaskDialogCommonButtons.Cancel,
        };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Resume the saved session");
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Start fresh (discard the saved session)");
        // DefaultButton has to be set AFTER the command links exist, not in
        // the object initializer above - a real bug found on the Revit
        // machine, 2026-08-31: TaskDialog validates DefaultButton against
        // whatever buttons the dialog already has at the moment it's
        // assigned, and an object initializer runs every member in
        // declaration order before any AddCommandLink call below it can
        // run, so DefaultButton = CommandLink1 there always throws
        // ("Corresponding button not found. Parameter name: defaultButton") -
        // there is no button yet when that assignment happens.
        dialog.DefaultButton = TaskDialogResult.CommandLink1;

        var result = dialog.Show();
        if (result != TaskDialogResult.CommandLink1)
        {
            return CheckingSession.Start(freshIssues, config);
        }

        try
        {
            return CheckingSessionSerializer.Load(sessionPath);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitCheck - Dimension Triage",
                $"Could not load the saved session, starting fresh instead:\n\n{ExceptionMessage.Full(ex)}");
            return CheckingSession.Start(freshIssues, config);
        }
    }
}
