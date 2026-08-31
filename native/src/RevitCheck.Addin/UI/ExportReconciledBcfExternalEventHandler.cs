using Autodesk.Revit.UI;
using RevitCheck.Addin.Commands;

namespace RevitCheck.Addin.UI;

/// <summary>
/// The checklist window's Export Reconciled BCF button, run through the
/// standard <see cref="IExternalEventHandler"/> pattern - see
/// <see cref="OpenViewExternalEventHandler"/>'s own remarks for why a
/// modeless window's button needs this at all.
/// </summary>
/// <remarks>
/// Writes <see cref="Core.Reporting.CheckingSession.ExportableConfirmedProblems"/>
/// via <see cref="IssueOutput.WriteNextToModel"/> with <c>writeBcf: true</c>
/// - the only list this session ever exports to BCF/Forma, per PLANNING.md
/// §14's "Product-shape correction" (dimension triage is candidates, not
/// verdicts; only a reconciled confirmed problem should ship). Manual-review
/// and still-open-triage items are written alongside as JSON/CSV only
/// (<see cref="IssueOutput.WriteSibling"/>), for a human to read at export
/// time rather than block the cycle earlier - the "a view landing on
/// NeedsManualReview counts as done" decision PLANNING.md §16 records.
/// </remarks>
internal sealed class ExportReconciledBcfExternalEventHandler : IExternalEventHandler
{
    public void Execute(UIApplication app)
    {
        var session = CheckingSessionHost.Session;
        var doc = app.ActiveUIDocument?.Document;
        if (session is null || doc is null)
        {
            TaskDialog.Show("RevitCheck - Reconciled Problems", "No active checking session to export - run Dimension Triage first.");
            return;
        }

        var confirmed = session.ExportableConfirmedProblems();
        var manualReview = session.ExportableManualReview();
        var stillOpen = session.ExportableStillOpenTriage();
        var manualResolutions = session.ExportableManualResolutions();

        string? jsonPath;
        try
        {
            jsonPath = IssueOutput.WriteNextToModel(doc, confirmed, "reconciled", "RevitCheck - Reconciled Problems");
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitCheck - Reconciled Problems", $"Could not write the export:\n\n{ExceptionMessage.Full(ex)}");
            return;
        }

        if (jsonPath is null)
        {
            TaskDialog.Show("RevitCheck - Reconciled Problems", "Save cancelled - nothing written.");
            return;
        }

        try
        {
            IssueOutput.WriteSibling(jsonPath, manualReview, "needs_manual_review");
            IssueOutput.WriteSibling(jsonPath, stillOpen, "still_open_triage");
            IssueOutput.WriteManualResolutionsSibling(jsonPath, manualResolutions);
        }
        catch (Exception ex)
        {
            // The confirmed-problems export (the important one) already
            // succeeded above - a failure writing the audit siblings is
            // worth reporting, not worth discarding what did write.
            TaskDialog.Show("RevitCheck - Reconciled Problems",
                $"{confirmed.Count} confirmed problem(s) written to BCF/JSON/CSV:\n{jsonPath}\n\n" +
                $"But the manual-review/still-open-triage/manual-resolutions siblings could not be written:\n\n{ExceptionMessage.Full(ex)}");
            CheckingSessionHost.Window?.Refresh();
            return;
        }

        TaskDialog.Show("RevitCheck - Reconciled Problems",
            $"{confirmed.Count} confirmed problem(s) written to BCF/JSON/CSV.\n" +
            $"{manualReview.Count} item(s) need manual review (JSON/CSV only).\n" +
            $"{stillOpen.Count} triage item(s) still open (JSON/CSV only).\n" +
            $"{manualResolutions.Count} view(s) manually dismissed (JSON only, audit trail).\n\n" +
            $"Written alongside:\n{jsonPath}");

        CheckingSessionHost.Window?.Refresh();
    }

    public string GetName() => "RevitCheck - Export Reconciled BCF";
}
