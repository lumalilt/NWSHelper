using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NWSHelper.Gui.Services;

public sealed record DatasetProviderOption(string Id, string DisplayName, string RelativeRootFolder)
{
    public override string ToString() => DisplayName;
}

public sealed record DatasetCatalogItem(
    string ProviderId,
    string Key,
    string DisplayName,
    int? JobId,
    long? SizeBytes);

public sealed record DatasetDownloadProgress(
    int Completed,
    int Total,
    string CurrentDataset,
    string Message)
{
    public double PercentComplete => Total <= 0
        ? 0
        : (Completed / (double)Total) * 100d;
}

public sealed record DatasetDownloadRequest(
    string ProviderId,
    string DatasetRootPath,
    IReadOnlyCollection<string> SelectedDatasetKeys,
    string? OpenAddressesApiBaseUrl,
    string? OpenAddressesApiToken);

public sealed record DatasetDownloadResult(
    string ProviderDatasetRootPath,
    int DownloadedCount,
    int RequestedCount);

public sealed record DatasetProviderConnectionTestResult(
    bool IsSuccess,
    string Message);

public interface IDatasetDownloadService
{
    IReadOnlyList<DatasetProviderOption> GetProviders();

    string ResolveProviderDatasetRoot(string datasetRootPath, string providerId);

    Task<IReadOnlyList<DatasetCatalogItem>> GetDatasetsAsync(
        string providerId,
        string? openAddressesApiBaseUrl,
        CancellationToken cancellationToken);

    Task<DatasetDownloadResult> DownloadDatasetsAsync(
        DatasetDownloadRequest request,
        IProgress<DatasetDownloadProgress>? progress,
        CancellationToken cancellationToken);

    Task<DatasetProviderConnectionTestResult> TestProviderConnectionAsync(
        string providerId,
        string? openAddressesApiBaseUrl,
        string? openAddressesApiToken,
        CancellationToken cancellationToken);
}

