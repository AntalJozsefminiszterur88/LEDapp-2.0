using System;
using Avalonia.Controls;
using Avalonia.Input;
using LedController.Core.Models;
using LedController.UI.ViewModels;

namespace LedController.UI.Views;

public partial class DeviceControlView : UserControl
{
    public DeviceControlView()
    {
        InitializeComponent();
    }

    private void OnColorContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control control)
        {
            return;
        }

        if (DataContext is not DeviceControlViewModel viewModel)
        {
            return;
        }

        if (control.DataContext is not LedColor color)
        {
            return;
        }

        viewModel.BeginEditColor(color);
    }

    private void OnColorContextClosed(object? sender, EventArgs e)
    {
        if (DataContext is not DeviceControlViewModel viewModel)
        {
            return;
        }

        viewModel.CancelEditColor();
    }
}
