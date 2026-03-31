using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using NWSHelper.Gui.ViewModels;

namespace NWSHelper.Gui.Views.Stages;

public partial class SettingsStageView : UserControl
{
    private MainWindowViewModel? observedViewModel;
    private int lastHandledStoreContinuityAttentionRequestId;

    public SettingsStageView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (observedViewModel is not null)
        {
            observedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        observedViewModel = DataContext as MainWindowViewModel;

        if (observedViewModel is not null)
        {
            observedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        _ = ProcessStoreContinuityAttentionAsync();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.StoreContinuityAttentionRequestId) ||
            e.PropertyName == nameof(MainWindowViewModel.CurrentStage))
        {
            _ = ProcessStoreContinuityAttentionAsync();
        }
    }

    private async Task ProcessStoreContinuityAttentionAsync()
    {
        if (observedViewModel is null || observedViewModel.CurrentStage != WorkflowStage.Settings)
        {
            return;
        }

        var requestId = observedViewModel.StoreContinuityAttentionRequestId;
        if (requestId <= 0 || requestId == lastHandledStoreContinuityAttentionRequestId)
        {
            return;
        }

        lastHandledStoreContinuityAttentionRequestId = requestId;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                AccountLinkSection.BringIntoView();
                AccountLinkEmailTextBox.Focus();
            }, DispatcherPriority.Background);

            if (AccountLinkEmailTextBox.IsFocused)
            {
                break;
            }

            await Task.Delay(40);
        }

        await PulseStoreContinuityPromptAsync();
    }

    private async Task PulseStoreContinuityPromptAsync()
    {
        if (!StoreContinuityPromptBanner.IsVisible)
        {
            return;
        }

        StoreContinuityPromptBanner.Opacity = 0.4;
        await Task.Delay(250);
        StoreContinuityPromptBanner.Opacity = 0.75;
        await Task.Delay(250);
        StoreContinuityPromptBanner.Opacity = 0.5;
        await Task.Delay(250);
        StoreContinuityPromptBanner.Opacity = 1.0;
    }

    private async void OnExportMigrationBackupClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var path = await PickSaveFileAsync("Export Migration Backup", "NWSHelper-migration-backup.json");
        if (!string.IsNullOrWhiteSpace(path))
        {
            await viewModel.ExportSettingsMigrationAsync(path);
        }
    }

    private async void OnImportMigrationBackupClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var path = await PickOpenFileAsync("Import Migration Backup");
        if (!string.IsNullOrWhiteSpace(path))
        {
            await viewModel.ImportSettingsMigrationAsync(path);
        }
    }

    private async Task<string?> PickSaveFileAsync(string title, string suggestedFileName)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            return null;
        }

        var suggestedStartLocation = await GetSuggestedStartFolderAsync(topLevel.StorageProvider);
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "json",
            ShowOverwritePrompt = true,
            SuggestedStartLocation = suggestedStartLocation,
            FileTypeChoices =
            [
                new FilePickerFileType("JSON")
                {
                    Patterns = ["*.json"]
                },
                FilePickerFileTypes.All
            ]
        });

        return file?.TryGetLocalPath();
    }

    private async Task<string?> PickOpenFileAsync(string title)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            return null;
        }

        var suggestedStartLocation = await GetSuggestedStartFolderAsync(topLevel.StorageProvider);
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = suggestedStartLocation,
            FileTypeFilter =
            [
                new FilePickerFileType("JSON")
                {
                    Patterns = ["*.json"]
                },
                FilePickerFileTypes.All
            ]
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private static async Task<IStorageFolder?> GetSuggestedStartFolderAsync(IStorageProvider storageProvider)
    {
        var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documentsPath) || !Directory.Exists(documentsPath))
        {
            return null;
        }

        return await storageProvider.TryGetFolderFromPathAsync(documentsPath);
    }
}
