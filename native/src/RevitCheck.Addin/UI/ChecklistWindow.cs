using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using RevitCheck.Addin.Commands;
using RevitCheck.Core.Issues;
using RevitCheck.Core.Reporting;

namespace RevitCheck.Addin.UI;

/// <summary>
/// The interactive checking workflow's checklist window (PLANNING.md §16
/// Stage 3). Shows <see cref="CheckingSessionHost.Session"/>'s rows,
/// grouped by sheet; lets a reviewer open a flagged view, bulk-dismiss a
/// whole sheet by human judgement, record a manual verdict on one specific
/// dimension while checking it against the drawing (<see cref="OnMarkDetailVerdictClick"/>,
/// added 2026-08-31 - real user feedback that there was no way to do this
/// at all, only a whole-view dismissal), and export the reconciled BCF
/// once done cycling through every view.
/// </summary>
/// <remarks>
/// <para>
/// <b>No .xaml file, no SDK change.</b> <c>RevitCheck.Addin</c> is a plain
/// <c>Microsoft.NET.Sdk</c> project with no MSBuild XAML compilation wired
/// up (its <c>.csproj</c> references <c>PresentationCore</c>/
/// <c>PresentationFramework</c>/<c>WindowsBase</c> directly, for
/// <c>BitmapImage</c> ribbon icons and <c>Microsoft.Win32.SaveFileDialog</c>
/// - not for a full WPF app model). Switching to
/// <c>Microsoft.NET.Sdk.WindowsDesktop</c>/<c>UseWPF</c> to get XAML
/// compilation would be a real, avoidable toolchain risk on this
/// net48+Nice3point combination - PLANNING.md §16's own design deliberately
/// rules it out. Every element below is built with plain C# constructors
/// instead, the same way <see cref="ReasonPromptWindow"/> is.
/// </para>
/// <para>
/// <b>Modeless, not modal</b> (<c>Show()</c>, never <c>ShowDialog()</c>) -
/// Revit itself must stay usable while this is open, since the whole point
/// is cycling through views in the model with the window still visible.
/// Revit API calls triggered from this window's own buttons (Open View,
/// Export) go through <see cref="RevitCheckExternalEvents"/> - see
/// <see cref="OpenViewExternalEventHandler"/>'s remarks for why. "Mark
/// Selected Resolved..." needs no <see cref="Autodesk.Revit.UI.ExternalEvent"/>
/// at all - it's a pure <see cref="CheckingSession"/> mutation plus an
/// autosave and a local <see cref="Refresh"/>, no Revit API call involved.
/// </para>
/// <para>
/// <b>"Open View (per row)" is implemented as a toolbar button acting on
/// the current single selection</b>, not a button embedded in every
/// <see cref="ListView"/> row - the plan's own UI sketch names the
/// mechanism ("open a flagged view... while it's active"), not a specific
/// widget layout, and a per-row embedded button in a <see cref="ListView"/>
/// built entirely in code would need a <c>DataTemplate</c> constructed via
/// <c>FrameworkElementFactory</c> - real extra complexity with no way to
/// visually verify it without the Revit machine this whole feature is
/// built ahead of. "Mark Selected Resolved..." already establishes the
/// toolbar-button-over-multi-selection pattern for the bulk case; Open
/// View reuses it for the single-row case rather than introducing a second
/// interaction model.
/// </para>
/// <para>
/// <b><see cref="Refresh"/> rebuilds every row wholesale</b> from
/// <see cref="CheckingSessionHost.Session"/> on every call - no
/// data-binding/ViewModel/<c>INotifyPropertyChanged</c> infrastructure,
/// matching PLANNING.md §16 Stage 3's own design ("nothing like it exists
/// elsewhere in this codebase and this doesn't need to be the first"). The
/// <see cref="GridViewColumn.DisplayMemberBinding"/> calls below are the
/// standard minimal way to show columns in a <see cref="ListView"/>/
/// <see cref="GridView"/> without XAML - a one-way read of a plain
/// property on a fresh, disposable row object, not a ViewModel layer.
/// </para>
/// </remarks>
internal sealed class ChecklistWindow : Window
{
    private readonly ListView _listView;
    private readonly TextBlock _summary;
    private readonly CheckBox _showResolved;
    private readonly ListView _detailsListView;

