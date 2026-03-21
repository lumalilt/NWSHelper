using CommunityToolkit.Mvvm.ComponentModel;

namespace NWSHelper.Gui.ViewModels;

public enum WorkflowStage
{
    Setup,
    Preview,
    Run,
    Results,
    Settings,
}

public partial class TerritoryPreviewItemViewModel : ObservableObject
{
    public TerritoryPreviewItemViewModel(
        string territoryId,
        string territoryLabel,
        int existingCount,
        int newCount,
        int totalCount,
        bool hasWarning,
        string proposedOutputPath,
        bool isSelected)
    {
        TerritoryId = territoryId;
        TerritoryLabel = territoryLabel;
        ExistingCount = existingCount;
        NewCount = newCount;
        TotalCount = totalCount;
        HasWarning = hasWarning;
        ProposedOutputPath = proposedOutputPath;
        IsSelected = isSelected;
    }

    public string TerritoryId { get; }

    public string TerritoryLabel { get; }

    public int ExistingCount { get; }

    public int NewCount { get; }

    public int TotalCount { get; }

    public bool HasWarning { get; }

    public string WarningLabel => HasWarning ? "Yes" : "No";

    public string ProposedOutputPath { get; }

    [ObservableProperty]
    private bool isSelected;
}

public partial class ProgressLaneViewModel : ObservableObject
{
    public ProgressLaneViewModel(string title, double percentComplete, int processedCount, int matchedCount)
    {
        Title = title;
        PercentComplete = percentComplete;
        ProcessedCount = processedCount;
        MatchedCount = matchedCount;
    }

    public string Title { get; }

    [ObservableProperty]
    private double percentComplete;

    [ObservableProperty]
    private int processedCount;

    [ObservableProperty]
    private int matchedCount;
}

public partial class MapTerritorySelectionItemViewModel : ObservableObject
{
    public MapTerritorySelectionItemViewModel(string territoryId, string displayLabel, bool isSelected)
    {
        TerritoryId = territoryId;
        DisplayLabel = displayLabel;
        IsSelected = isSelected;
    }

    public string TerritoryId { get; }

    public string DisplayLabel { get; }

    [ObservableProperty]
    private bool isSelected;
}

public class TerritoryResultItemViewModel
{
    public TerritoryResultItemViewModel(
        string label,
        int existingCount,
        int foundCount,
        int distinctCount,
        int newCount,
        int writtenCount,
        string warningMarker,
        string addFill,
        string geoFill,
        string overwrite,
        string consolidatedOnly,
        string outputPath)
    {
        Label = label;
        ExistingCount = existingCount;
        FoundCount = foundCount;
        DistinctCount = distinctCount;
        NewCount = newCount;
        WrittenCount = writtenCount;
        WarningMarker = warningMarker;
        AddFill = addFill;
        GeoFill = geoFill;
        Overwrite = overwrite;
        ConsolidatedOnly = consolidatedOnly;
        OutputPath = outputPath;
    }

    public string Label { get; }

    public int ExistingCount { get; }

    public int FoundCount { get; }

    public int DistinctCount { get; }

    public int NewCount { get; }

    public int WrittenCount { get; }

    public string WarningMarker { get; }

    public string AddFill { get; }

    public string GeoFill { get; }

    public string Overwrite { get; }

    public string ConsolidatedOnly { get; }

    public string OutputPath { get; }

    public bool HasOutputPath => !string.IsNullOrWhiteSpace(OutputPath);
}

public class OutputArtifactItemViewModel
{
    public OutputArtifactItemViewModel(string group, string name, string path)
    {
        Group = group;
        Name = name;
        Path = path;
    }

    public string Group { get; }

    public string Name { get; }

    public string Path { get; }

    public bool HasPath => !string.IsNullOrWhiteSpace(Path);
}
