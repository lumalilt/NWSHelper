using Avalonia.Controls;
using System;
using NWSHelper.Gui.ViewModels;

namespace NWSHelper.Gui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;

        if (DataContext is MainWindowViewModel viewModel)
        {
            _ = viewModel.RunStartupUpdatePolicyAsync();
        }
    }
}
