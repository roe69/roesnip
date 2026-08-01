using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RoeSnip.App;

// RoeSnip.csproj enables both UseWPF and UseWindowsForms, so several WPF/WinForms type names
// collide by simple name - same disambiguation convention Overlay/ToolbarControl.xaml.cs already
// uses. "Color" alone would otherwise also risk resolving to the RoeSnip.Color namespace (a
// sibling of RoeSnip.App) instead of System.Windows.Media.Color.
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

/// <summary>Small owned modal used in place of <see cref="System.Windows.MessageBox"/> for RoeSnip's
/// own OK / Yes-No prompts (Save/validation failures, the elevated-restart confirm, the Remove
/// confirm). MessageBox is system chrome - it is never reachable from Theme/RoeSnipTheme.xaml
/// regardless of Owner, so it renders a bright system-light dialog bolted onto an otherwise fully
/// dark app (the exact bug this settings-legibility audit exists to remove elsewhere). Mirrors the
/// Avalonia app's own owned-dialog precedent (AppShell/TrayApp.cs's ShowYesNoDialogAsync,
/// AppShell/ShareProviderEditWindow.axaml.cs's ShowYesNoDialogAsync) - WPF's Window.ShowDialog is
/// synchronous, so unlike Avalonia this returns a plain bool, no Task needed. No icon glyph (OK/
/// error/warning/question) is reproduced - same simplification the Avalonia dialogs already made;
/// the message text alone carries the meaning.</summary>
internal sealed class OwnedMessageWindow : Window
{
    private bool _yesClicked;

    private OwnedMessageWindow(Window owner, string title, string message, bool yesNo)
    {
        Owner = owner;
        Title = title;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.Height;
        Width = 420;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0x00));
        Foreground = new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xF0));
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        // Same merge-per-window pattern as SettingsWindow.xaml/ShareProvidersWindow.xaml/
        // ShareProviderEditWindow.xaml (this app has no App.xaml application-scope merge point) -
        // needed so the implicit Button style and the keyed AccentButtonStyle below both resolve.
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Theme/RoeSnipTheme.xaml"),
        });

        DarkTitleBar.Apply(this);

        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20, 18, 20, 4),
        };

        var primaryButton = new Button
        {
            Content = yesNo ? "Yes" : "OK",
            Style = (Style)Resources["AccentButtonStyle"],
            MinWidth = 80,
            IsDefault = true,
            Margin = new Thickness(0),
        };
        primaryButton.Click += (_, _) => { _yesClicked = true; Close(); };

        var buttonsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(20, 16, 20, 18),
        };
        // Neutral action left, primary right - the same order the settings/provider windows use for
        // Cancel/Save, so a prompt raised from one of them doesn't reverse the button order the user
        // is looking at underneath it.
        if (yesNo)
        {
            var noButton = new Button { Content = "No", MinWidth = 80, IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
            noButton.Click += (_, _) => Close();
            buttonsPanel.Children.Add(noButton);
            primaryButton.Margin = new Thickness(0);
        }

        buttonsPanel.Children.Add(primaryButton);

        if (!yesNo)
        {
            primaryButton.IsCancel = true; // Escape also dismisses a single-button OK prompt
        }

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(text, 0);
        Grid.SetRow(buttonsPanel, 1);
        root.Children.Add(text);
        root.Children.Add(buttonsPanel);
        Content = root;
    }

    public static void ShowOk(Window owner, string message, string title = "RoeSnip")
        => new OwnedMessageWindow(owner, title, message, yesNo: false).ShowDialog();

    public static bool ShowYesNo(Window owner, string message, string title = "RoeSnip")
    {
        var window = new OwnedMessageWindow(owner, title, message, yesNo: true);
        window.ShowDialog();
        return window._yesClicked;
    }
}
