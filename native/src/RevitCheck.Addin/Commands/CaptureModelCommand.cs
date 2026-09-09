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
        var captureStopwatch = new System.Diagnostics.Stopwatch();
        try
        {
            // Every model category the document defines, not the five
            // DefaultCategories: a capture is what the per-model starter
            // config is derived from, so a category this code has never
            // heard of has to show up here or the config step cannot
            // reveal it. Per the user, 2026-09-07 - modellers put strange
            // content into models. See ModelCategoryIds's own remarks.
            // Survey positions are computed here, once per project, and
            // deliberately: without them a capture cannot replay either
            // pile check or be diffed against a later one, which is what
            // made the 2026-09-09 negative control impossible to audit
            // off-machine (§25). Same call the user already made about the
            // all-category sweep - "a one off time penalty is not a big
            // deal" - and the cost is reported rather than assumed, since
            // only elements with a LocationPoint pay it at all.
            captureStopwatch.Start();
            collectedMetadata = RevitMetadataElementSource.Collect(
                doc,
                captureScope.MappingScopeViewName,
                allModelCategories: true,
                populateLivePosition: true,
                scopeView: captureScope.ScopeView);
            collectedDimensions = RevitDimensionSource.Collect(doc, scopeView: captureScope.ScopeView);
            captureStopwatch.Stop();
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

        var positionNote = PositionCoverage(collectedMetadata.Elements, captureStopwatch.Elapsed);
        var starterNote = WriteStarterConfig(doc, model);
        var portableNote = WritePortableConfigBesideCapture(doc, savePath);

        TaskDialog.Show("RevitCheck - Capture Model",
            $"{collectedMetadata.Elements.Count} element(s), {collectedDimensions.Sheets.Count} sheet(s), " +
            $"{collectedDimensions.Views.Count} view(s), {collectedDimensions.Dimensions.Count} dimension(s), " +
            $"{collectedSchedules.Count} schedule(s) captured" +
            (extractionErrors.Count > 0 ? $", {extractionErrors.Count} extraction error(s)" : "") +
            $".\n\n{positionNote}\n\n{captureScope.Description}\n\nWritten to:\n{savePath}\n\n" +
            $"{starterNote}{portableNote}\n\n" +
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
    /// <summary>
    /// Copies this model's config next to the capture file just written, so
    /// the two travel together to Forma.
    /// </summary>
    /// <remarks>
    /// Added 2026-09-09, per the user: "the config needs to be able to be
    /// saved off the machine, to Forma so that nothing is relying on having
    /// a file that lives on someone's C: drive." A capture already leaves
    /// this machine that way; the config is the other half of reproducing a
    /// run, and until now existed only under LocalApplicationData. Writing
    /// it beside the capture makes exporting it free rather than a separate
    /// step someone has to remember.
    /// </remarks>
    /// <summary>
    /// How much of this capture actually carries geometry, and what that
    /// cost - reported rather than assumed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Added 2026-09-09 (§25).</b> Captures carried no element geometry
    /// at all - 0 of 36,626 elements on the real model - because
    /// <c>LocalPoint</c> shared a flag with the per-element
    /// <c>GetProjectPosition</c> call and Capture Model, reasonably, would
    /// not pay that. The consequence was that neither pile check could be
    /// replayed off-machine and two captures could not be diffed to find a
    /// moved element, which is exactly what the first negative control
    /// needed.
    /// </para>
    /// <para>
    /// Only elements with a <c>LocationPoint</c> get a position at all, so
    /// the real count is unknown in advance and worth stating: it says how
    /// much of a model a positional check can ever reach, and the elapsed
    /// time says whether paying for it once per project stays reasonable on
    /// a large cloud model. Measured, not guessed - the same discipline the
    /// all-category sweep was given.
    /// </para>
    /// </remarks>
    private static string PositionCoverage(List<ElementMetadata> elements, TimeSpan elapsed)
    {
        var withLocal = elements.Count(e => e.LocalPoint is not null);
        var withSurvey = elements.Count(e => e.ProjectPositionEastingMm is not null);

        var note =
            $"{withLocal} of {elements.Count} element(s) have a position in the model, " +
            $"{withSurvey} of those also in survey coordinates. " +
            $"Collection took {elapsed.TotalSeconds:0.#}s.";

        if (withLocal > 0 && withSurvey == 0)
        {
            note +=
                " No survey position could be computed for any of them - check this model has a Survey Point " +
                "set, since the pile checks compare against schedule Eastings/Northings.";
        }

        return note;
    }

    private static string WritePortableConfigBesideCapture(Document doc, string capturePath)
    {
        try
        {
            var json = RuleConfigSource.ReadRaw(doc);
            if (json is null)
            {
                return string.Empty;
            }

            var folder = Path.GetDirectoryName(capturePath);
            if (string.IsNullOrEmpty(folder))
            {
                return string.Empty;
            }

            var portable = Path.Combine(folder, RuleConfigSource.FileNameFor(doc));
            File.WriteAllText(portable, json);
            return
                $"\n\nThis model's config was copied next to the capture:\n{portable}\nUpload it to Forma " +
                "alongside the capture - it is the other half of reproducing this run, and Rule Config > " +
                "Import puts it back on any machine.";
        }
        catch (Exception ex)
        {
            return $"\n\n(Could not copy the config next to the capture: {ExceptionMessage.Full(ex)})";
        }
    }

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
            // Still never overwritten - a project's own settings are not
            // this button's to discard. But an existing file written before
            // 2026-09-09 records every setting as it stood then, so it
            // silently overrides later recalibration; saying which settings
            // it pins is what turns that from invisible into a decision.
            var message = $"This model already has a config, left untouched:\n{path}";
            try
            {
                var pinned = RuleConfigSerializer.DescribeOverrides(File.ReadAllText(path));
                if (pinned.Count > 0)
                {
                    message +=
                        $"\n\nIt pins {pinned.Count} setting(s) away from the current built-in defaults:\n  " +
                        string.Join("\n  ", pinned) +
                        "\n\nA setting recorded here does not track later recalibration. If any of these were " +
                        "not chosen deliberately for this project, delete the file and run Capture Model again " +
                        "to write a fresh starter config.";
                }
            }
            catch (Exception ex)
            {
                message += $"\n\n(Could not read what it pins: {ExceptionMessage.Full(ex)})";
            }

            return message;
        }

        try
        {
            var starter = RuleConfigStarter.Build(model);
            RuleConfigSerializer.Save(starter.Config, path, DocumentPaths.SafeBaseName(doc));
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
