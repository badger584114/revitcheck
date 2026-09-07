using System.IO;
using RevitCheck.Core.Checks;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using RevitCheck.Addin.Adapters;
using RevitCheck.Core.Capture;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Mapping;

namespace RevitCheck.Addin.Commands;

/// <summary>
/// Writes a full model sweep to a <see cref="CaptureSerializer"/> JSON file
/// - the native add-in's counterpart to the Python side's Capture Model
/// button and its whole dev-loop role (native/README.md, CLAUDE.md's
/// "Development setup"): a point-in-time snapshot, not a live sync, taken
/// once and then iterated against off the Revit machine as many times as
/// needed. Existed to unblock building output/reporting logic (grouping by
/// family/type/field/values) against real element diversity without a
/// Revit-machine round trip for every change - confirmed with the user
/// 2026-08-24.
/// </summary>
/// <remarks>
/// Captures both halves of <see cref="RevitModel"/> in one file, matching
/// Python's own <c>capture.py</c> (which never split metadata from
/// sheets/views/dimensions into separate captures either) - extended
/// 2026-08-25 for the dimension-adapter port, having started as
/// metadata-only while that was the only adapter that existed.
/// <para>
/// <b>Scope is chosen per run</b> (<see cref="ChooseScope"/>): the whole
/// document, the active view, or a mapping file's own scope view. A mapping
/// was mandatory until 2026-09-07 and only ever read for its
/// <see cref="ParameterMapping.ScopeViewName"/> - defensible while a capture
/// existed to serve metadata reconciliation, but circular once a capture
/// also became the discovery step that writes a model's starter
/// <see cref="RuleConfig"/>: a model being set up for the first time has no
/// mapping file, so the artefact was required by the very step that produces
/// what is needed to build it. Neither the mapping's <c>Fields</c> nor any
/// CSV are used here.
/// </para>
/// </remarks>
[Transaction(TransactionMode.ReadOnly)]
public class CaptureModelCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiDoc = commandData.Application.ActiveUIDocument;
        var doc = uiDoc?.Document;
        if (doc is null)
        {
            message = "No active document.";
            return Result.Failed;
        }

        var scope = ChooseScope(doc, uiDoc!);
        if (scope is not { } captureScope)
        {
            return Result.Cancelled;
        }

        MetadataCollectionResult collectedMetadata;
        DimensionCollectionResult collectedDimensions;
        try
        {
            // Every model category the document defines, not the five
            // DefaultCategories: a capture is what the per-model starter
            // config is derived from, so a category this code has never
            // heard of has to show up here or the config step cannot
            // reveal it. Per the user, 2026-09-07 - modellers put strange
            // content into models. See ModelCategoryIds's own remarks.
            collectedMetadata = RevitMetadataElementSource.Collect(
                doc,
                captureScope.MappingScopeViewName,
                allModelCategories: true,
                scopeView: captureScope.ScopeView);
            collectedDimensions = RevitDimensionSource.Collect(doc, scopeView: captureScope.ScopeView);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitCheck - Capture Model", $"Could not collect the model:\n\n{ExceptionMessage.Full(ex)}");
            return Result.Failed;
        }

        var extractionErrors = new List<string>(collectedMetadata.ExtractionErrors);
        extractionErrors.AddRange(collectedDimensions.ExtractionErrors);

        // Schedule HEADERS only - three empty candidate lists mean "no
        // schedule qualifies for a body read" (RevitScheduleSource's own
        // null-vs-empty distinction), which is all the starter config needs
        // and avoids the expensive per-cell read that a ReadOnly
        // transaction cannot perform anyway (PLANNING.md §16).
        var scheduleErrors = new List<string>();
        var collectedSchedules = RevitScheduleSource.Collect(
            doc, scheduleErrors, new List<string>(), new List<string>(), new List<string>());
        extractionErrors.AddRange(scheduleErrors);

        var model = new RevitModel
        {
            DocTitle = doc.Title,
            RevitVersion = commandData.Application.Application.VersionNumber,
            CapturedAt = DateTime.UtcNow.ToString("O"),
            Elements = collectedMetadata.Elements,
            Sheets = collectedDimensions.Sheets,
            Views = collectedDimensions.Views,
            Dimensions = collectedDimensions.Dimensions,
            Schedules = collectedSchedules,
            ExtractionErrors = extractionErrors,
            ExcludedWorksets = collectedDimensions.ExcludedWorksets,
        };

        var savePath = PromptForSaveLocation(doc);
        if (savePath is null)
        {
            return Result.Cancelled;
        }

        try
        {
            CaptureSerializer.Save(model, savePath);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitCheck - Capture Model", $"Could not write the capture file:\n\n{ExceptionMessage.Full(ex)}");
            return Result.Failed;
        }

        var starterNote = WriteStarterConfig(doc, model);

        TaskDialog.Show("RevitCheck - Capture Model",
            $"{collectedMetadata.Elements.Count} element(s), {collectedDimensions.Sheets.Count} sheet(s), " +
            $"{collectedDimensions.Views.Count} view(s), {collectedDimensions.Dimensions.Count} dimension(s), " +
            $"{collectedSchedules.Count} schedule(s) captured" +
            (extractionErrors.Count > 0 ? $", {extractionErrors.Count} extraction error(s)" : "") +
            $".\n\n{captureScope.Description}\n\nWritten to:\n{savePath}\n\n{starterNote}\n\n" +
            "Treat this file like a real model capture (PLANNING.md §2) - it contains real " +
            "parameter values from a real project.");

        return Result.Succeeded;
    }

    private static string? PromptForFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>
    /// Writes a starter <see cref="RuleConfig"/> for this model if it
    /// hasn't got one, so the checks can be configured for a new project by
    /// editing a file rather than rebuilding the add-in. Never overwrites
    /// an existing one - a person's own edits outrank anything derived
    /// automatically.
    /// </summary>
    /// <remarks>
    /// This is the loop metadata reconciliation has always had (capture the
    /// model, build a per-model mapping from it, let a human finish it) and
    /// which the checks built afterwards never joined - see
    /// <see cref="RuleConfigStarter"/>'s remarks.
    /// </remarks>
    private static string WriteStarterConfig(Document doc, RevitModel model)
    {
        string path;
        try
        {
            path = RuleConfigSource.PathFor(doc);
        }
        catch (Exception ex)
        {
            return $"No starter config written (could not resolve a path: {ex.Message}).";
        }

        if (File.Exists(path))
        {
            return $"This model already has a config, left untouched:\n{path}";
        }

        try
        {
            var starter = RuleConfigStarter.Build(model);
            RuleConfigSerializer.Save(starter.Config, path);
            return
                $"Starter config written to:\n{path}\n\nReview it before trusting a run - " +
                string.Join("\n\n", starter.Diagnostics);
        }
        catch (Exception ex)
        {
            return $"No starter config written: {ExceptionMessage.Full(ex)}";
        }
    }

    /// <summary>What this capture covers - see <see cref="ChooseScope"/>.</summary>
    private readonly struct CaptureScope
    {
        public CaptureScope(string? mappingScopeViewName, View? scopeView, string description)
        {
            MappingScopeViewName = mappingScopeViewName;
            ScopeView = scopeView;
            Description = description;
        }

        /// <summary>A mapping file's own scope view name, when one was chosen. Null otherwise.</summary>
        public string? MappingScopeViewName { get; }

        /// <summary>A live view to scope to, when the active view was chosen. Null means the whole document.</summary>
        public View? ScopeView { get; }

        /// <summary>One line for the run's own output, so a capture always says what it covered.</summary>
        public string Description { get; }
    }

    /// <summary>
    /// Asks what this capture should cover, returning null if the user
    /// cancelled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The mapping file used to be mandatory, and that was circular</b>
    /// (reported 2026-09-07). It was read only for
    /// <see cref="ParameterMapping.ScopeViewName"/>, which made sense while
    /// a capture existed to serve metadata reconciliation - the metadata
    /// scope should match what a real reconciliation run would see. But a
    /// capture is now also the discovery step that writes a model's starter
    /// <see cref="RuleConfig"/>, and a model being set up for the first
    /// time has no mapping file yet: the artefact was required by the very
    /// step that produces the information needed to build it.
    /// </para>
    /// <para>
    /// So a mapping is now one option among three rather than a gate. The
    /// whole-document sweep is first because it is the right answer for the
    /// case that was blocked - a model nobody has configured yet, where the
    /// point is to find out what is actually in it.
    /// </para>
    /// </remarks>
    private static CaptureScope? ChooseScope(Document doc, UIDocument uiDoc)
    {
        var dialog = new TaskDialog("RevitCheck - Capture Model")
        {
            MainInstruction = "What should this capture cover?",
            MainContent =
                "A capture is a point-in-time snapshot for working off the Revit machine, and it is what a " +
                "model's starter config is built from.",
            CommonButtons = TaskDialogCommonButtons.Cancel,
        };

        dialog.AddCommandLink(
            TaskDialogCommandLinkId.CommandLink1,
            "The whole document",
            "Everything, in every model category. Best for a model being set up for the first time - it is what " +
            "reveals which categories this project actually uses. Slowest.");
        dialog.AddCommandLink(
            TaskDialogCommandLinkId.CommandLink2,
            "The active view only",
            $"Just what is visible in '{SafeViewName(uiDoc)}' - matches what the per-view check buttons themselves see.");
        dialog.AddCommandLink(
            TaskDialogCommandLinkId.CommandLink3,
            "A mapping file's scope view",
            "Matches what a Metadata Reconciliation run using that mapping would see. Needs a mapping file to " +
            "already exist.");

        // DefaultButton must be set after the command links exist - a real
        // crash on the Revit machine, 2026-08-31 (PLANNING.md §16):
        // TaskDialog validates it against the links defined so far, and an
        // object initialiser runs before any AddCommandLink call.
        dialog.DefaultButton = TaskDialogResult.CommandLink1;

        switch (dialog.Show())
        {
            case TaskDialogResult.CommandLink1:
                return new CaptureScope(null, null, "Captured the whole document, every model category.");

            case TaskDialogResult.CommandLink2:
                var activeView = uiDoc.ActiveView;
                if (activeView is null)
                {
                    TaskDialog.Show("RevitCheck - Capture Model", "There is no active view to scope to.");
                    return null;
                }

                return new CaptureScope(null, activeView, $"Captured the active view only: '{activeView.Name}'.");

            case TaskDialogResult.CommandLink3:
                var mappingPath = PromptForFile(
                    "RevitCheck - select mapping file (for its scope view only)",
                    "Mapping JSON (*.mapping.json)|*.mapping.json|JSON files (*.json)|*.json|All files (*.*)|*.*");
                if (mappingPath is null)
                {
                    return null;
                }

                try
                {
                    var mapping = ParameterMappingSerializer.Load(mappingPath);
                    return new CaptureScope(
                        mapping.ScopeViewName,
                        null,
                        string.IsNullOrWhiteSpace(mapping.ScopeViewName)
                            ? "Captured the whole document - the chosen mapping names no scope view."
                            : $"Captured the mapping's scope view: '{mapping.ScopeViewName}'.");
                }
                catch (Exception ex)
                {
                    TaskDialog.Show("RevitCheck - Capture Model", $"Could not load mapping:\n\n{ExceptionMessage.Full(ex)}");
                    return null;
                }

            default:
                return null;
        }
    }

    private static string SafeViewName(UIDocument uiDoc)
    {
        try
        {
            return uiDoc.ActiveView?.Name ?? "(no active view)";
        }
        catch
        {
            return "(no active view)";
        }
    }

    private static string? PromptForSaveLocation(Document doc)
    {
        var suggestedName = DocumentPaths.SafeBaseName(doc);

        var dialog = new SaveFileDialog
        {
            Title = "RevitCheck - save capture as",
            FileName = $"{suggestedName}.capture.json",
            DefaultExt = ".json",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

}
