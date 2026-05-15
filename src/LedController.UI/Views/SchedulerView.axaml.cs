using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using LedController.UI.ViewModels;

namespace LedController.UI.Views;

public partial class SchedulerView : UserControl
{
    public SchedulerView()
    {
        InitializeComponent();
    }

    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null || DataContext is not SchedulerViewModel viewModel)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "\u00dctemez\u00e9s import\u00e1l\u00e1sa",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("JSON")
                {
                    Patterns = new[] { "*.json" }
                }
            }
        });

        var file = files?.FirstOrDefault();
        var path = file?.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await viewModel.ImportLegacyScheduleAsync(path);
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null || DataContext is not SchedulerViewModel viewModel)
        {
            return;
        }

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "\u00dctemez\u00e9s export\u00e1l\u00e1sa",
            SuggestedFileName = "led_schedule_profiles.json",
            DefaultExtension = "json",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("JSON")
                {
                    Patterns = new[] { "*.json" }
                }
            }
        });

        var path = file?.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await viewModel.ExportLegacyScheduleAsync(path);
    }
}
