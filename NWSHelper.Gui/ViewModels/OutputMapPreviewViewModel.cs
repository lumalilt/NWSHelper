using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NWSHelper.Core.Models;
using NWSHelper.Core.Services;
using NWSHelper.Core.Utils;

namespace NWSHelper.Gui.ViewModels;

public sealed partial class OutputMapPreviewViewModel : ViewModelBase
{
    private const double CanvasWidth = 1400;
    private const double CanvasHeight = 900;
    private const double MaxMercatorLatitude = 85.05112878;
    private const int TileSize = 256;
    private const int MinZoom = 1;
    private const int MaxZoom = 19;
    private const int PreloadSeedZoom = 1;
    private const int PreloadValidCoordinateLimit = 80;
    private const int PreloadMaxFiles = 10;
    private const double TouchpadZoomSensitivity = 0.42;
    private const double TouchpadZoomStepThreshold = 0.85;
    private const int MaxProjectedZoomCacheEntries = 6;
    private const double ViewportCullMarginPixels = 24;
    private const double SpatialIndexCellSizePixels = TileSize * 2.0;
    private const int ViewportRefreshThrottleMilliseconds = 22;
    private const int MaxConcurrentTileFetches = 6;
    private const int InteractionSettleMilliseconds = 150;
    private const int InteractionMaxPointRenderCount = 3200;
    private const int MaxTileWindowCacheEntries = 96;
    private const int DefaultTileCacheLifeDays = 7;