    public ChecklistWindow()
    {
        Title = "RevitCheck - Dimension Triage Checklist";
        Width = 900;
        Height = 760;

        // Owned by Revit's own main window via its process handle - the
        // simplest way to parent a modeless WPF window from a Revit add-in
        // without a dependency on Autodesk.Windows/AdWindows.dll (no
        // existing precedent for that dependency anywhere in this
        // codebase). Cosmetic only if it fails - an unparented window
        // still works, just doesn't minimize/restore with Revit. Unconfirmed
        // until Stage 4's real run, same as every other Revit-machine-only
        // behaviour in this codebase.
        try
        {
            new WindowInteropHelper(this).Owner = Process.GetCurrentProcess().MainWindowHandle;
        }
        catch
        {
            // See remark above - not worth failing window construction over.
        }

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _summary = new TextBlock { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(_summary, 0);
        root.Children.Add(_summary);

        // Real user feedback, 2026-08-31 Stage 4: a real drawing set has
        // hundreds of views, and mixing already-resolved rows in with the
        // ones still needing attention makes the list unworkable. Hidden
        // by default so the working list is only what's left to do; the
        // full picture (including resolved rows) is one checkbox away, not
        // a separate window/tab - the simplest change that actually
        // addresses "hard to go through hundreds of issues".
        _showResolved = new CheckBox
        {
            Content = "Show resolved rows too",
            Margin = new Thickness(8, 0, 8, 8),
        };
        _showResolved.Checked += (_, _) => Refresh();
        _showResolved.Unchecked += (_, _) => Refresh();
        Grid.SetRow(_showResolved, 1);
        root.Children.Add(_showResolved);

        _listView = BuildListView();
        _listView.SelectionChanged += (_, _) => UpdateDetails();
        Grid.SetRow(_listView, 2);
        root.Children.Add(_listView);

        // Real user feedback, 2026-08-31 Stage 4: a bare status/count row
        // ("needs manual review, triage 2, manual review 4") gives no way
        // to know what those actually are or how to act on them. This pane
        // shows the selected row's real issue descriptions - what to go
        // check, not just how many - and is itself selectable so a
        // specific dimension can be marked resolved/confirmed (see
        // DetailsToolbar below and UpdateDetails' own remarks) - a second
        // real gap, also from real use: there was no way at all to weigh
        // in on one specific dimension while manually checking it, only a
        // whole view via Mark Selected Resolved.
        var detailsPanel = new DockPanel { Margin = new Thickness(8, 4, 8, 8) };
        var detailsLabel = new TextBlock
        {
            Text = "Details for the selected view - select one or more issues below to act on them individually:",
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 4),
            TextWrapping = TextWrapping.Wrap,
        };
        DockPanel.SetDock(detailsLabel, Dock.Top);
        detailsPanel.Children.Add(detailsLabel);
        _detailsListView = BuildDetailsListView();
        detailsPanel.Children.Add(_detailsListView);
        Grid.SetRow(detailsPanel, 3);
        root.Children.Add(detailsPanel);

        var detailsToolbar = BuildDetailsToolbar();
        Grid.SetRow(detailsToolbar, 4);
        root.Children.Add(detailsToolbar);

        var toolbar = BuildToolbar();
        Grid.SetRow(toolbar, 5);
        root.Children.Add(toolbar);

        var exportBar = BuildExportBar();
        Grid.SetRow(exportBar, 6);
        root.Children.Add(exportBar);

        Content = root;

        Closing += (_, _) => CheckingSessionHost.Window = null;

        Refresh();
    }

