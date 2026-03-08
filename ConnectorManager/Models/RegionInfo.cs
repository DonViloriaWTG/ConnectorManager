namespace ConnectorManager.Models;

/// <summary>
/// Represents a region with its flag image and connector count
/// for the region navigation view.
/// </summary>
public sealed class RegionInfo
{
    public string Name { get; init; } = string.Empty;
    public string Code { get; init; } = "??";
    public string FlagPath { get; init; } = string.Empty;
    public bool IsGlobe { get; init; }
    public int ConnectorCount { get; init; }

    /// <summary>
    /// Maps a region folder name to a short country/region code.
    /// </summary>
    public static string GetCode(string region) => region switch
    {
        "Australia" => "au",
        "Canada" => "ca",
        "Finland" => "fi",
        "Germany" => "de",
        "Ireland" => "ie",
        "Netherlands" => "nl",
        "NewZealand" => "nz",
        "NorthAmerica" => "us",
        "SouthAfrica" => "za",
        "UK" or "UnitedKingdom" => "gb",
        "Global" => "global",
        "International" => "international",
        _ => "global"
    };

    /// <summary>
    /// Returns the pack URI for the flag image resource, or empty for globe regions.
    /// </summary>
    public static string GetFlagPath(string region)
    {
        var code = GetCode(region);
        if (code is "global" or "international")
            return string.Empty;
        return $"/Assets/Flags/{code}.png";
    }

    /// <summary>
    /// Returns true for regions that use the globe icon instead of a flag image.
    /// </summary>
    public static bool IsGlobeRegion(string region) =>
        region is "Global" or "International";
}
