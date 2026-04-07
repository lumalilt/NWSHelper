using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using NWSHelper.Gui.ViewModels;
using NWSHelper.Gui.Views;

namespace NWSHelper.Gui.Services;

public interface IOutputPathActions
{
    Task<bool> OpenPathAsync(string path);

    Task<bool> OpenUrlAsync(string url);

    Task<bool> CopyPathAsync(string path);

    Task<bool> PreviewMapAsync(
        string path,
        string? boundaryCsvPath,
        string? previewContextLabel = null,
        IReadOnlyCollection<string>? selectedTerritoryIds = null);

    Task PreloadMapTilesAsync(IReadOnlyCollection<string> outputPaths, string? boundaryCsvPath);
}

public sealed class OutputPathActions : IOutputPathActions
{
    public Task<bool> OpenPathAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult(false);
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var target = fullPath;

            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                var parentDirectory = Path.GetDirectoryName(fullPath);
                if (string.IsNullOrWhiteSpace(parentDirectory) || !Directory.Exists(parentDirectory))
                {
                    return Task.FromResult(false);
                }

                target = parentDirectory;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });

            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task<bool> OpenUrlAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return Task.FromResult(false);
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });

            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public async Task<bool> CopyPathAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            ProcessStartInfo? startInfo;
            if (OperatingSystem.IsWindows())
            {
                startInfo = new ProcessStartInfo
                {
                    FileName = "clip.exe",
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }
            else if (OperatingSystem.IsMacOS())
            {
                startInfo = new ProcessStartInfo
                {
                    FileName = "pbcopy",
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }
            else if (OperatingSystem.IsLinux())
            {
                startInfo = new ProcessStartInfo
                {
                    FileName = "xclip",
                    Arguments = "-selection clipboard",
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }
            else
            {
                return false;
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            await process.StandardInput.WriteAsync(path);
            process.StandardInput.Close();
            await process.WaitForExitAsync();

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public Task<bool> PreviewMapAsync(
        string path,
        string? boundaryCsvPath,
        string? previewContextLabel = null,
        IReadOnlyCollection<string>? selectedTerritoryIds = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult(false);
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                return Task.FromResult(false);
            }

            var fullBoundaryPath = string.IsNullOrWhiteSpace(boundaryCsvPath)
                ? null
                : Path.GetFullPath(boundaryCsvPath);

            var viewModel = new OutputMapPreviewViewModel(fullPath, fullBoundaryPath, previewContextLabel, selectedTerritoryIds);
            var window = new OutputMapPreviewWindow
            {
                DataContext = viewModel
            };

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow is not null)
            {
                window.Show(desktop.MainWindow);
            }
            else
            {
                window.Show();
            }

            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public async Task PreloadMapTilesAsync(IReadOnlyCollection<string> outputPaths, string? boundaryCsvPath)
    {
        if (outputPaths.Count == 0)
        {
            return;
        }

        try
        {
            var fullPaths = outputPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (fullPaths.Length == 0)
            {
                return;
            }

            var fullBoundaryPath = string.IsNullOrWhiteSpace(boundaryCsvPath)
                ? null
                : Path.GetFullPath(boundaryCsvPath);

            await Task.Run(() => OutputMapPreviewViewModel.PreloadForOutputsAsync(fullPaths, fullBoundaryPath));
        }
        catch
        {
        }
    }
}
