using System.Text.Json;

namespace OrthoStreamXP.Setup;

/// <summary>Keeps the Build Scenery page's choices in <c>%LOCALAPPDATA%\OrthoStreamXP\scenery-build.json</c>.</summary>
internal sealed class SceneryBuildPreferencesStore(string path)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultPath => Path.Join(AppPaths.Root, "scenery-build.json");

    /// <summary>The saved choices; the defaults when there are none or they cannot be read.</summary>
    public SceneryBuildPreferences Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<SceneryBuildPreferences>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warning($"The Build Scenery choices could not be read, the defaults are used: {ex.Message}");
            return new();
        }
    }

    public void Save(SceneryBuildPreferences preferences)
    {
        try
        {
            AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(preferences, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"The Build Scenery choices could not be saved: {ex.Message}");
        }
    }
}