    private static ListView BuildListView()
    {
        var gridView = new GridView();
        gridView.Columns.Add(Column("Sheet No", nameof(ChecklistRow.SheetNo), 90));
        gridView.Columns.Add(Column("View Name", nameof(ChecklistRow.ViewName), 280));
        gridView.Columns.Add(Column("Status", nameof(ChecklistRow.StatusText), 110));
        gridView.Columns.Add(Column("Still Open", nameof(ChecklistRow.StillOpenCount), 75));
        gridView.Columns.Add(Column("Confirmed", nameof(ChecklistRow.ConfirmedCount), 80));
        gridView.Columns.Add(Column("Manual Review", nameof(ChecklistRow.ManualReviewCount), 100));

        return new ListView
        {
            Margin = new Thickness(8, 0, 8, 8),
            SelectionMode = SelectionMode.Extended,
            View = gridView,
        };
    }

    private static GridViewColumn Column(string header, string bindingPath, double width) =>
        new()
        {
            Header = header,
            Width = width,
            DisplayMemberBinding = new Binding(bindingPath),
        };

    private static ListView BuildDetailsListView()
    {
        var gridView = new GridView();
        gridView.Columns.Add(Column("Section", nameof(DetailRow.Section), 130));
        gridView.Columns.Add(Column("Rule", nameof(DetailRow.RuleId), 220));
        gridView.Columns.Add(Column("Severity", nameof(DetailRow.Severity), 70));
        gridView.Columns.Add(Column("Element", nameof(DetailRow.ElementIdText), 80));
        gridView.Columns.Add(Column("Description", nameof(DetailRow.Description), 340));

        return new ListView
        {
            SelectionMode = SelectionMode.Extended,
            View = gridView,
        };
    }

