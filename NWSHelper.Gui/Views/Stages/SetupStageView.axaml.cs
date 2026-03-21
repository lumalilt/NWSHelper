using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NWSHelper.Gui.Services;
using NWSHelper.Gui.ViewModels;
using NWSHelper.Gui.Views;

namespace NWSHelper.Gui.Views.Stages;

public partial class SetupStageView : UserControl
{
    private bool isSynchronizingStateFilterSelection;
    private bool isSynchronizingDatasetSelection;
    private bool hasPendingDatasetSelectionChanges;
    private bool isApplyingDatasetSelection;
    private bool suppressDatasetFlyoutClosedApply;
    private bool suppressStatesFlyoutClosedApply;
    private string lastAppliedDatasetSelectionFingerprint = string.Empty;
    private readonly OpenAddressesOnboardingCoordinator onboardingCoordinator;

    public SetupStageView()
        : this(new OpenAddressesOnboardingCoordinator())
    {
    }

    internal SetupStageView(OpenAddressesOnboardingCoordinator onboardingCoordinator)
    {
        this.onboardingCoordinator = onboardingCoordinator ?? throw new ArgumentNullException(nameof(onboardingCoordinator));
        InitializeComponent();
        StateFilterNodes = [];
        StateFilterTree.ItemsSource = StateFilterNodes;
        StateFilterTree.SelectionChanged += OnTreeSelectionChanged;
        DatasetSelectionNodes = [];
        DatasetSelectionTree.ItemsSource = DatasetSelectionNodes;
        DatasetSelectionTree.SelectionChanged += OnTreeSelectionChanged;
    }

    public ObservableCollection<StateFilterNode> StateFilterNodes { get; }

    public ObservableCollection<DatasetSelectionNode> DatasetSelectionNodes { get; }

    private async void OnBrowseBoundaryCsvClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = GetViewModel();
        if (viewModel is null)
        {
            return;
        }

