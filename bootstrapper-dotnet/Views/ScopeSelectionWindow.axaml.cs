using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using M2M.XmlEditor.Bootstrapper.Models;

namespace M2M.XmlEditor.Bootstrapper.Views;

public sealed partial class ScopeSelectionWindow : Window
{
    public event Action<InstallationScope>? Accepted;
    public event EventHandler? Rejected;

    public ScopeSelectionWindow()
    {
        InitializeComponent();
        CurrentUserRadio.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.Property == ToggleButton.IsCheckedProperty) UpdateState();
        };
        AllUsersRadio.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.Property == ToggleButton.IsCheckedProperty) UpdateState();
        };
    }

    private void UpdateState()
    {
        ContinueButton.IsEnabled = CurrentUserRadio.IsChecked == true || AllUsersRadio.IsChecked == true;
    }

    private void ContinueButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (CurrentUserRadio.IsChecked == true)
            Accepted?.Invoke(InstallationScope.CurrentUser);
        else if (AllUsersRadio.IsChecked == true)
            Accepted?.Invoke(InstallationScope.AllUsers);
    }

    private void CancelButton_OnClick(object? sender, RoutedEventArgs e) => Rejected?.Invoke(this, EventArgs.Empty);
}
