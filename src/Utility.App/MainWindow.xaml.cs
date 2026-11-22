using System.Windows;
using Utility.App.ViewModels;

namespace Utility.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        Loaded += async (_, _) => await viewModel.InitializeAsync();
        StateChanged += (_, _) => viewModel.SetWindowVisibility(WindowState != WindowState.Minimized && IsVisible);
        IsVisibleChanged += (_, _) => viewModel.SetWindowVisibility(WindowState != WindowState.Minimized && IsVisible);
        Closed += (_, _) => viewModel.Dispose();
    }
}
