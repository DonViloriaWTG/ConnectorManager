using Newtonsoft.Json;

namespace ConnectorManager.Services;

/// <summary>
/// Persists and loads workspace settings (repo paths, auth config, etc.) to/from a JSON file.
/// Settings are stored in the user's local app data directory.
/// </summary>
public sealed class SettingsPersistenceService
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ConnectorManager");

    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

    public Models.WorkspaceSettings Load()
    {
        if (!File.Exists(SettingsFile))
        {
            return AutoDetectSettings();
        }

        try
        {
            var json = File.ReadAllText(SettingsFile);
            return JsonConvert.DeserializeObject<Models.WorkspaceSettings>(json) ?? AutoDetectSettings();
        }
        catch
        {
            return AutoDetectSettings();
        }
    }

    public void Save(Models.WorkspaceSettings settings)
    {
        Directory.CreateDirectory(SettingsDir);
        var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
        File.WriteAllText(SettingsFile, json);
    }

    private static Models.WorkspaceSettings AutoDetectSettings()
    {
        // Walk up from the executable location looking for a parent directory
        // that contains CMB.* repo folders (e.g. CMB.Core, CMB.Common, etc.).
        // This works regardless of where the solution lives on disk.
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var settings = Models.WorkspaceSettings.AutoDetect(dir);
            if (settings.IsValid)
            {
                return settings;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return new Models.WorkspaceSettings();
    }
}
