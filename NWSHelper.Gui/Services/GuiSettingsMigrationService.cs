using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NWSHelper.Gui.Services;

public sealed class GuiSettingsMigrationBackupDocument
{
    public int SchemaVersion { get; set; } = 1;

    public string BackupKind { get; set; } = "portable-settings";

    public DateTimeOffset ExportedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public GuiConfigurationDocument Configuration { get; set; } = new();
}

public sealed class GuiSettingsMigrationResult
{
    public bool IsSuccess { get; init; }

    public string Message { get; init; } = string.Empty;

    public GuiConfigurationDocument? Configuration { get; init; }
}

public interface IGuiSettingsMigrationService
{
    Task<GuiSettingsMigrationResult> ExportAsync(string path, CancellationToken cancellationToken);

    Task<GuiSettingsMigrationResult> ImportAsync(string path, CancellationToken cancellationToken);
}

public sealed class GuiSettingsMigrationService : IGuiSettingsMigrationService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly GuiConfigurationStore configurationStore;

    public GuiSettingsMigrationService(string? filePath = null)
    {
        configurationStore = new GuiConfigurationStore(filePath);
    }

    public Task<GuiSettingsMigrationResult> ExportAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult(CreateFailureResult("Choose a file path for the migration backup."));
        }

        try
        {
            var exportDirectory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(exportDirectory))
            {
                Directory.CreateDirectory(exportDirectory);
            }

            var backupDocument = new GuiSettingsMigrationBackupDocument
            {
                ExportedAtUtc = DateTimeOffset.UtcNow,
                Configuration = CreatePortableConfiguration(configurationStore.Load())
            };

            WriteJsonAtomically(path, backupDocument);

            return Task.FromResult(new GuiSettingsMigrationResult
            {
                IsSuccess = true,
                Message = "Migration backup exported. Activation keys and entitlement tokens were excluded.",
                Configuration = backupDocument.Configuration
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(CreateFailureResult($"Could not export migration backup: {ex.Message}"));
        }
    }

    public Task<GuiSettingsMigrationResult> ImportAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult(CreateFailureResult("Choose a migration backup file to import."));
        }

        if (!File.Exists(path))
        {
            return Task.FromResult(CreateFailureResult("The selected migration backup file was not found."));
        }

        try
        {
            var importedConfiguration = LoadPortableConfiguration(path);
            if (importedConfiguration is null)
            {
                return Task.FromResult(CreateFailureResult("The selected file is not a valid NWS Helper migration backup."));
            }

            var current = configurationStore.Load();
            current.Theme = importedConfiguration.Theme;

            if (importedConfiguration.Setup is not null)
            {
                current.Setup = importedConfiguration.Setup;
            }

            if (importedConfiguration.Updates is not null)
            {
                current.Updates = importedConfiguration.Updates;
            }

            if (importedConfiguration.Entitlement?.AccountLink is not null)
            {
                current.Entitlement ??= new GuiEntitlementSettings();
                current.Entitlement.AccountLink = importedConfiguration.Entitlement.AccountLink;
            }

            configurationStore.Save(current);

            return Task.FromResult(new GuiSettingsMigrationResult
            {
                IsSuccess = true,
                Message = "Migration backup imported. Refresh account link status after signing in with the same email to rehydrate Store entitlement on this install.",
                Configuration = importedConfiguration
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(CreateFailureResult($"Could not import migration backup: {ex.Message}"));
        }
    }

    private static GuiConfigurationDocument? LoadPortableConfiguration(string path)
    {
        var json = File.ReadAllText(path);

        var wrappedBackup = JsonSerializer.Deserialize<GuiSettingsMigrationBackupDocument>(json);
        if (wrappedBackup?.Configuration is not null &&
            string.Equals(wrappedBackup.BackupKind, "portable-settings", StringComparison.OrdinalIgnoreCase))
        {
            return CreatePortableConfiguration(wrappedBackup.Configuration);
        }

        var rawConfiguration = JsonSerializer.Deserialize<GuiConfigurationDocument>(json);
        return rawConfiguration is null
            ? null
            : CreatePortableConfiguration(rawConfiguration);
    }

    private static GuiConfigurationDocument CreatePortableConfiguration(GuiConfigurationDocument configuration)
    {
        return new GuiConfigurationDocument
        {
            Theme = configuration.Theme,
            Setup = configuration.Setup,
            Updates = configuration.Updates,
            Entitlement = configuration.Entitlement is null
                ? null
                : new GuiEntitlementSettings
                {
                    AccountLink = configuration.Entitlement.AccountLink ?? new GuiAccountLinkSettings()
                }
        };
    }

    private static void WriteJsonAtomically(string path, object value)
    {
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(value, SerializerOptions));
        File.Move(tempPath, path, overwrite: true);
    }

    private static GuiSettingsMigrationResult CreateFailureResult(string message)
    {
        return new GuiSettingsMigrationResult
        {
            IsSuccess = false,
            Message = message
        };
    }
}