using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Utility.App.Localization;
using Utility.App.Views;
using Utility.App.ViewModels;

namespace Utility.App;

public partial class MainWindow : Window
{
    private static readonly IEasingFunction PageEase = new CubicEase { EasingMode = EasingMode.EaseOut };

    private readonly UserControl?[] _pages = new UserControl[MainViewModel.NavLast + 1];

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        ShowPage(viewModel);

        Loaded += async (_, _) => await viewModel.RunSafelyAsync(viewModel.InitializeAsync);
        StateChanged += (_, _) =>
        {
            viewModel.SetWindowVisibility(WindowState != WindowState.Minimized && IsVisible);
            UpdateMaximizedLayout();
        };
        IsVisibleChanged += (_, _) => viewModel.SetWindowVisibility(WindowState != WindowState.Minimized && IsVisible);
        Closed += (_, _) => viewModel.Dispose();
        SourceInitialized += (_, _) => ApplyDwmAttributes();

        _toastTimer.Tick += OnToastTimerTick;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedNavIndex))
            {
                ShowPage(viewModel);
                AnimatePageIn();
                return;
            }
            if (e.PropertyName != nameof(MainViewModel.StatusMessage))
                return;
            if (Dispatcher.CheckAccess())
                ShowToast(viewModel.StatusMessage);
            else
                Dispatcher.BeginInvoke(() => ShowToast(viewModel.StatusMessage));
        };
    }

    private void ShowPage(MainViewModel model)
    {
        var index = model.SelectedNavIndex;
        CurrentPage.Content = _pages[index] ??= index switch
        {
            MainViewModel.NavDashboard => new DashboardView(),
            MainViewModel.NavProfiles => new ProfilesView(),
            MainViewModel.NavFanCurves => new FanCurvesView(),
            // Battery page also hosts the graphics mode card (NavGraphics is redirected to it).
            MainViewModel.NavBattery => new BatteryView(),
            MainViewModel.NavCleanup => new CleanupView { DataContext = model.Cleanup },
            MainViewModel.NavSoftware => new SoftwareView(),
            MainViewModel.NavDrivers => new DriversView(),
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }

    // ---------------- Action toast ----------------
    // Bottom-left feedback after an operation. Stays long enough to read (scaled by text length),
    // pauses while hovered, and fades out on its own. Animations start from the current values,
    // so a new message arriving mid-fade simply glides back in.

    private static readonly IEasingFunction ToastEaseIn = new CubicEase { EasingMode = EasingMode.EaseIn };
    private readonly DispatcherTimer _toastTimer = new();
    private int _toastGeneration;

    private static TimeSpan ToastDuration(string text) =>
        TimeSpan.FromMilliseconds(Math.Clamp(2600 + text.Length * 60, 3000, 8000));

    private void ShowToast(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            HideToast();
            return;
        }

        _toastGeneration++;
        ToastText.Text = text.Trim();
        if (Toast.Visibility != Visibility.Visible || Toast.Opacity < 0.01)
        {
            Toast.BeginAnimation(OpacityProperty, null);
            ToastOffset.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
            Toast.Opacity = 0;
            ToastOffset.Y = 10;
            Toast.Visibility = Visibility.Visible;
        }

        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)) { EasingFunction = PageEase });
        ToastOffset.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(240)) { EasingFunction = PageEase });

        _toastTimer.Stop();
        _toastTimer.Interval = ToastDuration(ToastText.Text);
        _toastTimer.Start();
    }

    private void OnToastTimerTick(object? sender, EventArgs e)
    {
        _toastTimer.Stop();
        if (Toast.IsMouseOver)
        {
            // Reading it: check again shortly instead of pulling it away.
            _toastTimer.Interval = TimeSpan.FromMilliseconds(1200);
            _toastTimer.Start();
            return;
        }

        HideToast();
    }

    private void OnToastClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => HideToast();

    private System.Windows.Threading.DispatcherTimer? _copiedTimer;

    /// <summary>Copies the device name; the icon shows a check for a moment as confirmation.</summary>
    private void OnCopyDeviceClick(object sender, RoutedEventArgs e)
    {
        var text = (DataContext as MainViewModel)?.MachineBannerText;
        if (string.IsNullOrWhiteSpace(text))
            return;
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return; // Clipboard held by another app: nothing was copied, so no confirmation.
        }

        CopyDeviceButton.Content = "\uE73E";
        CopyDeviceButton.Tag = "copied";
        if (_copiedTimer is null)
        {
            _copiedTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
            _copiedTimer.Tick += (_, _) =>
            {
                _copiedTimer.Stop();
                CopyDeviceButton.Content = "\uE8C8";
                CopyDeviceButton.Tag = null;
            };
        }
        _copiedTimer.Stop();
        _copiedTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        if (Toast.Visibility != Visibility.Visible)
            return;

        var generation = ++_toastGeneration;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(170)) { EasingFunction = ToastEaseIn };
        fade.Completed += (_, _) =>
        {
            if (generation == _toastGeneration)
                Toast.Visibility = Visibility.Collapsed;
        };
        Toast.BeginAnimation(OpacityProperty, fade);
        ToastOffset.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(6, TimeSpan.FromMilliseconds(170)) { EasingFunction = ToastEaseIn });
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void UpdateMaximizedLayout()
    {
        // A maximized WindowChrome window extends past the monitor by the resize frame; pad it back in.
        var maximized = WindowState == WindowState.Maximized;
        var frame = SystemParameters.WindowResizeBorderThickness;
        RootGrid.Margin = maximized
            ? new Thickness(frame.Left + 1, frame.Top + 1, frame.Right + 1, frame.Bottom + 1)
            : new Thickness(0);
        MaxRestoreButton.Content = maximized ? "" : "";
        MaxRestoreButton.ToolTip = Loc.Get(maximized ? "TipRestore" : "TipMaximize");
    }

    /// <summary>
    /// Page entrance: new page appears transparent and 10px low, then after one frame
    /// fades and rises in. Animations start from the current values, so fast clicking never jumps.
    /// </summary>
    private void AnimatePageIn()
    {
        if (!IsLoaded)
            return;

        PageHost.BeginAnimation(OpacityProperty, null);
        PageOffset.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        PageHost.Opacity = 0;
        PageOffset.Y = 10;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(170)) { EasingFunction = PageEase });
            PageOffset.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)) { EasingFunction = PageEase });
        });
    }

    private void ApplyDwmAttributes()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int dark = 0;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
            int round = DwmwcpRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
            int border = 0x00E4DAD4; // COLORREF (BGR) for #D4DAE4
            DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));
        }
        catch (Exception)
        {
            // Older Windows builds ignore or reject these attributes; the window still works.
        }
    }

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
