using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using RevitCheck.Addin.Commands;
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

    public ChecklistWindow()
    {
        Title = "RevitCheck - Dimension Triage Checklist";
        Width = 820;
        Height = 540;

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
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _summary = new TextBlock { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(_summary, 0);
        root.Children.Add(_summary);

        _listView = BuildListView();
        Grid.SetRow(_listView, 1);
        root.Children.Add(_listView);

        var toolbar = BuildToolbar();
        Grid.SetRow(toolbar, 2);
        root.Children.Add(toolbar);

        var exportBar = BuildExportBar();
        Grid.SetRow(exportBar, 3);
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
        gridView.Columns.Add(Column("Triage", nameof(ChecklistRow.TriageCount), 60));
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
            return;
        }

        var rows = session.Views
            .OrderBy(v => v.SheetNo ?? "", StringComparer.Ordinal)
            .ThenBy(v => v.ViewName ?? "", StringComparer.Ordinal)
            .Select(v => new ChecklistRow
            {
                ViewId = v.ViewId,
                SheetNo = v.SheetNo ?? "(no sheet)",
                ViewName = v.ViewName ?? "",
                Status = v.Status,
                TriageCount = v.TriageIssues.Count,
                ConfirmedCount = v.LastReconciliation.ConfirmedProblems.Count + v.OtherInvestigationFindings.Count,
                ManualReviewCount = v.LastReconciliation.NeedsManualReview.Count,
            })
            .ToList();

        _listView.ItemsSource = rows;

        var pending = rows.Count(r => r.Status == ViewInvestigationStatus.Pending);
        var flagged = rows.Count(r => r.Status == ViewInvestigationStatus.Flagged);
        var manualReview = rows.Count(r => r.Status == ViewInvestigationStatus.NeedsManualReview);
        var resolvedManually = rows.Count(r => r.Status == ViewInvestigationStatus.ResolvedManually);
        var resolved = rows.Count(r => r.Status == ViewInvestigationStatus.Resolved);

        _summary.Text =
            $"{rows.Count} view(s): {pending} pending, {flagged} flagged, {manualReview} need manual review, " +
            $"{resolvedManually} manually dismissed, {resolved} resolved." +
            (session.ModelWideNotes.Count > 0
                ? $" ({session.ModelWideNotes.Count} model-wide note(s) not shown here - see the export.)"
                : "");
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
        public int TriageCount { get; init; }
        public int ConfirmedCount { get; init; }
        public int ManualReviewCount { get; init; }
    }
}
