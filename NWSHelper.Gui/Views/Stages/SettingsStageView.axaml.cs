using Avalonia.Controls;
using Avalonia.Input;
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
    private const int DeveloperSettingsChordPressesRequired = 7;
    private static readonly TimeSpan DeveloperSettingsChordResetWindow = TimeSpan.FromSeconds(2.5);

    private MainWindowViewModel? observedViewModel;
    private TopLevel? subscribedTopLevel;
    private int lastHandledStoreContinuityAttentionRequestId;
    private int developerSettingsChordCount;
    private DateTimeOffset? lastDeveloperSettingsChordUtc;

    public SettingsStageView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        SubscribeToTopLevelKeyDown();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        UnsubscribeFromTopLevelKeyDown();
        ResetDeveloperSettingsChord();
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

        ResetDeveloperSettingsChord();

        _ = ProcessStoreContinuityAttentionAsync();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.StoreContinuityAttentionRequestId) ||
            e.PropertyName == nameof(MainWindowViewModel.CurrentStage))
        {
            if (observedViewModel?.CurrentStage != WorkflowStage.Settings)
            {
                ResetDeveloperSettingsChord();
            }

            _ = ProcessStoreContinuityAttentionAsync();
        }
    }

    private void OnSettingsStageKeyDown(object? sender, KeyEventArgs e)
    {
        if (observedViewModel?.CurrentStage != WorkflowStage.Settings)
        {
            ResetDeveloperSettingsChord();
            return;
        }

        if (e.Key == Key.G && e.KeyModifiers == KeyModifiers.Control)
        {
            RegisterDeveloperSettingsChord();
            e.Handled = true;
            return;
        }

        if (e.Key is not Key.LeftCtrl and not Key.RightCtrl)
        {
            ResetDeveloperSettingsChord();
        }
    }

    private void SubscribeToTopLevelKeyDown()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null || ReferenceEquals(topLevel, subscribedTopLevel))
        {
            return;
        }

        UnsubscribeFromTopLevelKeyDown();
        topLevel.AddHandler(KeyDownEvent, OnSettingsStageKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        subscribedTopLevel = topLevel;
    }

    private void UnsubscribeFromTopLevelKeyDown()
    {
        if (subscribedTopLevel is null)
        {
            return;
        }

        subscribedTopLevel.RemoveHandler(KeyDownEvent, OnSettingsStageKeyDown);
        subscribedTopLevel = null;
    }

    private void RegisterDeveloperSettingsChord()
    {
        var now = DateTimeOffset.UtcNow;
        if (!lastDeveloperSettingsChordUtc.HasValue || now - lastDeveloperSettingsChordUtc.Value > DeveloperSettingsChordResetWindow)
        {
            developerSettingsChordCount = 0;
        }

        lastDeveloperSettingsChordUtc = now;
        developerSettingsChordCount++;

        if (developerSettingsChordCount < DeveloperSettingsChordPressesRequired)
        {
            return;
        }

        ResetDeveloperSettingsChord();
        _ = RevealDeveloperSettingsAsync();
    }

    private void ResetDeveloperSettingsChord()
    {
        developerSettingsChordCount = 0;
        lastDeveloperSettingsChordUtc = null;
    }

    private async Task RevealDeveloperSettingsAsync()
    {
        DeveloperSettingsSection.IsVisible = true;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                DeveloperSettingsSection.BringIntoView();
                UpdateVersionOverrideForTestingTextBox.Focus();
            }, DispatcherPriority.Background);

            if (UpdateVersionOverrideForTestingTextBox.IsFocused)
            {
                break;
            }

            await Task.Delay(40);
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

    private async void OnExportSupportDiagnosticsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var suggestedFileName = $"NWSHelper-support-diagnostics-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json";
        var path = await PickSaveFileAsync("Export Support Diagnostics", suggestedFileName);
        if (!string.IsNullOrWhiteSpace(path))
        {
            await viewModel.ExportSupportDiagnosticsAsync(path);
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
