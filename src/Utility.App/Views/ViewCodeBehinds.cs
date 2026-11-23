using System.Windows;
using System.Windows.Controls;
using Utility.App.ViewModels;

namespace Utility.App.Views;

public partial class DashboardView : UserControl { public DashboardView() => InitializeComponent(); }
public partial class ProfilesView : UserControl { public ProfilesView() => InitializeComponent(); }
public partial class BatteryView : UserControl { public BatteryView() => InitializeComponent(); }
public partial class SoftwareView : UserControl
{
    public SoftwareView() => InitializeComponent();

    private async void OnAppIconLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CleanupAppRow row })
            await row.LoadIconAsync();
    }

    private async void OnAppIconContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement { IsLoaded: true } && e.NewValue is CleanupAppRow row)
            await row.LoadIconAsync();
    }
}
public partial class FanCurvesView : UserControl { public FanCurvesView() => InitializeComponent(); }
public partial class DriversView : UserControl { public DriversView() => InitializeComponent(); }
