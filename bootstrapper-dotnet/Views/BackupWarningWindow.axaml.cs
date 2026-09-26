using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace M2M.XmlEditor.Bootstrapper.Views;

public sealed partial class BackupWarningWindow : Window
{
    public event EventHandler? Accepted;
    public event EventHandler? Rejected;

    public BackupWarningWindow()
    {
        InitializeComponent();
        AcknowledgementCheckBox.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.Property == ToggleButton.IsCheckedProperty)
            {
                ContinueButton.IsEnabled = AcknowledgementCheckBox.IsChecked == true;
            }
        };
    }

    private void ContinueButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (AcknowledgementCheckBox.IsChecked != true)
        {
            return;
        }

        Accepted?.Invoke(this, EventArgs.Empty);
    }

    private void ExitButton_OnClick(object? sender, RoutedEventArgs e)
    {
        Rejected?.Invoke(this, EventArgs.Empty);
    }
}
