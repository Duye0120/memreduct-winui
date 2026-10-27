using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;

namespace MemReduct.Fluent;

public sealed partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        UpdateStatus.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) =>
            UpdateStatus.Visibility = string.IsNullOrWhiteSpace(UpdateStatus.Text) ? Visibility.Collapsed : Visibility.Visible);
    }
}
