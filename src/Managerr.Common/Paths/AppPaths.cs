namespace Managerr.Common.Paths;

public interface IAppPaths
{
    string ConfigDirectory { get; }
    string CoreDatabasePath { get; }
    string CacheDatabasePath { get; }
    string TelemetryDatabasePath { get; }
    string BackupsDirectory { get; }
    string MasterKeyFilePath { get; }
    string PlexClientIdFilePath { get; }
    string CustomDefinitionsDirectory { get; }
}

public sealed class AppPaths : IAppPaths
{
    public AppPaths(string? configDirectoryOverride = null)
    {
        var configured = configDirectoryOverride
            ?? Environment.GetEnvironmentVariable("MANAGERR_CONFIG_DIR");

        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = Directory.Exists("/config")
                ? "/config"
                : Path.Combine(Directory.GetCurrentDirectory(), "config");
        }

        ConfigDirectory = Path.GetFullPath(configured);
        BackupsDirectory = Path.Combine(ConfigDirectory, "backups");
        CustomDefinitionsDirectory = Path.Combine(ConfigDirectory, "Definitions", "Custom");
        CoreDatabasePath = Path.Combine(ConfigDirectory, "managerr.db");
        CacheDatabasePath = Path.Combine(ConfigDirectory, "cache.db");
        TelemetryDatabasePath = Path.Combine(ConfigDirectory, "telemetry.db");
        MasterKeyFilePath = Path.Combine(ConfigDirectory, "master.key");
        PlexClientIdFilePath = Path.Combine(ConfigDirectory, "plex_client_id");

        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(BackupsDirectory);
        Directory.CreateDirectory(CustomDefinitionsDirectory);
    }

    public string ConfigDirectory { get; }
    public string CoreDatabasePath { get; }
    public string CacheDatabasePath { get; }
    public string TelemetryDatabasePath { get; }
    public string BackupsDirectory { get; }
    public string MasterKeyFilePath { get; }
    public string PlexClientIdFilePath { get; }
    public string CustomDefinitionsDirectory { get; }
}
