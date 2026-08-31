using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using RevitCheck.Addin.Commands;
using RevitCheck.Core.Issues;
using RevitCheck.Core.Reporting;

namespace RevitCheck.Addin.UI;

/// <summary>
/// The interactive checking workflow's checklist window (PLANNING.md §16
/// Stage 3). Shows <see cref="CheckingSessionHost.Session"/>'s rows,
/// grouped by sheet; lets a reviewer open a flagged view, bulk-dismiss a
/// whole sheet by human judgement, and export the reconciled BCF once done
/// cycling through every view.
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
    private readonly TextBox _details;

    public ChecklistWindow()
    {
        Title = "RevitCheck - Dimension Triage Checklist";
        Width = 820;
        Height = 700;

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
        // check, not just how many.
        var detailsPanel = new DockPanel { Margin = new Thickness(8, 4, 8, 8) };
        var detailsLabel = new TextBlock
        {
            Text = "Details for the selected view:",
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 4),
        };
        DockPanel.SetDock(detailsLabel, Dock.Top);
        detailsPanel.Children.Add(detailsLabel);
        _details = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FontFamily = new FontFamily("Consolas"),
            Background = Brushes.WhiteSmoke,
        };
        detailsPanel.Children.Add(_details);
        Grid.SetRow(detailsPanel, 3);
        root.Children.Add(detailsPanel);

        var toolbar = BuildToolbar();
        Grid.SetRow(toolbar, 4);
        root.Children.Add(toolbar);

        var exportBar = BuildExportBar();
        Grid.SetRow(exportBar, 5);
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
    private void UpdateDetails()
    {
        var selected = _listView.SelectedItems.Cast<ChecklistRow>().ToList();
        if (selected.Count == 0)
        {
            _details.Text = "Select a row above to see its issues.";
            return;
        }

        if (selected.Count > 1)
        {
            _details.Text = $"{selected.Count} rows selected - select just one to see its issues.";
            return;
        }

        var entry = CheckingSessionHost.Session?.FindView(selected[0].ViewId);
        if (entry is null)
        {
            _details.Text = "(no session data for this row)";
            return;
        }

        var sb = new StringBuilder();
        AppendSection(sb, "CONFIRMED PROBLEMS - exports to BCF", entry.LastReconciliation.ConfirmedProblems);
        AppendSection(sb, "OTHER INVESTIGATION FINDINGS (e.g. pile schedule) - exports to BCF", entry.OtherInvestigationFindings);
        AppendSection(sb, "NEEDS MANUAL REVIEW - check the drawing (this view), then leave for export or dismiss", entry.LastReconciliation.NeedsManualReview);
        AppendSection(sb, "STILL OPEN TRIAGE - not yet investigated by any check", entry.LastReconciliation.StillOpenTriage);

        if (entry.ManualResolutionReason is not null)
        {
            sb.AppendLine($"MANUALLY DISMISSED - reason: {(entry.ManualResolutionReason.Length == 0 ? "(none given)" : entry.ManualResolutionReason)}");
        }

        _details.Text = sb.Length == 0 ? "(nothing outstanding for this row)" : sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, string heading, IReadOnlyList<Issue> issues)
    {
        if (issues.Count == 0)
        {
            return;
        }

        sb.AppendLine($"-- {heading} ({issues.Count}) --");
        foreach (var issue in issues)
        {
            sb.AppendLine($"[{issue.RuleId}] {issue.Description}" + (issue.ElementId is { } id ? $" (element {id})" : ""));
        }

        sb.AppendLine();
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
}