    private static readonly HttpClient TileHttpClient = CreateTileClient();
    private static readonly ConcurrentDictionary<string, Bitmap> TileBitmapCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Task<byte[]?>> TileFetchTasks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> TileFailureTimes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, BoundaryCacheEntry> BoundaryCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<TileWindowCacheKey, IReadOnlyList<TileRequestSeed>> TileWindowRequestSeedCache = [];
    private static readonly string PersistentTileCacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NWSHelper",
        "MapTileCache");

    private static TimeSpan persistentTileCacheLifetime = TimeSpan.FromDays(DefaultTileCacheLifeDays);

    public static bool EnableIncrementalCollectionDiffs { get; set; } = true;

    public static bool EnableRenderItemReuse { get; set; } = true;

    public static bool EnableAddressPointDeduplication { get; set; } = true;

    public static void SetPersistentTileCacheLifeDays(int days)
    {
        var normalizedDays = Math.Clamp(days, 1, 365);
        persistentTileCacheLifetime = TimeSpan.FromDays(normalizedDays);
    }

    public static void ResetPersistentTileCache()
    {
        TileBitmapCache.Clear();
        TileFetchTasks.Clear();
        TileFailureTimes.Clear();

        try
        {
            if (Directory.Exists(PersistentTileCacheDirectory))
            {
                Directory.Delete(PersistentTileCacheDirectory, recursive: true);
            }
        }
        catch
        {
        }
    }

    private readonly List<RawPoint> rawPoints = [];
    private readonly List<RawBoundary> rawBoundaries = [];
    private readonly Dictionary<int, IReadOnlyList<ProjectedPoint>> projectedPointsByZoom = [];
    private readonly Dictionary<int, IReadOnlyList<ProjectedBoundary>> projectedBoundariesByZoom = [];
    private readonly Dictionary<int, SpatialIndex<ProjectedPoint>> pointSpatialIndexesByZoom = [];
    private readonly Dictionary<int, SpatialIndex<ProjectedBoundary>> boundarySpatialIndexesByZoom = [];
    private readonly Stack<MapPreviewTileViewModel> pooledTiles = [];
    private readonly Stack<MapBoundaryViewModel> pooledBoundaries = [];
    private readonly Stack<MapPreviewPointViewModel> pooledPoints = [];
    private readonly HashSet<string> selectedTerritoryIds;
    private readonly string previewContextLabel;
    private readonly DispatcherTimer viewportRefreshTimer;

    private double centerWorldX;
    private double centerWorldY;
    private int tileZoom = MinZoom;
    private int fitZoom = MinZoom;
    private double fitCenterLatitude;
    private double fitCenterLongitude;
    private int viewportVersion;
    private double zoomDeltaAccumulator;
    private bool viewportRefreshPending;
    private bool viewportRefreshRequested;
    private double lastTileFetchMilliseconds;
    private DateTime interactionLastActivityUtc;
    private bool interactionRefinementPending;

    [ObservableProperty]
    private string selectedMapProvider;

    [ObservableProperty]
    private string providerNote = "OpenStreetMap tile layer (Web Mercator)";

    [ObservableProperty]
    private string coverageNote = string.Empty;

    [ObservableProperty]
    private string mapLayerStatus = "Loading tiles...";

    [ObservableProperty]
    private bool showFallbackGrid = true;

    [ObservableProperty]
    private string performanceOverlay = string.Empty;

    [ObservableProperty]
    private string headerTitle;

    [ObservableProperty]
    private string windowTitle;

    [ObservableProperty]
    private bool showPerformanceOverlay = true;

    [ObservableProperty]
    private bool showLoadPerformanceNotice = true;

    public OutputMapPreviewViewModel(
        string sourcePath,
        string? boundaryCsvPath = null,
        string? previewContextLabel = null,
        IReadOnlyCollection<string>? selectedTerritoryIds = null)
    {
        SourcePath = sourcePath;
        BoundaryCsvPath = boundaryCsvPath;
        this.selectedTerritoryIds = selectedTerritoryIds?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? [];
        this.previewContextLabel = string.IsNullOrWhiteSpace(previewContextLabel)
            ? "Output"
            : previewContextLabel.Trim();

        MapProviders = ["OpenStreetMap"];
        selectedMapProvider = MapProviders[0];

        headerTitle = $"{this.previewContextLabel} Map Preview";
        windowTitle = headerTitle;

        viewportRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(ViewportRefreshThrottleMilliseconds)
        };
        viewportRefreshTimer.Tick += OnViewportRefreshTimerTick;

        Tiles = [];
        Boundaries = [];
        Points = [];

        _ = InitializeMapAsync();
    }

    public string SourcePath { get; }

    public string? BoundaryCsvPath { get; }

    public IReadOnlyList<string> MapProviders { get; }

    public ObservableCollection<MapPreviewTileViewModel> Tiles { get; }

    public ObservableCollection<MapBoundaryViewModel> Boundaries { get; }

    public ObservableCollection<MapPreviewPointViewModel> Points { get; }

    public bool HasPerformanceOverlay => !string.IsNullOrWhiteSpace(PerformanceOverlay);

    public bool IsPerformanceOverlayVisible => ShowPerformanceOverlay && HasPerformanceOverlay;

    public string ZoomStatus => $"Zoom: {tileZoom}";

    partial void OnPerformanceOverlayChanged(string value)
    {
        OnPropertyChanged(nameof(HasPerformanceOverlay));
        OnPropertyChanged(nameof(IsPerformanceOverlayVisible));
    }

    partial void OnShowPerformanceOverlayChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPerformanceOverlayVisible));
    }

    [RelayCommand]
    private void ZoomIn() => AdjustZoom(1);

    [RelayCommand]
    private void ZoomOut() => AdjustZoom(-1);

    [RelayCommand]
    private void ResetZoom()
    {
        zoomDeltaAccumulator = 0;
        tileZoom = fitZoom;
        (centerWorldX, centerWorldY) = ToWorldPixels(fitCenterLatitude, fitCenterLongitude, tileZoom);
        NormalizeCenterWorld();
        OnPropertyChanged(nameof(ZoomStatus));
        RefreshViewport();
    }

    public void ZoomByTouchpadDelta(double deltaY, Point? viewportPoint = null)
    {
        if (Math.Abs(deltaY) < double.Epsilon)
        {
            return;
        }

        zoomDeltaAccumulator += deltaY * TouchpadZoomSensitivity;

        var steps = (int)Math.Floor(Math.Abs(zoomDeltaAccumulator) / TouchpadZoomStepThreshold);
        if (steps <= 0)
        {
            return;
        }

        var direction = zoomDeltaAccumulator > 0 ? 1 : -1;
        var requestedDelta = direction * steps;
        var appliedDelta = AdjustZoom(requestedDelta, viewportPoint);

        if (appliedDelta == 0)
        {
            zoomDeltaAccumulator = 0;
            return;
        }

        zoomDeltaAccumulator -= appliedDelta * TouchpadZoomStepThreshold;
    }

    public void PanByPixels(double deltaX, double deltaY)
    {
        if (Math.Abs(deltaX) < double.Epsilon && Math.Abs(deltaY) < double.Epsilon)
        {
            return;
        }

        MarkInteractionActivity();
        centerWorldX -= deltaX;
        centerWorldY -= deltaY;
        NormalizeCenterWorld();
        RequestViewportRefresh();
    }

    public static async Task PreloadForOutputsAsync(
        IReadOnlyCollection<string> outputPaths,
        string? boundaryCsvPath,
        CancellationToken cancellationToken = default)
    {
        if (outputPaths.Count == 0)
        {
            return;
        }

        var distinctPaths = outputPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(PreloadMaxFiles)
            .ToArray();

        if (distinctPaths.Length == 0)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(boundaryCsvPath) && File.Exists(Path.GetFullPath(boundaryCsvPath)))
        {
            _ = Task.Run(() => GetBoundariesCached(Path.GetFullPath(boundaryCsvPath)));
        }

        foreach (var outputPath in distinctPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryReadPreloadSeed(outputPath, out var preloadSeed))
            {
                continue;
            }

            var (centerWorldX, centerWorldY) = ToWorldPixels(preloadSeed.CenterLatitude, preloadSeed.CenterLongitude, PreloadSeedZoom);

            var topLeftWorldX = centerWorldX - (CanvasWidth / 2.0);
            var topLeftWorldY = centerWorldY - (CanvasHeight / 2.0);
            var requests = BuildTileRequests(PreloadSeedZoom, topLeftWorldX, topLeftWorldY, marginTiles: 0);

            var preloadTasks = requests.Select(EnsureTileCachedAsync).ToArray();
            await Task.WhenAll(preloadTasks).ConfigureAwait(false);
        }
    }

    private static bool TryReadPreloadSeed(string sourcePath, out PreloadSeed seed)
    {
        seed = default;

        try
        {
            using var stream = File.OpenRead(sourcePath);
            using var reader = new StreamReader(stream);
            using var csv = CsvFactory.CreateReader(reader);

            if (!csv.Read() || !csv.ReadHeader())
            {
                return false;
            }

            var headers = csv.HeaderRecord ?? [];
            var latitudeSum = 0.0;
            var longitudeSum = 0.0;
            var validCount = 0;

            while (csv.Read())
            {
                var latitudeRaw = GetField(csv, headers, "Latitude", "Lat");
                var longitudeRaw = GetField(csv, headers, "Longitude", "Lon", "Lng");

                if (!double.TryParse(latitudeRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) ||
                    !double.TryParse(longitudeRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude) ||
                    latitude is < -90 or > 90 ||
                    longitude is < -180 or > 180)
                {
                    continue;
                }

                latitudeSum += latitude;
                longitudeSum += longitude;
                validCount++;

                if (validCount >= PreloadValidCoordinateLimit)
                {
                    break;
                }
            }

            if (validCount == 0)
            {
                return false;
            }

            seed = new PreloadSeed(latitudeSum / validCount, longitudeSum / validCount);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task InitializeMapAsync()
    {
        rawPoints.Clear();
        rawBoundaries.Clear();
        projectedPointsByZoom.Clear();
        projectedBoundariesByZoom.Clear();
        pointSpatialIndexesByZoom.Clear();
        boundarySpatialIndexesByZoom.Clear();
        zoomDeltaAccumulator = 0;
        ShowLoadPerformanceNotice = true;
        MapLayerStatus = "Loading map data...";
        PerformanceOverlay = string.Empty;

        if (!File.Exists(SourcePath))
        {
            HeaderTitle = $"File not found: {Path.GetFileName(SourcePath)}";
            WindowTitle = HeaderTitle;
            CoverageNote = "Rows: 0 • Boundary points: 0 • Address points: 0 • Address status: DoNotCall 0, Other 0 • Boundaries: 0";
            MapLayerStatus = "No map data available";
            ShowLoadPerformanceNotice = false;
            return;
        }

        MapSourceData sourceData;
        List<RawBoundary> selectedBoundaries;

        try
        {
            sourceData = await Task.Run(() => ReadMapSourceData(SourcePath, selectedTerritoryIds));
            var territoryIdsForBoundaries = selectedTerritoryIds.Count > 0
                ? selectedTerritoryIds
                : sourceData.TerritoryIds;
            selectedBoundaries = await Task.Run(() => LoadBoundaryOverlays(BoundaryCsvPath, territoryIdsForBoundaries, sourceData.TerritoryNumbers));
        }
        catch
        {
            HeaderTitle = $"{Path.GetFileName(SourcePath)} • Map initialization failed";
            WindowTitle = HeaderTitle;
            CoverageNote = "Rows: 0 • Boundary points: 0 • Address points: 0 • Address status: DoNotCall 0, Other 0 • Boundaries: 0";
            MapLayerStatus = "Failed to load map data";
            ShowLoadPerformanceNotice = false;
            return;
        }

        if (sourceData.Points.Count == 0)
        {
            HeaderTitle = $"{Path.GetFileName(SourcePath)} • 0 mapped • {sourceData.MissingCoordinateCount} missing coordinates";
            WindowTitle = HeaderTitle;
            CoverageNote = $"Rows: {sourceData.TotalRowCount} • Boundary points: 0 • Address points: 0 • Address status: DoNotCall 0, Other 0 • Boundaries: 0";
            MapLayerStatus = "No valid coordinates to render";
            ShowLoadPerformanceNotice = false;
            return;
        }

        rawPoints.AddRange(sourceData.Points);
        rawBoundaries.AddRange(selectedBoundaries);
        ShowLoadPerformanceNotice = false;

        var resolvedTerritoryNames = rawBoundaries
            .Select(boundary => boundary.Label)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var territoryDetails = resolvedTerritoryNames.Count switch
        {
            1 => $"Territory {resolvedTerritoryNames[0]}",
            > 1 => $"{resolvedTerritoryNames.Count} territories",
            _ => sourceData.TerritoryNumbers.Count switch
            {
                0 when sourceData.TerritoryIds.Count == 0 => Path.GetFileName(SourcePath),
                0 => $"{sourceData.TerritoryIds.Count} territory IDs",
                1 => $"Territory {sourceData.TerritoryNumbers.First()}" +
                     (sourceData.TerritoryIds.Count == 1 ? $" [ID:{sourceData.TerritoryIds.First()}]" : string.Empty),
                _ => $"{sourceData.TerritoryNumbers.Count} territories"
            }
        };

        HeaderTitle =
            $"{territoryDetails} • {sourceData.Points.Count} mapped • {sourceData.DoNotCallCount} DoNotCall • {sourceData.MissingCoordinateCount} missing coordinates";
        WindowTitle = $"{previewContextLabel} • {territoryDetails}";

        var boundaryPointCount = CountBoundaryPoints(rawBoundaries);
        var nonDoNotCallCount = Math.Max(0, sourceData.Points.Count - sourceData.DoNotCallCount);

        CoverageNote =
            $"Rows: {sourceData.TotalRowCount} • Boundary points: {boundaryPointCount} • Address points: {sourceData.Points.Count} • Address status: DoNotCall {sourceData.DoNotCallCount}, Other {nonDoNotCallCount} • Overlapping address coordinates: {sourceData.OverlappingCoordinateCount} • Boundaries: {rawBoundaries.Count}";

        var geoBounds = CalculateGeoBounds(sourceData.Points);
        fitZoom = DetermineFitZoom(geoBounds);
        fitCenterLatitude = (geoBounds.MinLatitude + geoBounds.MaxLatitude) / 2.0;
        fitCenterLongitude = (geoBounds.MinLongitude + geoBounds.MaxLongitude) / 2.0;

        tileZoom = fitZoom;
        (centerWorldX, centerWorldY) = ToWorldPixels(fitCenterLatitude, fitCenterLongitude, tileZoom);
        NormalizeCenterWorld();
        OnPropertyChanged(nameof(ZoomStatus));

        RefreshViewport();
    }

    private void RefreshViewport()
    {
        var totalRenderStopwatch = Stopwatch.StartNew();
        var interactionRenderMode = IsInteractionRenderModeActive();
        var useIncrementalDiffs = EnableIncrementalCollectionDiffs;

        viewportVersion++;
        var version = viewportVersion;

        if (!useIncrementalDiffs)
        {
            ClearRenderedCollections();
        }

        if (rawPoints.Count == 0)
        {
            if (useIncrementalDiffs)
            {
                TrimTileCollection(0);
                TrimBoundaryCollection(0);
                TrimPointCollection(0);
            }

            return;
        }

        var worldSize = GetWorldSize(tileZoom);
        var topLeftWorldX = centerWorldX - (CanvasWidth / 2.0);
        var topLeftWorldY = centerWorldY - (CanvasHeight / 2.0);
        var viewportMinX = topLeftWorldX - ViewportCullMarginPixels;
        var viewportMaxX = topLeftWorldX + CanvasWidth + ViewportCullMarginPixels;
        var viewportMinY = topLeftWorldY - ViewportCullMarginPixels;
        var viewportMaxY = topLeftWorldY + CanvasHeight + ViewportCullMarginPixels;

        var tileMargin = interactionRenderMode ? 0 : 1;
        var requiredRequests = BuildTileRequests(tileZoom, topLeftWorldX, topLeftWorldY, marginTiles: tileMargin);
        var missing = new List<TileRequest>();
        var loadedCount = 0;
        var renderedTileCount = 0;

        var tileProjectionStopwatch = Stopwatch.StartNew();

        foreach (var request in requiredRequests)
        {
            if (TileBitmapCache.TryGetValue(request.CacheKey, out var bitmap))
            {
                UpsertTile(renderedTileCount, request.ScreenX, request.ScreenY, bitmap);
                renderedTileCount++;
                loadedCount++;
            }
            else
            {
                missing.Add(request);
            }
        }

        if (useIncrementalDiffs)
        {
            TrimTileCollection(renderedTileCount);
        }

        tileProjectionStopwatch.Stop();

        var boundaryProjectionStopwatch = Stopwatch.StartNew();
        var projectedBoundaries = GetProjectedBoundariesForZoom(tileZoom);
        var totalBoundaryCount = projectedBoundaries.Count;
        var boundarySpatialIndex = GetBoundarySpatialIndexForZoom(tileZoom, projectedBoundaries);
        var visibleBoundaryIndices = QueryVisibleIndices(boundarySpatialIndex, viewportMinX, viewportMaxX, viewportMinY, viewportMaxY, worldSize);
        var candidateBoundaryCount = visibleBoundaryIndices.Count;
        var renderedBoundaryCount = 0;

        if (!interactionRenderMode)
        {
            foreach (var boundaryIndex in visibleBoundaryIndices)
            {
                var boundary = projectedBoundaries[boundaryIndex];

                if (!IsProjectedBoundsVisible(boundary.Bounds, topLeftWorldX, topLeftWorldY, worldSize, ViewportCullMarginPixels))
                {
                    continue;
                }

                var screenPoints = BuildBoundaryScreenPoints(boundary, topLeftWorldX, topLeftWorldY, worldSize);

                if (screenPoints.Count >= 2)
                {
                    UpsertBoundary(
                        renderedBoundaryCount,
                        screenPoints,
                        Brushes.Orange,
                        $"Boundary: {boundary.Label}");
                    renderedBoundaryCount++;
                }
            }
        }

        if (useIncrementalDiffs)
        {
            TrimBoundaryCollection(renderedBoundaryCount);
        }

        boundaryProjectionStopwatch.Stop();

        var pointProjectionStopwatch = Stopwatch.StartNew();
        var projectedPoints = GetProjectedPointsForZoom(tileZoom);
        var totalPointCount = projectedPoints.Count;
        var pointSpatialIndex = GetPointSpatialIndexForZoom(tileZoom, projectedPoints);
        var visiblePointIndices = QueryVisibleIndices(pointSpatialIndex, viewportMinX, viewportMaxX, viewportMinY, viewportMaxY, worldSize);
        var candidatePointCount = visiblePointIndices.Count;
        var pointSamplingStep = 1;
        var renderedPointCount = 0;

        if (interactionRenderMode && candidatePointCount > InteractionMaxPointRenderCount)
        {
            pointSamplingStep = (int)Math.Ceiling(candidatePointCount / (double)InteractionMaxPointRenderCount);
        }

        for (var visibleIndex = 0; visibleIndex < visiblePointIndices.Count; visibleIndex += pointSamplingStep)
        {
            var pointIndex = visiblePointIndices[visibleIndex];
            var point = projectedPoints[pointIndex];
            var screenX = NormalizeWrappedDelta(point.WorldX - topLeftWorldX, worldSize);
            var screenY = point.WorldY - topLeftWorldY;

            if (!IsScreenCoordinateVisible(screenX, screenY, ViewportCullMarginPixels))
            {
                continue;
            }

            UpsertPoint(
                renderedPointCount,
                screenX,
                screenY,
                point.IsDoNotCall ? Brushes.IndianRed : Brushes.DodgerBlue,
                point.HoverDetails);
            renderedPointCount++;
        }

        if (useIncrementalDiffs)
        {
            TrimPointCollection(renderedPointCount);
        }

        pointProjectionStopwatch.Stop();
        totalRenderStopwatch.Stop();

        var renderPerfSummary = BuildRenderPerfSummary(
            totalRenderStopwatch.Elapsed.TotalMilliseconds,
            tileProjectionStopwatch.Elapsed.TotalMilliseconds,
            boundaryProjectionStopwatch.Elapsed.TotalMilliseconds,
            pointProjectionStopwatch.Elapsed.TotalMilliseconds,
            missing.Count == 0 ? lastTileFetchMilliseconds : null,
            renderedPointCount,
            renderedBoundaryCount,
            candidatePointCount,
            candidateBoundaryCount,
            totalPointCount,
            totalBoundaryCount,
            interactionRenderMode);

        PerformanceOverlay = renderPerfSummary;

        if (!interactionRenderMode)
        {
            interactionRefinementPending = false;
        }
        else
        {
            RequestViewportRefresh();
        }

        MapLayerStatus = missing.Count == 0
            ? $"OSM tiles loaded: {loadedCount}/{requiredRequests.Count} at zoom {tileZoom}"
            : $"OSM tiles loaded: {loadedCount}/{requiredRequests.Count} at zoom {tileZoom} (fetching {missing.Count})";
        ShowFallbackGrid = loadedCount == 0;

        if (missing.Count > 0)
        {
            lastTileFetchMilliseconds = 0;
            var prioritizedMissing = PrioritizeTileRequests(missing);
            _ = FetchMissingTilesAsync(prioritizedMissing, version);
        }
    }

    private static IReadOnlyList<TileRequest> PrioritizeTileRequests(IReadOnlyList<TileRequest> missing)
    {
        if (missing.Count <= 1)
        {
            return missing;
        }

        var centerX = CanvasWidth / 2.0;
        var centerY = CanvasHeight / 2.0;

        return missing
            .OrderBy(request => TileCenterDistanceSquared(request, centerX, centerY))
            .ToArray();
    }

    private static double TileCenterDistanceSquared(TileRequest request, double centerX, double centerY)
    {
        var tileCenterX = request.ScreenX + (TileSize / 2.0);
        var tileCenterY = request.ScreenY + (TileSize / 2.0);
        var deltaX = tileCenterX - centerX;
        var deltaY = tileCenterY - centerY;
        return (deltaX * deltaX) + (deltaY * deltaY);
    }

    private IReadOnlyList<ProjectedPoint> GetProjectedPointsForZoom(int zoom)
    {
        if (projectedPointsByZoom.TryGetValue(zoom, out var cached))
        {
            return cached;
        }

        var projected = rawPoints
            .Select(point =>
            {
                var (worldX, worldY) = ToWorldPixels(point.Latitude, point.Longitude, zoom);
                return new ProjectedPoint(worldX, worldY, point.IsDoNotCall, point.HoverDetails);
            })
            .ToList();

        StoreProjectedCacheEntry(projectedPointsByZoom, zoom, projected);
        return projected;
    }

    private IReadOnlyList<ProjectedBoundary> GetProjectedBoundariesForZoom(int zoom)
    {
        if (projectedBoundariesByZoom.TryGetValue(zoom, out var cached))
        {
            return cached;
        }

        var projectedBoundaries = new List<ProjectedBoundary>(rawBoundaries.Count);

        foreach (var boundary in rawBoundaries)
        {
            if (boundary.Vertices.Count == 0)
            {
                continue;
            }

            var projectedVertices = new List<ProjectedBoundaryVertex>(boundary.Vertices.Count);
            var minX = double.MaxValue;
            var maxX = double.MinValue;
            var minY = double.MaxValue;
            var maxY = double.MinValue;

            foreach (var vertex in boundary.Vertices)
            {
                var (worldX, worldY) = ToWorldPixels(vertex.Latitude, vertex.Longitude, zoom);
                projectedVertices.Add(new ProjectedBoundaryVertex(worldX, worldY));

                if (worldX < minX)
                {
                    minX = worldX;
                }

                if (worldX > maxX)
                {
                    maxX = worldX;
                }

                if (worldY < minY)
                {
                    minY = worldY;
                }

                if (worldY > maxY)
                {
                    maxY = worldY;
                }
            }

            projectedBoundaries.Add(new ProjectedBoundary(
                boundary.Label,
                SimplifyBoundaryVertices(projectedVertices, GetBoundarySimplificationTolerancePixels(zoom)),
                new ProjectedWorldBounds(minX, maxX, minY, maxY)));
        }

        StoreProjectedCacheEntry(projectedBoundariesByZoom, zoom, projectedBoundaries);
        return projectedBoundaries;
    }

    private static void StoreProjectedCacheEntry<T>(Dictionary<int, IReadOnlyList<T>> cache, int zoom, IReadOnlyList<T> values)
    {
        if (!cache.ContainsKey(zoom) && cache.Count >= MaxProjectedZoomCacheEntries)
        {
            cache.Clear();
        }

        cache[zoom] = values;
    }

    private static double GetBoundarySimplificationTolerancePixels(int zoom)
    {
        return zoom switch
        {
            <= 4 => 3.6,
            <= 6 => 2.4,
            <= 8 => 1.6,
            <= 10 => 1.0,
            <= 12 => 0.6,
            _ => 0
        };
    }

    private static IReadOnlyList<ProjectedBoundaryVertex> SimplifyBoundaryVertices(
        IReadOnlyList<ProjectedBoundaryVertex> vertices,
        double tolerancePixels)
    {
        if (tolerancePixels <= 0 || vertices.Count < 5)
        {
            return vertices;
        }

        var isClosed = IsSameProjectedVertex(vertices[0], vertices[^1]);
        var openVertices = isClosed
            ? vertices.Take(vertices.Count - 1).ToList()
            : vertices.ToList();

        if (openVertices.Count < 3)
        {
            return vertices;
        }

        var simplifiedOpen = SimplifyPolylineRdp(openVertices, tolerancePixels);
        var minimumOpenCount = isClosed ? 3 : 2;

        if (simplifiedOpen.Count < minimumOpenCount)
        {
            simplifiedOpen = openVertices;
        }

        if (!isClosed)
        {
            return simplifiedOpen;
        }

        var closedSimplified = new List<ProjectedBoundaryVertex>(simplifiedOpen.Count + 1);
        closedSimplified.AddRange(simplifiedOpen);
        closedSimplified.Add(simplifiedOpen[0]);
        return closedSimplified;
    }

    private static List<ProjectedBoundaryVertex> SimplifyPolylineRdp(
        IReadOnlyList<ProjectedBoundaryVertex> points,
        double tolerancePixels)
    {
        if (points.Count <= 2)
        {
            return points.ToList();
        }

        var keep = new bool[points.Count];
        keep[0] = true;
        keep[^1] = true;

        var toleranceSquared = tolerancePixels * tolerancePixels;
        var stack = new Stack<(int Start, int End)>();
        stack.Push((0, points.Count - 1));

        while (stack.Count > 0)
        {
            var (startIndex, endIndex) = stack.Pop();
            if (endIndex <= startIndex + 1)
            {
                continue;
            }

            var farthestDistanceSquared = 0.0;
            var farthestIndex = -1;

            var start = points[startIndex];
            var end = points[endIndex];

            for (var pointIndex = startIndex + 1; pointIndex < endIndex; pointIndex++)
            {
                var distanceSquared = GetSegmentDistanceSquared(points[pointIndex], start, end);
                if (distanceSquared > farthestDistanceSquared)
                {
                    farthestDistanceSquared = distanceSquared;
                    farthestIndex = pointIndex;
                }
            }

            if (farthestIndex >= 0 && farthestDistanceSquared > toleranceSquared)
            {
                keep[farthestIndex] = true;
                stack.Push((startIndex, farthestIndex));
                stack.Push((farthestIndex, endIndex));
            }
        }

        var simplified = new List<ProjectedBoundaryVertex>(points.Count);
        for (var index = 0; index < points.Count; index++)
        {
            if (keep[index])
            {
                simplified.Add(points[index]);
            }
        }

        return simplified;
    }

    private static double GetSegmentDistanceSquared(
        ProjectedBoundaryVertex point,
        ProjectedBoundaryVertex segmentStart,
        ProjectedBoundaryVertex segmentEnd)
    {
        var deltaX = segmentEnd.WorldX - segmentStart.WorldX;
        var deltaY = segmentEnd.WorldY - segmentStart.WorldY;

        if (Math.Abs(deltaX) <= double.Epsilon && Math.Abs(deltaY) <= double.Epsilon)
        {
            var startDistanceX = point.WorldX - segmentStart.WorldX;
            var startDistanceY = point.WorldY - segmentStart.WorldY;
            return (startDistanceX * startDistanceX) + (startDistanceY * startDistanceY);
        }

        var projection = ((point.WorldX - segmentStart.WorldX) * deltaX +
                          (point.WorldY - segmentStart.WorldY) * deltaY) /
                         ((deltaX * deltaX) + (deltaY * deltaY));

        var clampedProjection = Math.Clamp(projection, 0, 1);
        var nearestX = segmentStart.WorldX + (clampedProjection * deltaX);
        var nearestY = segmentStart.WorldY + (clampedProjection * deltaY);
        var distanceX = point.WorldX - nearestX;
        var distanceY = point.WorldY - nearestY;

        return (distanceX * distanceX) + (distanceY * distanceY);
    }

    private static bool IsSameProjectedVertex(ProjectedBoundaryVertex left, ProjectedBoundaryVertex right)
    {
        return Math.Abs(left.WorldX - right.WorldX) <= 1e-9 &&
               Math.Abs(left.WorldY - right.WorldY) <= 1e-9;
    }

    private static void StoreSpatialIndexCacheEntry<T>(Dictionary<int, SpatialIndex<T>> cache, int zoom, SpatialIndex<T> values)
    {
        if (!cache.ContainsKey(zoom) && cache.Count >= MaxProjectedZoomCacheEntries)
        {
            cache.Clear();
        }

        cache[zoom] = values;
    }

    private SpatialIndex<ProjectedPoint> GetPointSpatialIndexForZoom(int zoom, IReadOnlyList<ProjectedPoint> projectedPoints)
    {
        if (pointSpatialIndexesByZoom.TryGetValue(zoom, out var cached))
        {
            return cached;
        }

        var index = BuildSpatialIndex(
            projectedPoints,
            point => new ProjectedWorldBounds(point.WorldX, point.WorldX, point.WorldY, point.WorldY));

        StoreSpatialIndexCacheEntry(pointSpatialIndexesByZoom, zoom, index);
        return index;
    }

    private SpatialIndex<ProjectedBoundary> GetBoundarySpatialIndexForZoom(int zoom, IReadOnlyList<ProjectedBoundary> projectedBoundaries)
    {
        if (boundarySpatialIndexesByZoom.TryGetValue(zoom, out var cached))
        {
            return cached;
        }

        var index = BuildSpatialIndex(projectedBoundaries, boundary => boundary.Bounds);
        StoreSpatialIndexCacheEntry(boundarySpatialIndexesByZoom, zoom, index);
        return index;
    }

    private static SpatialIndex<T> BuildSpatialIndex<T>(
        IReadOnlyList<T> items,
        Func<T, ProjectedWorldBounds> boundsSelector)
    {
        var bins = new Dictionary<SpatialCellKey, List<int>>();

        for (var itemIndex = 0; itemIndex < items.Count; itemIndex++)
        {
            var bounds = boundsSelector(items[itemIndex]);
            var minCellX = (int)Math.Floor(bounds.MinX / SpatialIndexCellSizePixels);
            var maxCellX = (int)Math.Floor(bounds.MaxX / SpatialIndexCellSizePixels);
            var minCellY = (int)Math.Floor(bounds.MinY / SpatialIndexCellSizePixels);
            var maxCellY = (int)Math.Floor(bounds.MaxY / SpatialIndexCellSizePixels);

            for (var cellX = minCellX; cellX <= maxCellX; cellX++)
            {
                for (var cellY = minCellY; cellY <= maxCellY; cellY++)
                {
                    var key = new SpatialCellKey(cellX, cellY);
                    if (!bins.TryGetValue(key, out var cellItems))
                    {
                        cellItems = [];
                        bins[key] = cellItems;
                    }

                    cellItems.Add(itemIndex);
                }
            }
        }

        return new SpatialIndex<T>(items, bins, SpatialIndexCellSizePixels);
    }

    private static List<int> QueryVisibleIndices<T>(
        SpatialIndex<T> index,
        double viewportMinX,
        double viewportMaxX,
        double viewportMinY,
        double viewportMaxY,
        double worldSize)
    {
        if (index.Items.Count == 0 || index.Bins.Count == 0)
        {
            return [];
        }

        var minCellY = (int)Math.Floor(viewportMinY / index.CellSize);
        var maxCellY = (int)Math.Floor(viewportMaxY / index.CellSize);
        var selected = new HashSet<int>();

        foreach (var (segmentMinX, segmentMaxX) in GetViewportXSegments(viewportMinX, viewportMaxX, worldSize))
        {
            var minCellX = (int)Math.Floor(segmentMinX / index.CellSize);
            var maxCellX = (int)Math.Floor(segmentMaxX / index.CellSize);

            for (var cellX = minCellX; cellX <= maxCellX; cellX++)
            {
                for (var cellY = minCellY; cellY <= maxCellY; cellY++)
                {
                    var key = new SpatialCellKey(cellX, cellY);
                    if (!index.Bins.TryGetValue(key, out var candidateItems))
                    {
                        continue;
                    }

                    foreach (var candidateIndex in candidateItems)
                    {
                        selected.Add(candidateIndex);
                    }
                }
            }
        }

        var visibleIndices = selected.ToList();
        visibleIndices.Sort();
        return visibleIndices;
    }

    private static IEnumerable<(double MinX, double MaxX)> GetViewportXSegments(double viewportMinX, double viewportMaxX, double worldSize)
    {
        if (worldSize <= 0)
        {
            yield return (viewportMinX, viewportMaxX);
            yield break;
        }

        var span = viewportMaxX - viewportMinX;
        if (span >= worldSize)
        {
            yield return (0, worldSize);
            yield break;
        }

        var normalizedMinX = Mod(viewportMinX, worldSize);
        var normalizedMaxX = normalizedMinX + span;

        if (normalizedMaxX <= worldSize)
        {
            yield return (normalizedMinX, normalizedMaxX);
            yield break;
        }

        yield return (normalizedMinX, worldSize);
        yield return (0, normalizedMaxX - worldSize);
    }

    private AvaloniaList<Point> BuildBoundaryScreenPoints(ProjectedBoundary boundary, double topLeftWorldX, double topLeftWorldY, double worldSize)
    {
        var screenPoints = new AvaloniaList<Point>();
        if (boundary.Vertices.Count == 0)
        {
            return screenPoints;
        }

        var projectedVertices = boundary.Vertices;

        if (worldSize <= 0)
        {
            foreach (var projected in projectedVertices)
            {
                screenPoints.Add(new Point(projected.WorldX - topLeftWorldX, projected.WorldY - topLeftWorldY));
            }

            return screenPoints;
        }

        var unwrappedWorldX = new double[projectedVertices.Count];
        unwrappedWorldX[0] = projectedVertices[0].WorldX;

        for (var index = 1; index < projectedVertices.Count; index++)
        {
            var candidateWorldX = projectedVertices[index].WorldX;
            var previousWorldX = unwrappedWorldX[index - 1];

            while (candidateWorldX - previousWorldX > worldSize / 2.0)
            {
                candidateWorldX -= worldSize;
            }

            while (candidateWorldX - previousWorldX < -worldSize / 2.0)
            {
                candidateWorldX += worldSize;
            }

            unwrappedWorldX[index] = candidateWorldX;
        }

        var averageWorldX = unwrappedWorldX.Average();
        var wrapShift = Math.Round((centerWorldX - averageWorldX) / worldSize) * worldSize;

        for (var index = 0; index < projectedVertices.Count; index++)
        {
            var screenX = (unwrappedWorldX[index] + wrapShift) - topLeftWorldX;
            var screenY = projectedVertices[index].WorldY - topLeftWorldY;
            screenPoints.Add(new Point(screenX, screenY));
        }

        return screenPoints;
    }

    private static bool IsProjectedBoundsVisible(
        ProjectedWorldBounds bounds,
        double topLeftWorldX,
        double topLeftWorldY,
        double worldSize,
        double marginPixels)
    {
        var viewportMinY = topLeftWorldY - marginPixels;
        var viewportMaxY = topLeftWorldY + CanvasHeight + marginPixels;

        if (!Intersects(bounds.MinY, bounds.MaxY, viewportMinY, viewportMaxY))
        {
            return false;
        }

        if (worldSize <= 0)
        {
            return false;
        }

        if (worldSize <= CanvasWidth + (marginPixels * 2))
        {
            return true;
        }

        var viewportMinX = topLeftWorldX - marginPixels;
        var viewportMaxX = topLeftWorldX + CanvasWidth + marginPixels;

        for (var wrapShift = -1; wrapShift <= 1; wrapShift++)
        {
            var shiftedMinX = bounds.MinX + (wrapShift * worldSize);
            var shiftedMaxX = bounds.MaxX + (wrapShift * worldSize);

            if (Intersects(shiftedMinX, shiftedMaxX, viewportMinX, viewportMaxX))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsScreenCoordinateVisible(double screenX, double screenY, double marginPixels)
    {
        return screenX >= -marginPixels &&
               screenX <= CanvasWidth + marginPixels &&
               screenY >= -marginPixels &&
               screenY <= CanvasHeight + marginPixels;
    }

    private static bool Intersects(double minA, double maxA, double minB, double maxB)
    {
        return maxA >= minB && maxB >= minA;
    }

    private async Task FetchMissingTilesAsync(IReadOnlyList<TileRequest> missing, int version)
    {
        var tileFetchStopwatch = Stopwatch.StartNew();

        for (var index = 0; index < missing.Count; index += MaxConcurrentTileFetches)
        {
            if (version != viewportVersion)
            {
                return;
            }

            var batchSize = Math.Min(MaxConcurrentTileFetches, missing.Count - index);
            var fetchTasks = new Task[batchSize];

            for (var batchOffset = 0; batchOffset < batchSize; batchOffset++)
            {
                fetchTasks[batchOffset] = EnsureTileCachedAsync(missing[index + batchOffset]);
            }

            await Task.WhenAll(fetchTasks).ConfigureAwait(false);
        }

        tileFetchStopwatch.Stop();

        if (version != viewportVersion)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (version == viewportVersion)
            {
                lastTileFetchMilliseconds = tileFetchStopwatch.Elapsed.TotalMilliseconds;
                RefreshTilesOnly(version);
            }
        });
    }

    private void RefreshTilesOnly(int version)
    {
        if (version != viewportVersion || rawPoints.Count == 0)
        {
            return;
        }

        var useIncrementalDiffs = EnableIncrementalCollectionDiffs;
        if (!useIncrementalDiffs)
        {
            ClearTileCollection();
        }

        var worldSize = GetWorldSize(tileZoom);
        var topLeftWorldX = centerWorldX - (CanvasWidth / 2.0);
        var topLeftWorldY = centerWorldY - (CanvasHeight / 2.0);
        var tileMargin = IsInteractionRenderModeActive() ? 0 : 1;
        var requiredRequests = BuildTileRequests(tileZoom, topLeftWorldX, topLeftWorldY, marginTiles: tileMargin);
        var renderedTileCount = 0;

        var loadedCount = 0;
        var missingCount = 0;
        foreach (var request in requiredRequests)
        {
            if (TileBitmapCache.TryGetValue(request.CacheKey, out var bitmap))
            {
                UpsertTile(renderedTileCount, request.ScreenX, request.ScreenY, bitmap);
                renderedTileCount++;
                loadedCount++;
            }
            else
            {
                missingCount++;
            }
        }

        if (useIncrementalDiffs)
        {
            TrimTileCollection(renderedTileCount);
        }

        MapLayerStatus = missingCount == 0
            ? $"OSM tiles loaded: {loadedCount}/{requiredRequests.Count} at zoom {tileZoom}"
            : $"OSM tiles loaded: {loadedCount}/{requiredRequests.Count} at zoom {tileZoom} (fetching {missingCount})";
        ShowFallbackGrid = loadedCount == 0;
    }

    private void UpsertTile(int index, double x, double y, IImage image)
    {
        if (index < Tiles.Count)
        {
            if (EnableRenderItemReuse)
            {
                Tiles[index].Update(x, y, image);
            }
            else
            {
                Tiles[index] = new MapPreviewTileViewModel(x, y, image);
            }

            return;
        }

        var tile = EnableRenderItemReuse && pooledTiles.Count > 0
            ? pooledTiles.Pop()
            : new MapPreviewTileViewModel(x, y, image);
        tile.Update(x, y, image);
        Tiles.Add(tile);
    }

    private void UpsertBoundary(int index, AvaloniaList<Point> points, IBrush strokeBrush, string hoverDetails)
    {
        if (index < Boundaries.Count)
        {
            if (EnableRenderItemReuse)
            {
                Boundaries[index].Update(points, strokeBrush, hoverDetails);
            }
            else
            {
                Boundaries[index] = new MapBoundaryViewModel(points, strokeBrush, hoverDetails);
            }

            return;
        }

        var boundary = EnableRenderItemReuse && pooledBoundaries.Count > 0
            ? pooledBoundaries.Pop()
            : new MapBoundaryViewModel(points, strokeBrush, hoverDetails);
        boundary.Update(points, strokeBrush, hoverDetails);
        Boundaries.Add(boundary);
    }

    private void UpsertPoint(int index, double x, double y, IBrush markerBrush, string hoverDetails)
    {
        if (index < Points.Count)
        {
            if (EnableRenderItemReuse)
            {
                Points[index].Update(x, y, markerBrush, hoverDetails);
            }
            else
            {
                Points[index] = new MapPreviewPointViewModel(x, y, markerBrush, hoverDetails);
            }

            return;
        }

        var point = EnableRenderItemReuse && pooledPoints.Count > 0
            ? pooledPoints.Pop()
            : new MapPreviewPointViewModel(x, y, markerBrush, hoverDetails);
        point.Update(x, y, markerBrush, hoverDetails);
        Points.Add(point);
    }

    private void TrimTileCollection(int keepCount)
    {
        while (Tiles.Count > keepCount)
        {
            var item = Tiles[^1];
            Tiles.RemoveAt(Tiles.Count - 1);

            if (EnableRenderItemReuse)
            {
                pooledTiles.Push(item);
            }
        }

        if (!EnableRenderItemReuse)
        {
            pooledTiles.Clear();
        }
    }

    private void TrimBoundaryCollection(int keepCount)
    {
        while (Boundaries.Count > keepCount)
        {
            var item = Boundaries[^1];
            Boundaries.RemoveAt(Boundaries.Count - 1);

            if (EnableRenderItemReuse)
            {
                pooledBoundaries.Push(item);
            }
        }

        if (!EnableRenderItemReuse)
        {
            pooledBoundaries.Clear();
        }
    }

    private void TrimPointCollection(int keepCount)
    {
        while (Points.Count > keepCount)
        {
            var item = Points[^1];
            Points.RemoveAt(Points.Count - 1);

            if (EnableRenderItemReuse)
            {
                pooledPoints.Push(item);
            }
        }

        if (!EnableRenderItemReuse)
        {
            pooledPoints.Clear();
        }
    }

    private void ClearRenderedCollections()
    {
        ClearTileCollection();
        ClearBoundaryCollection();
        ClearPointCollection();
    }

    private void ClearTileCollection()
    {
        while (Tiles.Count > 0)
        {
            var item = Tiles[^1];
            Tiles.RemoveAt(Tiles.Count - 1);

            if (EnableRenderItemReuse)
            {
                pooledTiles.Push(item);
            }
        }

        if (!EnableRenderItemReuse)
        {
            pooledTiles.Clear();
        }
    }

    private void ClearBoundaryCollection()
    {
        while (Boundaries.Count > 0)
        {
            var item = Boundaries[^1];
            Boundaries.RemoveAt(Boundaries.Count - 1);

            if (EnableRenderItemReuse)
            {
                pooledBoundaries.Push(item);
            }
        }

        if (!EnableRenderItemReuse)
        {
            pooledBoundaries.Clear();
        }
    }

    private void ClearPointCollection()
    {
        while (Points.Count > 0)
        {
            var item = Points[^1];
            Points.RemoveAt(Points.Count - 1);

            if (EnableRenderItemReuse)
            {
                pooledPoints.Push(item);
            }
        }

        if (!EnableRenderItemReuse)
        {
            pooledPoints.Clear();
        }
    }

    private static string BuildRenderPerfSummary(
        double totalRenderMilliseconds,
        double tileProjectionMilliseconds,
        double boundaryProjectionMilliseconds,
        double pointProjectionMilliseconds,
        double? tileFetchMilliseconds,
        int renderedPointCount,
        int renderedBoundaryCount,
        int candidatePointCount,
        int candidateBoundaryCount,
        int totalPointCount,
        int totalBoundaryCount,
        bool interactionRenderMode)
    {
        var summary =
            $"render {totalRenderMilliseconds:0}ms (tiles {tileProjectionMilliseconds:0}, boundaries {boundaryProjectionMilliseconds:0}, points {pointProjectionMilliseconds:0}) • items points {renderedPointCount}/{candidatePointCount}/{totalPointCount}, boundaries {renderedBoundaryCount}/{candidateBoundaryCount}/{totalBoundaryCount}";

        if (tileFetchMilliseconds is > 0)
        {
            summary += $" • fetch {tileFetchMilliseconds.Value:0}ms";
        }

        if (interactionRenderMode)
        {
            summary += " • mode interaction";
        }

        return summary;
    }

    private int AdjustZoom(int delta, Point? viewportPoint = null)
    {
        var nextZoom = Math.Clamp(tileZoom + delta, MinZoom, MaxZoom);
        if (nextZoom == tileZoom)
        {
            return 0;
        }

        var previousZoom = tileZoom;
        var previousWorldSize = GetWorldSize(previousZoom);
        var nextWorldSize = GetWorldSize(nextZoom);
        var scaleFactor = nextWorldSize / previousWorldSize;

        if (viewportPoint is { } anchor)
        {
            var anchorX = Math.Clamp(anchor.X, 0, CanvasWidth);
            var anchorY = Math.Clamp(anchor.Y, 0, CanvasHeight);

            var previousTopLeftWorldX = centerWorldX - (CanvasWidth / 2.0);
            var previousTopLeftWorldY = centerWorldY - (CanvasHeight / 2.0);
            var anchorWorldX = Mod(previousTopLeftWorldX + anchorX, previousWorldSize);
            var anchorWorldY = Math.Clamp(previousTopLeftWorldY + anchorY, 0, previousWorldSize);

            var scaledAnchorWorldX = anchorWorldX * scaleFactor;
            var scaledAnchorWorldY = anchorWorldY * scaleFactor;

            centerWorldX = scaledAnchorWorldX - anchorX + (CanvasWidth / 2.0);
            centerWorldY = scaledAnchorWorldY - anchorY + (CanvasHeight / 2.0);
        }
        else
        {
            centerWorldX = Mod(centerWorldX, previousWorldSize) * scaleFactor;
            centerWorldY = Math.Clamp(centerWorldY, 0, previousWorldSize) * scaleFactor;
        }

        tileZoom = nextZoom;
        NormalizeCenterWorld();
        OnPropertyChanged(nameof(ZoomStatus));
        MarkInteractionActivity();
        RequestViewportRefresh();
        return nextZoom - previousZoom;
    }

    private void MarkInteractionActivity()
    {
        interactionLastActivityUtc = DateTime.UtcNow;
        interactionRefinementPending = true;
    }

    private bool IsInteractionRenderModeActive()
    {
        if (!interactionRefinementPending)
        {
            return false;
        }

        return (DateTime.UtcNow - interactionLastActivityUtc) < TimeSpan.FromMilliseconds(InteractionSettleMilliseconds);
    }

    private static int CountBoundaryPoints(IReadOnlyCollection<RawBoundary> boundaries)
    {
        var total = 0;

        foreach (var boundary in boundaries)
        {
            if (boundary.Vertices.Count == 0)
            {
                continue;
            }

            if (boundary.Vertices.Count > 1 && AreSameVertex(boundary.Vertices[0], boundary.Vertices[^1]))
            {
                total += boundary.Vertices.Count - 1;
                continue;
            }

            total += boundary.Vertices.Count;
        }

        return total;
    }

    private static bool AreSameVertex(RawBoundaryVertex left, RawBoundaryVertex right)
    {
        return Math.Abs(left.Latitude - right.Latitude) <= 1e-9 &&
               Math.Abs(left.Longitude - right.Longitude) <= 1e-9;
    }

    private void RequestViewportRefresh()
    {
        viewportRefreshRequested = true;

        if (!viewportRefreshTimer.IsEnabled)
        {
            viewportRefreshTimer.Start();
        }
    }

    private void OnViewportRefreshTimerTick(object? sender, EventArgs e)
    {
        if (!viewportRefreshRequested && !viewportRefreshPending)
        {
            viewportRefreshTimer.Stop();
            return;
        }

        if (viewportRefreshPending)
        {
            return;
        }

        viewportRefreshRequested = false;
        viewportRefreshPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            viewportRefreshPending = false;
            RefreshViewport();

            if (!viewportRefreshRequested)
            {
                viewportRefreshTimer.Stop();
            }
        }, DispatcherPriority.Render);
    }

    private void NormalizeCenterWorld()
    {
        var worldSize = GetWorldSize(tileZoom);
        centerWorldX = Mod(centerWorldX, worldSize);

        if (worldSize <= CanvasHeight)
        {
            centerWorldY = worldSize / 2.0;
            return;
        }

        var halfHeight = CanvasHeight / 2.0;
        centerWorldY = Math.Clamp(centerWorldY, halfHeight, worldSize - halfHeight);
    }

    private static List<RawBoundary> LoadBoundaryOverlays(string? boundaryCsvPath, HashSet<string> territoryIds, HashSet<string> territoryNumbers)
    {
        var selectedBoundaries = new List<RawBoundary>();

        if (string.IsNullOrWhiteSpace(boundaryCsvPath))
        {
            return selectedBoundaries;
        }

        var fullBoundaryPath = Path.GetFullPath(boundaryCsvPath);
        if (!File.Exists(fullBoundaryPath))
        {
            return selectedBoundaries;
        }

        var allBoundaries = GetBoundariesCached(fullBoundaryPath);
        if (allBoundaries.Count == 0)
        {
            return selectedBoundaries;
        }

        var normalizedTerritoryIds = territoryIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var normalizedTerritoryNumbers = territoryNumbers
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var matchByTerritoryId = normalizedTerritoryIds.Count > 0;
        var seenBoundaryKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var boundary in allBoundaries)
        {
            var boundaryId = boundary.TerritoryId?.Trim() ?? string.Empty;
            var boundaryTerritoryNumber = boundary.TerritoryNumber?.Trim() ?? string.Empty;
            var boundaryNumber = string.IsNullOrWhiteSpace(boundary.Suffix)
                ? boundary.Number
                : $"{boundary.Number}-{boundary.Suffix}";

            var matchesId = normalizedTerritoryIds.Contains(boundaryId);
            var matchesNumber = normalizedTerritoryNumbers.Contains(boundaryTerritoryNumber) ||
                                normalizedTerritoryNumbers.Contains(boundaryNumber);

            var isMatch = matchByTerritoryId ? matchesId : matchesNumber;

            if (!isMatch)
            {
                continue;
            }

            var boundaryKey = string.IsNullOrWhiteSpace(boundaryId)
                ? boundaryNumber
                : boundaryId;

            if (!seenBoundaryKeys.Add(boundaryKey))
            {
                continue;
            }

            var exteriorRing = boundary.Polygon.ExteriorRing;
            if (exteriorRing is null)
            {
                continue;
            }

            var vertices = exteriorRing.Coordinates
                .Select(coordinate => new RawBoundaryVertex(coordinate.Y, coordinate.X))
                .ToList();

            if (vertices.Count < 2)
            {
                continue;
            }

            var label = BuildBoundaryDisplayName(boundary);

            selectedBoundaries.Add(new RawBoundary(label, vertices));
        }

        return selectedBoundaries;
    }

    private static string BuildBoundaryDisplayName(TerritoryBoundary boundary)
    {
        var categoryCode = boundary.CategoryCode?.Trim() ?? string.Empty;
        var number = boundary.Number?.Trim() ?? string.Empty;
        var suffix = boundary.Suffix?.Trim() ?? string.Empty;

        var displayParts = new List<string>(capacity: 3);
        if (!string.IsNullOrWhiteSpace(categoryCode))
        {
            displayParts.Add(categoryCode);
        }

        if (!string.IsNullOrWhiteSpace(number))
        {
            displayParts.Add(number);
        }

        if (!string.IsNullOrWhiteSpace(suffix))
        {
            displayParts.Add(suffix);
        }

        if (displayParts.Count > 0)
        {
            return string.Join("-", displayParts);
        }

        var territoryNumber = boundary.TerritoryNumber?.Trim();
        if (!string.IsNullOrWhiteSpace(territoryNumber))
        {
            return territoryNumber;
        }

        var territoryId = boundary.TerritoryId?.Trim();
        return string.IsNullOrWhiteSpace(territoryId) ? "(unknown territory)" : territoryId;
    }

    private static IReadOnlyList<TerritoryBoundary> GetBoundariesCached(string boundaryCsvPath)
    {
        if (!File.Exists(boundaryCsvPath))
        {
            return Array.Empty<TerritoryBoundary>();
        }

        var cachePath = Path.GetFullPath(boundaryCsvPath);
        var lastWriteUtc = File.GetLastWriteTimeUtc(cachePath);

        if (BoundaryCache.TryGetValue(cachePath, out var cached) && cached.LastWriteUtc == lastWriteUtc)
        {
            return cached.Boundaries;
        }

        IReadOnlyList<TerritoryBoundary> boundaries;
        try
        {
            var reader = new TerritoryBoundaryReader();
            boundaries = reader.ReadAsync(cachePath).GetAwaiter().GetResult();
        }
        catch
        {
            boundaries = Array.Empty<TerritoryBoundary>();
        }

        BoundaryCache[cachePath] = new BoundaryCacheEntry(lastWriteUtc, boundaries);
        return boundaries;
    }

    private static MapSourceData ReadMapSourceData(string sourcePath, IReadOnlyCollection<string>? selectedTerritoryIds = null)
    {
        var points = new List<RawPoint>();
        var territoryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var territoryNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pointDedupKeys = EnableAddressPointDeduplication
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : null;
        var selectedTerritoryIdsSet = selectedTerritoryIds?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? [];

        var missingCoordinateCount = 0;
        var totalRowCount = 0;
        var doNotCallCount = 0;

        using var stream = File.OpenRead(sourcePath);
        using var reader = new StreamReader(stream);
        using var csv = CsvFactory.CreateReader(reader);

        if (!csv.Read() || !csv.ReadHeader())
        {
            return new MapSourceData(points, territoryIds, territoryNumbers, missingCoordinateCount, totalRowCount, doNotCallCount, 0);
        }

        var headers = csv.HeaderRecord ?? [];

        while (csv.Read())
        {
            var territoryId = GetField(csv, headers, "TerritoryID");
            var territoryNumber = GetField(csv, headers, "TerritoryNumber");
            var number = GetField(csv, headers, "Number");
            var street = GetField(csv, headers, "Street");
            var apartment = GetField(csv, headers, "ApartmentNumber");
            var suburb = GetField(csv, headers, "Suburb", "City");
            var state = GetField(csv, headers, "State");
            var postal = GetField(csv, headers, "PostalCode", "Zip", "ZipCode");
            var status = GetField(csv, headers, "Status");
            var notes = GetField(csv, headers, "Notes");

            if (selectedTerritoryIdsSet.Count > 0 &&
                !selectedTerritoryIdsSet.Contains(territoryId.Trim()))
            {
                continue;
            }

            totalRowCount++;

            if (!string.IsNullOrWhiteSpace(territoryId))
            {
                territoryIds.Add(territoryId);
            }

            if (!string.IsNullOrWhiteSpace(territoryNumber))
            {
                territoryNumbers.Add(territoryNumber);
            }

            var latitudeRaw = GetField(csv, headers, "Latitude", "Lat");
            var longitudeRaw = GetField(csv, headers, "Longitude", "Lon", "Lng");

            if (!double.TryParse(latitudeRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) ||
                !double.TryParse(longitudeRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude) ||
                latitude is < -90 or > 90 ||
                longitude is < -180 or > 180)
            {
                missingCoordinateCount++;
                continue;
            }

            if (pointDedupKeys is not null)
            {
                var dedupKey = BuildAddressPointDedupKey(territoryId, number, street);
                if (!string.IsNullOrWhiteSpace(dedupKey) && !pointDedupKeys.Add(dedupKey))
                {
                    continue;
                }
            }

            var isDoNotCall = status.Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Equals("DoNotCall", StringComparison.OrdinalIgnoreCase)
                || status.Equals("DNC", StringComparison.OrdinalIgnoreCase);

            if (isDoNotCall)
            {
                doNotCallCount++;
            }

            var unitPart = string.IsNullOrWhiteSpace(apartment) ? string.Empty : $", Unit {apartment}";
            var addressLine = $"{number} {street}{unitPart}".Trim();
            var locationLine = string.Join(", ",
                new[] { suburb, state, postal }.Where(value => !string.IsNullOrWhiteSpace(value)));

            var hoverDetails = string.Join("\n",
                new[]
                {
                    string.IsNullOrWhiteSpace(addressLine) ? "(Address unavailable)" : addressLine,
                    string.IsNullOrWhiteSpace(locationLine) ? "(Location unavailable)" : locationLine,
                    string.IsNullOrWhiteSpace(status) ? "Status: (none)" : $"Status: {status}",
                    string.IsNullOrWhiteSpace(territoryNumber)
                        ? $"Territory ID: {territoryId}"
                        : $"Territory: {territoryNumber} [ID:{territoryId}]",
                    string.IsNullOrWhiteSpace(notes) ? string.Empty : $"Notes: {notes}"
                }.Where(value => !string.IsNullOrWhiteSpace(value)));

            points.Add(new RawPoint(latitude, longitude, isDoNotCall, hoverDetails));
        }

        var uniqueCoordinateCount = points
            .Select(point => $"{point.Latitude:F6},{point.Longitude:F6}")
            .Distinct(StringComparer.Ordinal)
            .Count();

        var overlappingCoordinateCount = Math.Max(0, points.Count - uniqueCoordinateCount);

        return new MapSourceData(
            points,
            territoryIds,
            territoryNumbers,
            missingCoordinateCount,
            totalRowCount,
            doNotCallCount,
            overlappingCoordinateCount);
    }

    private static string BuildAddressPointDedupKey(string territoryId, string number, string street)
    {
        var normalizedTerritoryId = NormalizeDedupValue(territoryId);
        var normalizedNumber = NormalizeDedupValue(number);
        var normalizedStreet = NormalizeDedupValue(street);

        if (string.IsNullOrWhiteSpace(normalizedTerritoryId) ||
            string.IsNullOrWhiteSpace(normalizedNumber) ||
            string.IsNullOrWhiteSpace(normalizedStreet))
        {
            return string.Empty;
        }

        return $"{normalizedTerritoryId}|{normalizedNumber}|{normalizedStreet}";
    }

    private static string NormalizeDedupValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(' ', value
            .Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static List<TileRequest> BuildTileRequests(int zoom, double topLeftWorldX, double topLeftWorldY, int marginTiles)
    {
        var worldSize = GetWorldSize(zoom);

        var startTileX = (int)Math.Floor(topLeftWorldX / TileSize) - marginTiles;
        var endTileX = (int)Math.Floor((topLeftWorldX + CanvasWidth) / TileSize) + marginTiles;
        var startTileY = (int)Math.Floor(topLeftWorldY / TileSize) - marginTiles;
        var endTileY = (int)Math.Floor((topLeftWorldY + CanvasHeight) / TileSize) + marginTiles;

        var requestSeeds = GetTileRequestSeeds(zoom, startTileX, endTileX, startTileY, endTileY);

        var requests = new List<TileRequest>(requestSeeds.Count);
        foreach (var requestSeed in requestSeeds)
        {
            var screenX = requestSeed.TileX * TileSize - topLeftWorldX;
            var wrappedScreenX = NormalizeWrappedDelta(screenX, worldSize);
            var screenY = requestSeed.TileY * TileSize - topLeftWorldY;

            requests.Add(new TileRequest(
                requestSeed.CacheKey,
                requestSeed.Url,
                wrappedScreenX,
                screenY));
        }

        return requests;
    }

    private static IReadOnlyList<TileRequestSeed> GetTileRequestSeeds(int zoom, int startTileX, int endTileX, int startTileY, int endTileY)
    {
        var cacheKey = new TileWindowCacheKey(zoom, startTileX, endTileX, startTileY, endTileY);
        if (TileWindowRequestSeedCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        if (TileWindowRequestSeedCache.Count >= MaxTileWindowCacheEntries)
        {
            TileWindowRequestSeedCache.Clear();
        }

        var tileCountPerAxis = 1 << zoom;

        var requests = new List<TileRequestSeed>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var tileY = startTileY; tileY <= endTileY; tileY++)
        {
            if (tileY < 0 || tileY >= tileCountPerAxis)
            {
                continue;
            }

            for (var tileX = startTileX; tileX <= endTileX; tileX++)
            {
                var wrappedTileX = ((tileX % tileCountPerAxis) + tileCountPerAxis) % tileCountPerAxis;
                var requestCacheKey = $"{zoom}/{wrappedTileX}/{tileY}";

                if (!seenKeys.Add(requestCacheKey))
                {
                    continue;
                }

                requests.Add(new TileRequestSeed(
                    requestCacheKey,
                    $"https://tile.openstreetmap.org/{zoom}/{wrappedTileX}/{tileY}.png",
                    tileX,
                    tileY));
            }
        }

        TileWindowRequestSeedCache[cacheKey] = requests;
        return requests;
    }

    private static async Task EnsureTileCachedAsync(TileRequest request)
    {
        if (TileBitmapCache.ContainsKey(request.CacheKey))
        {
            return;
        }

        var persistedTile = await TryReadPersistentTileAsync(request.CacheKey).ConfigureAwait(false);
        if (persistedTile is not null)
        {
            TileBitmapCache[request.CacheKey] = persistedTile;
            TileFailureTimes.TryRemove(request.CacheKey, out _);
            return;
        }

        if (TileFailureTimes.TryGetValue(request.CacheKey, out var failedAt) &&
            (DateTimeOffset.UtcNow - failedAt) < TimeSpan.FromSeconds(20))
        {
            return;
        }

        var fetchTask = TileFetchTasks.GetOrAdd(request.CacheKey, _ => DownloadTileBytesAsync(request.Url));
        try
        {
            var tileBytes = await fetchTask.ConfigureAwait(false);
            var bitmap = tileBytes is null ? null : CreateBitmap(tileBytes);
            if (bitmap is not null)
            {
                TileBitmapCache[request.CacheKey] = bitmap;
                TileFailureTimes.TryRemove(request.CacheKey, out _);

                if (tileBytes is not null)
                {
                    await WritePersistentTileAsync(request.CacheKey, tileBytes).ConfigureAwait(false);
                }
            }
            else
            {
                TileFailureTimes[request.CacheKey] = DateTimeOffset.UtcNow;
            }
        }
        finally
        {
            TileFetchTasks.TryRemove(request.CacheKey, out _);
        }
    }

    private static async Task<byte[]?> DownloadTileBytesAsync(string url)
    {
        try
        {
            using var response = await TileHttpClient.GetAsync(url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap? CreateBitmap(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<Bitmap?> TryReadPersistentTileAsync(string cacheKey)
    {
        var cachePath = GetPersistentTilePath(cacheKey);

        try
        {
            if (!File.Exists(cachePath))
            {
                return null;
            }

            var cacheAge = DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(cachePath);
            if (cacheAge > persistentTileCacheLifetime)
            {
                TryDeletePersistentTile(cachePath);
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(cachePath).ConfigureAwait(false);
            return CreateBitmap(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static async Task WritePersistentTileAsync(string cacheKey, byte[] bytes)
    {
        var cachePath = GetPersistentTilePath(cacheKey);

        try
        {
            var directory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllBytesAsync(cachePath, bytes).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static string GetPersistentTilePath(string cacheKey)
    {
        var relativePath = cacheKey.Replace('/', Path.DirectorySeparatorChar) + ".png";
        return Path.Combine(PersistentTileCacheDirectory, relativePath);
    }

    private static void TryDeletePersistentTile(string cachePath)
    {
        try
        {
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }
        }
        catch
        {
        }
    }

    private static GeoBounds CalculateGeoBounds(IReadOnlyCollection<RawPoint> points)
    {
        var minLatitude = points.Min(point => point.Latitude);
        var maxLatitude = points.Max(point => point.Latitude);
        var minLongitude = points.Min(point => point.Longitude);
        var maxLongitude = points.Max(point => point.Longitude);

        return new GeoBounds(minLatitude, maxLatitude, minLongitude, maxLongitude);
    }

    private static int DetermineFitZoom(GeoBounds bounds)
    {
        for (var zoom = MaxZoom; zoom >= MinZoom; zoom--)
        {
            var (minX, minY) = ToWorldPixels(bounds.MinLatitude, bounds.MinLongitude, zoom);
            var (maxX, maxY) = ToWorldPixels(bounds.MaxLatitude, bounds.MaxLongitude, zoom);
            var spanX = Math.Abs(maxX - minX);
            var spanY = Math.Abs(maxY - minY);

            if (spanX <= CanvasWidth * 0.82 && spanY <= CanvasHeight * 0.82)
            {
                return zoom;
            }
        }

        return MinZoom;
    }

    private static (double X, double Y) ToWorldPixels(double latitude, double longitude, int zoom)
    {
        var clampedLatitude = Math.Clamp(latitude, -MaxMercatorLatitude, MaxMercatorLatitude);
        var normalizedLongitude = Math.Clamp(longitude, -180.0, 180.0);

        var worldSize = GetWorldSize(zoom);
        var x = (normalizedLongitude + 180.0) / 360.0 * worldSize;

        var latitudeRadians = clampedLatitude * Math.PI / 180.0;
        var mercator = Math.Log(Math.Tan((Math.PI / 4.0) + (latitudeRadians / 2.0)));
        var y = (1.0 - (mercator / Math.PI)) / 2.0 * worldSize;

        return (x, y);
    }

    private static (double Latitude, double Longitude) FromWorldPixels(double x, double y, int zoom)
    {
        var worldSize = GetWorldSize(zoom);
        var normalizedX = Mod(x, worldSize) / worldSize;
        var normalizedY = Math.Clamp(y / worldSize, 0.0, 1.0);

        var longitude = normalizedX * 360.0 - 180.0;

        var n = Math.PI - (2.0 * Math.PI * normalizedY);
        var latitude = 180.0 / Math.PI * Math.Atan(Math.Sinh(n));

        return (latitude, longitude);
    }

    private static double NormalizeWrappedDelta(double delta, double worldSize)
    {
        if (worldSize <= 0)
        {
            return delta;
        }

        if (delta < -worldSize / 2.0)
        {
            delta += worldSize;
        }
        else if (delta > worldSize / 2.0)
        {
            delta -= worldSize;
        }

        return delta;
    }

    private static double Mod(double value, double modulus)
    {
        if (modulus <= 0)
        {
            return value;
        }

        var result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    private static double GetWorldSize(int zoom) => TileSize * Math.Pow(2, zoom);

    private static HttpClient CreateTileClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(6)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("NWSHelper.Gui/1.0 (+https://github.com/dmealo/NWSHelper)");
        return client;
    }

    private static string GetField(CsvHelper.CsvReader csv, IReadOnlyList<string> headers, params string[] candidateHeaders)
    {
        foreach (var candidate in candidateHeaders)
        {
            var matched = headers.FirstOrDefault(header =>
                string.Equals(header, candidate, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(matched))
            {
                return csv.GetField(matched) ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private sealed record RawPoint(double Latitude, double Longitude, bool IsDoNotCall, string HoverDetails);

    private sealed record ProjectedPoint(double WorldX, double WorldY, bool IsDoNotCall, string HoverDetails);

    private sealed record RawBoundaryVertex(double Latitude, double Longitude);

    private sealed record RawBoundary(string Label, IReadOnlyList<RawBoundaryVertex> Vertices);

    private sealed record ProjectedBoundaryVertex(double WorldX, double WorldY);

    private sealed record ProjectedBoundary(string Label, IReadOnlyList<ProjectedBoundaryVertex> Vertices, ProjectedWorldBounds Bounds);

    private readonly record struct ProjectedWorldBounds(double MinX, double MaxX, double MinY, double MaxY);

    private readonly record struct SpatialCellKey(int X, int Y);

    private sealed record SpatialIndex<T>(IReadOnlyList<T> Items, Dictionary<SpatialCellKey, List<int>> Bins, double CellSize);

    private sealed record GeoBounds(double MinLatitude, double MaxLatitude, double MinLongitude, double MaxLongitude);

    private readonly record struct PreloadSeed(double CenterLatitude, double CenterLongitude);

    private sealed record TileRequest(string CacheKey, string Url, double ScreenX, double ScreenY);

    private sealed record TileRequestSeed(string CacheKey, string Url, int TileX, int TileY);

    private readonly record struct TileWindowCacheKey(int Zoom, int StartTileX, int EndTileX, int StartTileY, int EndTileY);

    private readonly record struct BoundaryCacheEntry(DateTime LastWriteUtc, IReadOnlyList<TerritoryBoundary> Boundaries);

    private sealed record MapSourceData(
        List<RawPoint> Points,
        HashSet<string> TerritoryIds,
        HashSet<string> TerritoryNumbers,
        int MissingCoordinateCount,
        int TotalRowCount,
        int DoNotCallCount,
        int OverlappingCoordinateCount);
}

public sealed class MapPreviewTileViewModel : ObservableObject
{
    private double x;
    private double y;
    private IImage image;

    public MapPreviewTileViewModel(double x, double y, IImage image)
    {
        this.x = x;
        this.y = y;
        this.image = image;
    }

    public double X
    {
        get => x;
        private set => SetProperty(ref x, value);
    }

    public double Y
    {
        get => y;
        private set => SetProperty(ref y, value);
    }

    public IImage Image
    {
        get => image;
        private set => SetProperty(ref image, value);
    }

    public void Update(double nextX, double nextY, IImage nextImage)
    {
        X = nextX;
        Y = nextY;
        Image = nextImage;
    }
}

public sealed class MapBoundaryViewModel : ObservableObject
{
    private readonly AvaloniaList<Point> points = [];
    private IBrush strokeBrush;
    private string hoverDetails;

    public MapBoundaryViewModel(AvaloniaList<Point> points, IBrush strokeBrush, string hoverDetails)
    {
        this.strokeBrush = strokeBrush;
        this.hoverDetails = hoverDetails;
        SyncPoints(points);
    }

    public AvaloniaList<Point> Points => points;

    public IBrush StrokeBrush
    {
        get => strokeBrush;
        private set => SetProperty(ref strokeBrush, value);
    }

    public string HoverDetails
    {
        get => hoverDetails;
        private set => SetProperty(ref hoverDetails, value);
    }

    public void Update(AvaloniaList<Point> nextPoints, IBrush nextStrokeBrush, string nextHoverDetails)
    {
        SyncPoints(nextPoints);
        StrokeBrush = nextStrokeBrush;
        HoverDetails = nextHoverDetails;
    }

    private void SyncPoints(IReadOnlyList<Point> source)
    {
        var index = 0;
        for (; index < source.Count; index++)
        {
            if (index < points.Count)
            {
                points[index] = source[index];
            }
            else
            {
                points.Add(source[index]);
            }
        }

        while (points.Count > source.Count)
        {
            points.RemoveAt(points.Count - 1);
        }
    }
}

public sealed class MapPreviewPointViewModel : ObservableObject
{
    private double x;
    private double y;
    private IBrush markerBrush;
    private string hoverDetails;

    public MapPreviewPointViewModel(double x, double y, IBrush markerBrush, string hoverDetails)
    {
        this.x = x;
        this.y = y;
        this.markerBrush = markerBrush;
        this.hoverDetails = hoverDetails;
    }

    public double X
    {
        get => x;
        private set => SetProperty(ref x, value);
    }

    public double Y
    {
        get => y;
        private set => SetProperty(ref y, value);
    }

    public IBrush MarkerBrush
    {
        get => markerBrush;
        private set => SetProperty(ref markerBrush, value);
    }

    public string HoverDetails
    {
        get => hoverDetails;
        private set => SetProperty(ref hoverDetails, value);
    }

    public void Update(double nextX, double nextY, IBrush nextMarkerBrush, string nextHoverDetails)
    {
        X = nextX;
        Y = nextY;
        MarkerBrush = nextMarkerBrush;
        HoverDetails = nextHoverDetails;
    }
}