    /// <summary>
    /// "Mark Selected Issue(s) Resolved/Confirmed Problem" - real user
    /// feedback, 2026-08-31: manually checking a pile dimension against
    /// the drawing had no way to record the verdict on that specific
    /// dimension, only a whole view. Both act on <see cref="_detailsListView"/>'s
    /// selection, a different list from <see cref="_listView"/>'s own
    /// toolbar (<see cref="BuildToolbar"/>) - kept as a visually separate
    /// row so "act on this view" and "act on this specific issue" don't
    /// read as the same action.
    /// </summary>
    private StackPanel BuildDetailsToolbar()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 8, 8) };

        var resolve = new Button
        {
            Content = "Mark Selected Issue(s) Resolved",
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 8, 0),
        };
        resolve.Click += (_, _) => OnMarkDetailVerdictClick(isConfirmedProblem: false);
        panel.Children.Add(resolve);

        var confirm = new Button
        {
            Content = "Mark Selected Issue(s) Confirmed Problem",
            Padding = new Thickness(8, 4, 8, 4),
        };
        confirm.Click += (_, _) => OnMarkDetailVerdictClick(isConfirmedProblem: true);
        panel.Children.Add(confirm);

        return panel;
    }

    private StackPanel BuildToolbar()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 8, 8) };

        var openView = new Button
        {
            Content = "Open View",
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 8, 0),
        };
        openView.Click += OnOpenViewClick;
        panel.Children.Add(openView);

        var resolve = new Button { Content = "Mark Selected Resolved...", Padding = new Thickness(8, 4, 8, 4) };
        resolve.Click += OnMarkSelectedResolvedClick;
        panel.Children.Add(resolve);

        return panel;
    }

    private StackPanel BuildExportBar()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8, 0, 8, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var export = new Button { Content = "Export Reconciled BCF", Padding = new Thickness(12, 6, 12, 6) };
        export.Click += OnExportClick;
        panel.Children.Add(export);
        return panel;
    }

    /// <summary>
    /// Rebuilds every row from <see cref="CheckingSessionHost.Session"/>
    /// wholesale - see class remarks. Safe to call after any session
    /// mutation: a pile command's <c>RecordInvestigation</c>, this window's
    /// own <c>ResolveManually</c>, a fresh <c>DimensionTriageCommand</c>
    /// load/build.
    /// </summary>
    public void Refresh()
    {
        var session = CheckingSessionHost.Session;
        if (session is null)
        {
            _listView.ItemsSource = null;
            _summary.Text = "No active checking session.";
            UpdateDetails();
            return;
        }

        // Reassigning ItemsSource below (see class remarks - wholesale
        // rebuild, no data-binding) loses WPF's own selection every time,
        // which fights the exact workflow this window exists for: run a
        // pile check against the view you already have selected, and see
        // its row update in place. Re-select by ViewId after rebuilding
        // instead of leaving the reviewer's spot in the list to reset on
        // every investigation run.
        var previouslySelectedViewIds = new HashSet<long>(
            _listView.SelectedItems.Cast<ChecklistRow>().Select(r => r.ViewId));

        var allRows = session.Views
            .OrderBy(v => v.SheetNo ?? "", StringComparer.Ordinal)
            .ThenBy(v => v.ViewName ?? "", StringComparer.Ordinal)
            .Select(v => new ChecklistRow
            {
                ViewId = v.ViewId,
                SheetNo = v.SheetNo ?? "(no sheet)",
                ViewName = v.ViewName ?? "",
                Status = v.Status,
                // The current, still-outstanding count - not the original
                // triage count from before any investigation. Real user
                // feedback, 2026-08-31: showing the frozen original count
                // here (unlike the Confirmed/Manual Review columns, which
                // already read the live reconciled state) made a row look
                // unchanged after real investigation work had actually
                // resolved most of it.
                StillOpenCount = v.LastReconciliation.StillOpenTriage.Count,
                ConfirmedCount = v.LastReconciliation.ConfirmedProblems.Count + v.OtherInvestigationFindings.Count,
                ManualReviewCount = v.LastReconciliation.NeedsManualReview.Count,
            })
            .ToList();

        var pending = allRows.Count(r => r.Status == ViewInvestigationStatus.Pending);
        var flagged = allRows.Count(r => r.Status == ViewInvestigationStatus.Flagged);
        var manualReview = allRows.Count(r => r.Status == ViewInvestigationStatus.NeedsManualReview);
        var resolvedManually = allRows.Count(r => r.Status == ViewInvestigationStatus.ResolvedManually);
        var resolved = allRows.Count(r => r.Status == ViewInvestigationStatus.Resolved);

        // Resolved rows (Resolved and ResolvedManually) are hidden from the
        // list itself by default - see the _showResolved checkbox's own
        // remarks. Counts in the summary line always cover every row
        // regardless, so the true picture is never hidden, just the
        // clutter in the working list.
        var isDone = new Func<ChecklistRow, bool>(r =>
            r.Status is ViewInvestigationStatus.Resolved or ViewInvestigationStatus.ResolvedManually);
        var shownRows = _showResolved.IsChecked == true ? allRows : allRows.Where(r => !isDone(r)).ToList();
        var hiddenCount = allRows.Count - shownRows.Count;

        _listView.ItemsSource = shownRows;
        foreach (var row in shownRows.Where(r => previouslySelectedViewIds.Contains(r.ViewId)))
        {
            _listView.SelectedItems.Add(row);
        }

        _summary.Text =
            $"{allRows.Count} view(s): {pending} pending, {flagged} flagged, {manualReview} need manual review, " +
            $"{resolvedManually} manually dismissed, {resolved} resolved." +
            (hiddenCount > 0 ? $" ({hiddenCount} resolved row(s) hidden - tick the box below to show them.)" : "") +
            (session.ModelWideNotes.Count > 0
                ? $" ({session.ModelWideNotes.Count} model-wide note(s) not shown here - see the export.)"
                : "");

        UpdateDetails();
    }

    /// <summary>
    /// Shows the real issue descriptions behind the selected row's status
    /// and counts - added 2026-08-31, real user feedback: a row reading
    /// "needs manual review, still open 2, manual review 4" gives no way
    /// to know what those actually are or how to act on them without this.
    /// </summary>
    /// <remarks>
    /// <b>Still-open triage is expanded before display</b>
    /// (<see cref="InvestigationReconciliation.ExpandByElementIdList"/>,
    /// same mechanism <see cref="PileChainBearingConsistencyCommand"/>
    /// uses) - a "wholly-drafted view" rollup issue's <c>ElementId</c> is
    /// the *view's own* id, not any one dimension's, so selecting it
    /// directly and marking it resolved would be a silent no-op (its id
    /// never appears in any rollup's <c>drafted_dimension_ids</c> list -
    /// see <c>InvestigationReconciliation.AllDraftedDimensionsResolved</c>).
    /// A non-rollup triage issue passes through this call unchanged (see
    /// that method's own remarks), so this is safe to apply unconditionally
    /// rather than branching on whether a rollup is actually present.
    /// </remarks>
    private void UpdateDetails()
    {
        var selected = _listView.SelectedItems.Cast<ChecklistRow>().ToList();
        if (selected.Count == 0)
        {
            _detailsListView.ItemsSource = null;
            return;
        }

        if (selected.Count > 1)
        {
            _detailsListView.ItemsSource = null;
            return;
        }

        var entry = CheckingSessionHost.Session?.FindView(selected[0].ViewId);
        if (entry is null)
        {
            _detailsListView.ItemsSource = null;
            return;
        }

        var rows = new List<DetailRow>();
        AppendSection(rows, "Confirmed Problem", entry.LastReconciliation.ConfirmedProblems);
        AppendSection(rows, "Other Finding", entry.OtherInvestigationFindings);
        AppendSection(rows, "Needs Manual Review", entry.LastReconciliation.NeedsManualReview);

        // A partially-investigated rollup stays ONE issue in StillOpenTriage
        // until every one of its drafted_dimension_ids has a verdict (see
        // Reconcile's own remarks) - expanding it always re-lists all of
        // them, including ones a verdict (automated or manual - via
        // OnMarkDetailVerdictClick) has already been recorded for. A real
        // bug found on the Revit machine, 2026-08-31: without this filter,
        // "Mark Selected Issue(s) Resolved" appeared to do nothing at all,
        // since resolving adds no new row anywhere to counter the same
        // stale duplicate still showing here. Excluding anything already in
        // InvestigatedElementIds - whichever source recorded it - is what
        // makes this list actually shrink as verdicts come in, matching
        // Reconcile's own "examined" definition rather than only reflecting
        // the containing rollup's all-or-nothing clearing.
        var stillOpen = InvestigationReconciliation.ExpandByElementIdList(entry.LastReconciliation.StillOpenTriage, "drafted_dimension_ids")
            .Where(i => i.ElementId is not { } id || !entry.InvestigatedElementIds.Contains(id))
            .ToList();
        AppendSection(rows, "Still Open Triage", stillOpen);

        if (entry.ManualResolutionReason is not null)
        {
            rows.Add(new DetailRow
            {
                Section = "Manually Dismissed",
                RuleId = "",
                Severity = "",
                ElementId = null,
                Description = entry.ManualResolutionReason.Length == 0 ? "(no reason given)" : entry.ManualResolutionReason,
            });
        }

        _detailsListView.ItemsSource = rows;
    }

    private static void AppendSection(List<DetailRow> rows, string section, IReadOnlyList<Issue> issues)
    {
        foreach (var issue in issues)
        {
            rows.Add(new DetailRow
            {
                Section = section,
                RuleId = issue.RuleId,
                Severity = issue.Severity,
                ElementId = issue.ElementId,
                Description = issue.Description,
            });
        }
    }

    /// <summary>
    /// A human's own per-dimension verdict, given while manually checking
    /// one of <see cref="_detailsListView"/>'s selected issues against the
    /// drawing - real user feedback, 2026-08-31: there was no way at all to
    /// do this, only a whole-view dismissal
    /// (<see cref="OnMarkSelectedResolvedClick"/>).
    /// </summary>
    /// <remarks>
    /// Reuses <see cref="CheckingSession.RecordInvestigation"/> unchanged -
    /// a person's verdict on one dimension is, functionally, just another
    /// investigation source (see <see cref="InvestigationReconciliation.ManualVerdictRuleId"/>'s
    /// own remarks), so it goes through the exact same accumulate-then-
    /// reconcile path an automated check uses rather than a parallel
    /// mechanism. "Resolved" records the id as investigated with no issue
    /// (clean, same as an automated check finding nothing wrong);
    /// "Confirmed Problem" additionally emits a real Issue so it flows
    /// into <c>ConfirmedProblems</c> and the BCF export - no note is
    /// prompted for beyond the original finding's own description, kept
    /// deliberately fast for going through several dimensions in a row;
    /// a full reason prompt is what <c>ReasonPromptWindow</c> is for.
    /// </remarks>
    private void OnMarkDetailVerdictClick(bool isConfirmedProblem)
    {
        var session = CheckingSessionHost.Session;
        var selectedView = _listView.SelectedItems.Cast<ChecklistRow>().ToList();
        if (session is null || selectedView.Count != 1)
        {
            MessageBox.Show(this, "Select exactly one view above first, then select one or more issues below.",
                "RevitCheck", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectedDetails = _detailsListView.SelectedItems.Cast<DetailRow>().Where(r => r.ElementId is not null).ToList();
        if (selectedDetails.Count == 0)
        {
            MessageBox.Show(this, "Select one or more issues below to record a verdict on.", "RevitCheck",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var viewId = selectedView[0].ViewId;
        var elementIds = selectedDetails.Select(r => r.ElementId!.Value).ToList();
        var verdictIssues = isConfirmedProblem
            ? selectedDetails.Select(r => new Issue
            {
                RuleId = InvestigationReconciliation.ManualVerdictRuleId,
                Category = "geometry",
                Severity = "high",
                ElementId = r.ElementId!.Value,
                ViewId = viewId,
                ViewName = selectedView[0].ViewName,
                SheetNo = selectedView[0].SheetNo,
                Description = $"Manually confirmed as a real problem by a reviewer, checking against the " +
                    $"drawing. Original finding: {r.Description}",
            }).ToList()
            : new List<Issue>();

        session.RecordInvestigation(viewId, elementIds, verdictIssues);

        try
        {
            CheckingSessionHost.Autosave();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Recorded, but the session could not be saved to disk:\n\n{ExceptionMessage.Full(ex)}",
                "RevitCheck", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // Temporary diagnostic, 2026-08-31 - real user report: "Mark
        // Resolved" doesn't work on Still Open Triage entries even after
        // the display-filter fix, and re-reading the code found no further
        // bug. Same discipline as InspectPileSetout/ScheduleDiagnostics
        // elsewhere in this codebase: show the real data rather than guess
        // a third time. Remove once the real cause is found.
        var diagEntry = session.FindView(viewId);
        if (diagEntry is not null)
        {
            var stillOpenDump = string.Join("\n", diagEntry.LastReconciliation.StillOpenTriage.Select(i =>
                $"  - element={i.ElementId?.ToString() ?? "(null)"} rule={i.RuleId} scope={(i.SuggestedFix is { } f && f.TryGetValue("scope", out var s) ? s : "(none)")}"));
            MessageBox.Show(this,
                $"DIAGNOSTIC\nSubmitted id(s): {string.Join(", ", elementIds)}\n" +
                $"InvestigatedElementIds now has {diagEntry.InvestigatedElementIds.Count} entries, contains submitted: " +
                $"{elementIds.All(diagEntry.InvestigatedElementIds.Contains)}\n" +
                $"StillOpenTriage now has {diagEntry.LastReconciliation.StillOpenTriage.Count} raw issue(s):\n{stillOpenDump}",
                "RevitCheck - diagnostic (temporary)", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        Refresh();
    }

    private void OnOpenViewClick(object sender, RoutedEventArgs e)
    {
        var selected = _listView.SelectedItems.Cast<ChecklistRow>().ToList();
        if (selected.Count != 1)
        {
            MessageBox.Show(this, "Select exactly one row to open its view.", "RevitCheck",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var handler = RevitCheckExternalEvents.OpenViewHandler;
        var ev = RevitCheckExternalEvents.OpenView;
        if (handler is null || ev is null)
        {
            return;
        }

        handler.RequestedViewId = selected[0].ViewId;
        ev.Raise();
    }

    private void OnMarkSelectedResolvedClick(object sender, RoutedEventArgs e)
    {
        var session = CheckingSessionHost.Session;
        var selected = _listView.SelectedItems.Cast<ChecklistRow>().ToList();
        if (session is null || selected.Count == 0)
        {
            MessageBox.Show(this, "Select one or more rows to dismiss.", "RevitCheck",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // A row already carrying a confirmed problem stays Flagged
        // regardless of this dismissal (CheckingSession's own precedence
        // rule, ViewChecklistEntry.Status) - told plainly here rather than
        // let the reviewer discover it silently didn't work.
        var alreadyFlagged = selected.Where(r => r.Status == ViewInvestigationStatus.Flagged).ToList();
        if (alreadyFlagged.Count > 0)
        {
            var names = string.Join(", ", alreadyFlagged.Select(r => $"{r.SheetNo}/{r.ViewName}"));
            var proceed = MessageBox.Show(this,
                $"{alreadyFlagged.Count} of the selected view(s) already carry a CONFIRMED problem and will " +
                $"stay flagged regardless of this dismissal - it cannot bury an already-confirmed finding:\n\n{names}\n\n" +
                "Continue dismissing the rest of the selection?",
                "RevitCheck - confirmed problems in selection", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (proceed != MessageBoxResult.Yes)
            {
                return;
            }
        }

        var reasonWindow = new ReasonPromptWindow { Owner = this };
        if (reasonWindow.ShowDialog() != true)
        {
            return;
        }

        session.ResolveManually(selected.Select(r => r.ViewId), reasonWindow.Reason);

        try
        {
            CheckingSessionHost.Autosave();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Dismissed, but the session could not be saved to disk:\n\n{ExceptionMessage.Full(ex)}",
                "RevitCheck", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        Refresh();
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        var handler = RevitCheckExternalEvents.ExportBcfHandler;
        var ev = RevitCheckExternalEvents.ExportBcf;
        if (handler is null || ev is null)
        {
            return;
        }

        ev.Raise();
    }

    private sealed class ChecklistRow
    {
        public long ViewId { get; init; }
        public string SheetNo { get; init; } = "";
        public string ViewName { get; init; } = "";
        public ViewInvestigationStatus Status { get; init; }
        public string StatusText => Status.ToString();
        public int StillOpenCount { get; init; }
        public int ConfirmedCount { get; init; }
        public int ManualReviewCount { get; init; }
    }

    /// <summary>One row in <see cref="_detailsListView"/> - a single, real, individually-actionable finding (never an opaque rollup - see <see cref="UpdateDetails"/>'s own remarks).</summary>
    private sealed class DetailRow
    {
        public string Section { get; init; } = "";
        public string RuleId { get; init; } = "";
        public string Severity { get; init; } = "";
        public long? ElementId { get; init; }
        public string ElementIdText => ElementId?.ToString() ?? "";
        public string Description { get; init; } = "";
    }
}