        var path = await PickOpenFileAsync("Select Boundary CSV", viewModel.BoundaryCsvPath);
        if (!string.IsNullOrWhiteSpace(path))
        {
            viewModel.BoundaryCsvPath = path;
        }
    }

    private async void OnBrowseExistingCsvClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = GetViewModel();
        if (viewModel is null)
        {
            return;
        }

        var path = await PickOpenFileAsync("Select Existing Addresses CSV", viewModel.ExistingAddressesCsvPath);
        if (!string.IsNullOrWhiteSpace(path))
        {
            viewModel.ExistingAddressesCsvPath = path;
        }
    }

    private async void OnBrowseDatasetRootClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = GetViewModel();
        if (viewModel is null)
        {
            return;
        }

        var path = await PickFolderAsync("Select Dataset Root", viewModel.DatasetRootPath);
        if (!string.IsNullOrWhiteSpace(path))
        {
            viewModel.DatasetRootPath = path;
        }
    }

    private async void OnBrowseConsolidatedOutputClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = GetViewModel();
        if (viewModel is null)
        {
            return;
        }

        var path = await PickFolderAsync("Select Alternative Output Folder", viewModel.ConsolidatedOutputPath);
        if (!string.IsNullOrWhiteSpace(path))
        {
            viewModel.ConsolidatedOutputPath = path;
        }
    }

    private async void OnOpenDatasetSelectorClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = GetViewModel();
        if (viewModel is null)
        {
            return;
        }

        await ReloadDatasetSelectionTreeAsync(viewModel);
    }

    private async Task<bool> EnsureOpenAddressesApiTokenAsync(MainWindowViewModel viewModel)
    {
        if (!string.Equals(viewModel.SelectedDatasetProviderId, "openaddresses", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(viewModel.OpenAddressesApiToken))
        {
            return true;
        }

        if (TopLevel.GetTopLevel(this) is not Window ownerWindow)
        {
            viewModel.DatasetDownloadMessage = "Download failed - no API key was saved.";
            viewModel.StatusMessage = viewModel.DatasetDownloadMessage;
            return false;
        }

        var onboardingType = ApiOnboardingTypeLabels.ParseOrDefault(viewModel.OpenAddressesApiOnboardingType, ApiOnboardingType.Automated);

        var onboardingResult = await onboardingCoordinator.LaunchAsync(
            new OpenAddressesOnboardingWindowDialogSessionFactory(ownerWindow),
            viewModel.UseEmbeddedOpenAddressesOnboardingWebView,
            onboardingType);

        if (onboardingResult.PersistExternalFallbackPreference && viewModel.UseEmbeddedOpenAddressesOnboardingWebView)
        {
            // Persist first-run fallback when embedded onboarding dependency checks fail.
            viewModel.UseEmbeddedOpenAddressesOnboardingWebView = false;
        }

        if (string.IsNullOrWhiteSpace(onboardingResult.ApiToken))
        {
            var failureMessage = "Download failed - no API key was saved.";
            if (!string.IsNullOrWhiteSpace(onboardingResult.StatusMessage))
            {
                failureMessage = $"{onboardingResult.StatusMessage} {failureMessage}";
            }

            viewModel.DatasetDownloadMessage = failureMessage;
            viewModel.StatusMessage = failureMessage;
            return false;
        }

        viewModel.OpenAddressesApiToken = onboardingResult.ApiToken.Trim();
        viewModel.LastOpenAddressesOnboardingSuccessSummary = string.IsNullOrWhiteSpace(onboardingResult.LastSuccessfulApiTokenCaptureSummary)
            ? $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [Unknown] OpenAddresses API key saved."
            : onboardingResult.LastSuccessfulApiTokenCaptureSummary;
        viewModel.LastError = null;
        viewModel.DatasetDownloadMessage = string.Empty;
        viewModel.StatusMessage = onboardingResult.UsedExternalFallback
            ? "OpenAddresses API key saved using external browser onboarding fallback."
            : "OpenAddresses API key saved.";
        return true;
    }

    private void OnSelectAllDatasetsClick(object? sender, RoutedEventArgs e)
    {
        isSynchronizingDatasetSelection = true;
        foreach (var root in DatasetSelectionNodes)
        {
            root.IsSelected = true;
            SetDatasetChildrenSelection(root, isSelected: true);
        }

        isSynchronizingDatasetSelection = false;
        hasPendingDatasetSelectionChanges = true;
    }

    private void OnClearDatasetsSelectionClick(object? sender, RoutedEventArgs e)
    {
        isSynchronizingDatasetSelection = true;
        foreach (var root in DatasetSelectionNodes)
        {
            root.IsSelected = false;
            SetDatasetChildrenSelection(root, isSelected: false);
        }

        isSynchronizingDatasetSelection = false;
        hasPendingDatasetSelectionChanges = true;
    }

    private async void OnApplyDatasetSelectionClick(object? sender, RoutedEventArgs e)
    {
        suppressDatasetFlyoutClosedApply = true;
        try
        {
            (DatasetSelectorButton.Flyout as Flyout)?.Hide();
            await ApplyDatasetSelectionToViewModelAsync(forceDownload: true);
        }
        finally
        {
            suppressDatasetFlyoutClosedApply = false;
        }
    }

    private async void OnDatasetSelectionFlyoutClosed(object? sender, EventArgs e)
    {
        if (suppressDatasetFlyoutClosedApply)
        {
            return;
        }

        await ApplyDatasetSelectionToViewModelAsync(forceDownload: false);
    }

    private void OnOpenStatesFilterSelectorClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = GetViewModel();
        if (viewModel is null)
        {
            return;
        }

        ReloadStateFilterTree(viewModel);
    }

    private void OnSelectAllStatesFilterSelectionClick(object? sender, RoutedEventArgs e)
    {
        isSynchronizingStateFilterSelection = true;
        foreach (var state in StateFilterNodes)
        {
            state.IsSelected = true;
            foreach (var county in state.Children)
            {
                county.IsSelected = true;
            }
        }

        isSynchronizingStateFilterSelection = false;
    }

    private void OnClearStatesFilterSelectionClick(object? sender, RoutedEventArgs e)
    {
        isSynchronizingStateFilterSelection = true;
        foreach (var state in StateFilterNodes)
        {
            state.IsSelected = false;
            foreach (var county in state.Children)
            {
                county.IsSelected = false;
            }
        }

        isSynchronizingStateFilterSelection = false;
    }

    private void OnApplyStatesFilterSelectionClick(object? sender, RoutedEventArgs e)
    {
        suppressStatesFlyoutClosedApply = true;
        ApplyStateFilterSelectionToViewModel();
        (StatesFilterSelectorButton.Flyout as Flyout)?.Hide();
    }

    private void OnStatesFilterFlyoutClosed(object? sender, EventArgs e)
    {
        if (suppressStatesFlyoutClosedApply)
        {
            suppressStatesFlyoutClosedApply = false;
            return;
        }

        ApplyStateFilterSelectionToViewModel();
    }

    private static void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is TreeView treeView && treeView.SelectedItem is not null)
        {
            treeView.SelectedItem = null;
        }
    }

    private MainWindowViewModel? GetViewModel() => DataContext as MainWindowViewModel;

    private async Task ReloadDatasetSelectionTreeAsync(MainWindowViewModel viewModel)
    {
        foreach (var rootNode in DatasetSelectionNodes)
        {
            UnsubscribeDatasetNode(rootNode);
        }

        DatasetSelectionNodes.Clear();

        var availableDatasets = await viewModel.LoadAvailableDatasetsForSelectionAsync(CancellationToken.None);
        var selectedKeys = new HashSet<string>(viewModel.GetSelectedDatasetSourceKeys(), StringComparer.OrdinalIgnoreCase);
        var nodeLookup = new Dictionary<string, DatasetSelectionNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var dataset in availableDatasets.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var segments = dataset.Key
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => value.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();

            if (segments.Length == 0)
            {
                continue;
            }

            DatasetSelectionNode? parent = null;
            string? currentPath = null;

            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                currentPath = currentPath is null ? segment : $"{currentPath}/{segment}";
                var isLeaf = i == segments.Length - 1;

                if (!nodeLookup.TryGetValue(currentPath, out var node))
                {
                    var label = FormatDatasetSegmentLabel(segment, i);
                    node = new DatasetSelectionNode(
                        currentPath,
                        label,
                        parent,
                        isSelected: false,
                        datasetKey: isLeaf ? dataset.Key : null);

                    nodeLookup[currentPath] = node;

                    if (parent is null)
                    {
                        DatasetSelectionNodes.Add(node);
                    }
                    else
                    {
                        parent.Children.Add(node);
                    }
                }

                parent = node;
            }

            if (parent is not null && selectedKeys.Contains(dataset.Key))
            {
                parent.IsSelected = true;
            }
        }

        foreach (var rootNode in DatasetSelectionNodes)
        {
            RecalculateDatasetNodeSelectionFromChildren(rootNode);
            SubscribeDatasetNode(rootNode);
        }

        hasPendingDatasetSelectionChanges = false;
        lastAppliedDatasetSelectionFingerprint = BuildDatasetSelectionFingerprint();
    }

    private static string FormatDatasetSegmentLabel(string segment, int depth)
    {
        if (depth <= 1)
        {
            return segment.ToUpperInvariant();
        }

        return segment;
    }

    private void SubscribeDatasetNode(DatasetSelectionNode node)
    {
        node.PropertyChanged += OnDatasetSelectionNodePropertyChanged;
        foreach (var child in node.Children)
        {
            SubscribeDatasetNode(child);
        }
    }

    private void UnsubscribeDatasetNode(DatasetSelectionNode node)
    {
        node.PropertyChanged -= OnDatasetSelectionNodePropertyChanged;
        foreach (var child in node.Children)
        {
            UnsubscribeDatasetNode(child);
        }
    }

    private void OnDatasetSelectionNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (isSynchronizingDatasetSelection ||
            e.PropertyName != nameof(DatasetSelectionNode.IsSelected) ||
            sender is not DatasetSelectionNode node)
        {
            return;
        }

        isSynchronizingDatasetSelection = true;

        SetDatasetChildrenSelection(node, node.IsSelected);
        UpdateDatasetParentSelection(node.Parent);

        isSynchronizingDatasetSelection = false;
        hasPendingDatasetSelectionChanges = true;
    }

    private static void SetDatasetChildrenSelection(DatasetSelectionNode node, bool isSelected)
    {
        foreach (var child in node.Children)
        {
            child.IsSelected = isSelected;
            SetDatasetChildrenSelection(child, isSelected);
        }
    }

    private static void UpdateDatasetParentSelection(DatasetSelectionNode? node)
    {
        if (node is null)
        {
            return;
        }

        node.IsSelected = node.Children.Count > 0 && node.Children.All(child => child.IsSelected);
        UpdateDatasetParentSelection(node.Parent);
    }

    private static bool RecalculateDatasetNodeSelectionFromChildren(DatasetSelectionNode node)
    {
        if (node.Children.Count == 0)
        {
            return node.IsSelected;
        }

        var allChildrenSelected = true;
        foreach (var child in node.Children)
        {
            allChildrenSelected &= RecalculateDatasetNodeSelectionFromChildren(child);
        }

        node.IsSelected = allChildrenSelected;
        return node.IsSelected;
    }

    private IReadOnlyCollection<string> GatherSelectedDatasetKeys()
    {
        var selectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in DatasetSelectionNodes)
        {
            GatherSelectedDatasetKeys(root, selectedKeys);
        }

        return selectedKeys.ToArray();
    }

    private static void GatherSelectedDatasetKeys(DatasetSelectionNode node, ISet<string> selectedKeys)
    {
        if (!string.IsNullOrWhiteSpace(node.DatasetKey) && node.IsSelected)
        {
            selectedKeys.Add(node.DatasetKey);
        }

        foreach (var child in node.Children)
        {
            GatherSelectedDatasetKeys(child, selectedKeys);
        }
    }

    private async Task ApplyDatasetSelectionToViewModelAsync(bool forceDownload)
    {
        if (isApplyingDatasetSelection)
        {
            return;
        }

        var viewModel = GetViewModel();
        if (viewModel is null)
        {
            return;
        }

        var selectedKeys = GatherSelectedDatasetKeys()
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var nextSelectionFingerprint = string.Join("|", selectedKeys);
        viewModel.SelectedDatasetSourcesCsv = string.Join(',', selectedKeys);

        if (!forceDownload && (!hasPendingDatasetSelectionChanges ||
                               string.Equals(nextSelectionFingerprint, lastAppliedDatasetSelectionFingerprint, StringComparison.Ordinal)))
        {
            return;
        }

        hasPendingDatasetSelectionChanges = false;
        lastAppliedDatasetSelectionFingerprint = nextSelectionFingerprint;

        if (selectedKeys.Length == 0)
        {
            viewModel.StatusMessage = "No datasets selected.";
            return;
        }

        if (!await EnsureOpenAddressesApiTokenAsync(viewModel))
        {
            return;
        }

        isApplyingDatasetSelection = true;
        try
        {
            await viewModel.DownloadSelectedDatasetsCommand.ExecuteAsync(selectedKeys);
        }
        finally
        {
            isApplyingDatasetSelection = false;
        }
    }

    private string BuildDatasetSelectionFingerprint()
    {
        var selectedKeys = GatherSelectedDatasetKeys()
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase);

        return string.Join("|", selectedKeys);
    }

    private void ReloadStateFilterTree(MainWindowViewModel viewModel)
    {
        foreach (var stateNode in StateFilterNodes)
        {
            stateNode.PropertyChanged -= OnStateFilterNodePropertyChanged;
            foreach (var child in stateNode.Children)
            {
                child.PropertyChanged -= OnStateFilterNodePropertyChanged;
            }
        }

        StateFilterNodes.Clear();

        var datasetRoot = string.IsNullOrWhiteSpace(viewModel.DatasetRootPath)
            ? string.Empty
            : Path.GetFullPath(viewModel.DatasetRootPath);
        var providerRoot = ResolveOpenAddressesUsRoot(datasetRoot);

        if (string.IsNullOrWhiteSpace(providerRoot) || !Directory.Exists(providerRoot))
        {
            return;
        }

        var (selectedStates, selectedCounties) = ParseStateFilter(viewModel.StatesFilter);

        var stateDirectories = Directory
            .EnumerateDirectories(providerRoot)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var stateDirectory in stateDirectories)
        {
            var stateCode = Path.GetFileName(stateDirectory)?.Trim().ToLowerInvariant() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(stateCode))
            {
                continue;
            }

            var countyNames = Directory
                .EnumerateFiles(stateDirectory, "*.csv", SearchOption.AllDirectories)
                .Select(path => Path.GetFileNameWithoutExtension(path)?.Trim().ToLowerInvariant())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var stateSelected = selectedStates.Contains(stateCode);
            var stateNode = new StateFilterNode(stateCode, stateCode.ToUpperInvariant(), stateSelected);

            foreach (var county in countyNames)
            {
                var isCountySelected = stateSelected ||
                    (selectedCounties.TryGetValue(stateCode, out var countySet) && countySet.Contains(county!));

                var childNode = new StateFilterNode(county!, county!, isCountySelected, stateNode);
                childNode.PropertyChanged += OnStateFilterNodePropertyChanged;
                stateNode.Children.Add(childNode);
            }

            stateNode.PropertyChanged += OnStateFilterNodePropertyChanged;
            StateFilterNodes.Add(stateNode);
        }
    }

    private static string? ResolveOpenAddressesUsRoot(string datasetRoot)
    {
        if (string.IsNullOrWhiteSpace(datasetRoot) || !Directory.Exists(datasetRoot))
        {
            return null;
        }

        var nestedProviderRoot = Path.Combine(datasetRoot, "openaddresses", "us");
        if (Directory.Exists(nestedProviderRoot))
        {
            return nestedProviderRoot;
        }

        var providerUsRoot = Path.Combine(datasetRoot, "us");
        if (Directory.Exists(providerUsRoot))
        {
            return providerUsRoot;
        }

        var providerFolder = Directory
            .EnumerateDirectories(datasetRoot)
            .FirstOrDefault(path => string.Equals(Path.GetFileName(path), "openaddresses", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(providerFolder))
        {
            var usFolder = Path.Combine(providerFolder, "us");
            if (Directory.Exists(usFolder))
            {
                return usFolder;
            }
        }

        if (Directory.Exists(Path.Combine(datasetRoot, "md")) ||
            Directory.EnumerateDirectories(datasetRoot).Any(path => Path.GetFileName(path)?.Length == 2))
        {
            return datasetRoot;
        }

        return null;
    }

    private void ApplyStateFilterSelectionToViewModel()
    {
        var viewModel = GetViewModel();
        if (viewModel is null)
        {
            return;
        }

        var filters = new List<string>();

        foreach (var state in StateFilterNodes.OrderBy(node => node.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (state.IsSelected)
            {
                filters.Add(state.Key);
                continue;
            }

            foreach (var county in state.Children
                         .Where(node => node.IsSelected)
                         .OrderBy(node => node.Key, StringComparer.OrdinalIgnoreCase))
            {
                filters.Add($"{state.Key}:{county.Key}");
            }
        }

        viewModel.StatesFilter = string.Join(',', filters);
    }

    private static (HashSet<string> States, Dictionary<string, HashSet<string>> Counties) ParseStateFilter(string? statesFilter)
    {
        var states = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var countiesByState = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(statesFilter))
        {
            return (states, countiesByState);
        }

        var tokens = statesFilter
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var token in tokens)
        {
            var parts = token
                .Split(':', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (parts.Length == 1)
            {
                states.Add(parts[0].ToLowerInvariant());
                continue;
            }

            var state = parts[0].ToLowerInvariant();
            var county = parts[1].ToLowerInvariant();

            if (!countiesByState.TryGetValue(state, out var countySet))
            {
                countySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                countiesByState[state] = countySet;
            }

            countySet.Add(county);
        }

        return (states, countiesByState);
    }

    private void OnStateFilterNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (isSynchronizingStateFilterSelection ||
            e.PropertyName != nameof(StateFilterNode.IsSelected) ||
            sender is not StateFilterNode node)
        {
            return;
        }

        isSynchronizingStateFilterSelection = true;

        if (node.Parent is null)
        {
            foreach (var child in node.Children)
            {
                child.IsSelected = node.IsSelected;
            }
        }
        else
        {
            node.Parent.IsSelected = node.Parent.Children.Count > 0 && node.Parent.Children.All(child => child.IsSelected);
        }

        isSynchronizingStateFilterSelection = false;
    }

    private async Task<string?> PickOpenFileAsync(string title, string? currentPath)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            return null;
        }

        IStorageFolder? suggestedStartLocation = await GetSuggestedStartFolderAsync(topLevel.StorageProvider, currentPath);
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("CSV")
                {
                    Patterns = ["*.csv"]
                },
                FilePickerFileTypes.All
            ],
            SuggestedStartLocation = suggestedStartLocation
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async Task<string?> PickFolderAsync(string title, string? currentPath)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            return null;
        }

        IStorageFolder? suggestedStartLocation = await GetSuggestedStartFolderAsync(topLevel.StorageProvider, currentPath);
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = suggestedStartLocation
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private static async Task<IStorageFolder?> GetSuggestedStartFolderAsync(IStorageProvider storageProvider, string? currentPath)
    {
        if (string.IsNullOrWhiteSpace(currentPath))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(currentPath);
        var directory = Directory.Exists(fullPath)
            ? fullPath
            : Path.GetDirectoryName(fullPath);

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        return await storageProvider.TryGetFolderFromPathAsync(directory);
    }

    public sealed class StateFilterNode : INotifyPropertyChanged
    {
        private bool isSelected;

        public StateFilterNode(string key, string label, bool isSelected, StateFilterNode? parent = null)
        {
            Key = key;
            Label = label;
            this.isSelected = isSelected;
            Parent = parent;
            Children = [];
        }

        public string Key { get; }

        public string Label { get; }

        public StateFilterNode? Parent { get; }

        public ObservableCollection<StateFilterNode> Children { get; }

        public bool IsSelected
        {
            get => isSelected;
            set
            {
                if (isSelected == value)
                {
                    return;
                }

                isSelected = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public sealed class DatasetSelectionNode : INotifyPropertyChanged
    {
        private bool isSelected;

        public DatasetSelectionNode(string keyPath, string label, DatasetSelectionNode? parent, bool isSelected, string? datasetKey)
        {
            KeyPath = keyPath;
            Label = label;
            Parent = parent;
            DatasetKey = datasetKey;
            this.isSelected = isSelected;
            Children = [];
        }

        public string KeyPath { get; }

        public string Label { get; }

        public DatasetSelectionNode? Parent { get; }

        public string? DatasetKey { get; }

        public ObservableCollection<DatasetSelectionNode> Children { get; }

        public bool IsSelected
        {
            get => isSelected;
            set
            {
                if (isSelected == value)
                {
                    return;
                }

                isSelected = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
