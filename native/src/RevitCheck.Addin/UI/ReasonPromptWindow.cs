using System.Windows;
using System.Windows.Controls;

namespace RevitCheck.Addin.UI;

/// <summary>
/// Small modal prompt for a free-text dismissal reason, shown by
/// <see cref="ChecklistWindow"/>'s "Mark Selected Resolved..." button.
/// Code-behind-only, same reason as <see cref="ChecklistWindow"/> itself
/// (see its remarks) - no separate .xaml file for a window this small.
/// </summary>
/// <remarks>
/// Encouraged, not required - PLANNING.md §16's own design: a reason is
/// "not required, but encouraged, matching this project's own auditability
/// rule". Cancelling this dialog (<see cref="Window.DialogResult"/> stays
/// false/null) must abandon the whole dismissal, not proceed with an empty
/// reason - the caller checks <c>ShowDialog() == true</c> before reading
/// <see cref="Reason"/>.
/// </remarks>
internal sealed class ReasonPromptWindow : Window
{
    private readonly TextBox _textBox;

    /// <summary>Null if left blank - <c>CheckingSession.ResolveManually</c> accepts a null reason, it's just less useful in the audit trail.</summary>
    public string? Reason => string.IsNullOrWhiteSpace(_textBox.Text) ? null : _textBox.Text.Trim();

    public ReasonPromptWindow()
    {
        Title = "RevitCheck - reason for dismissal";
        Width = 420;
        Height = 200;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var label = new TextBlock
        {
            Text = "Why is this out of scope for checking? " +
                   "(e.g. \"Diagrammatic - construction sequence\") - optional but encouraged.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        Grid.SetRow(label, 0);
        root.Children.Add(label);

        _textBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetRow(_textBox, 1);
        root.Children.Add(_textBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var ok = new Button
        {
            // "Dismiss" read as confusing at the real Revit machine,
            // 2026-08-31 - "OK" is the plain, unambiguous confirm action;
            // the dismissal itself already happened by getting to this
            // dialog, this button just confirms/cancels providing a reason
            // for it.
            Content = "OK",
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true,
        };
        ok.Click += (_, _) => { DialogResult = true; Close(); };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(10, 4, 10, 4), IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        Content = root;
    }
}
