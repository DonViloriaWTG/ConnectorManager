using System.IO.Compression;

namespace ConnectorManager.Services;

/// <summary>
/// Creates ZIP packages from connector publish output, matching the format
/// expected by the CMB API's package upload endpoint.
/// </summary>
public sealed class ConnectorPackageService
{
    /// <summary>
    /// Creates a ZIP package from the publish directory.
    /// ZIP entry names follow the format: "{connectorName}-{version}/{relativePath}".
    /// Blacklisted DLLs (provided by the host at runtime) are excluded.
    /// </summary>
    public string CreatePackage(
        string publishDirectory,
        string connectorName,
        string version,
        Action<string> onOutput)
    {
        var zipPath = Path.Combine(
            Path.GetDirectoryName(publishDirectory)!,
            $"{connectorName}-{version}.zip");

        onOutput($"  Creating package: {zipPath}");

        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        using var zipStream = new FileStream(zipPath, FileMode.Create);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

        var files = Directory.GetFiles(publishDirectory, "*", SearchOption.AllDirectories);
        var includedCount = 0;
        var excludedCount = 0;

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            if (ConnectorBuildService.BlacklistedDlls.Contains(fileName))
            {
                onOutput($"    Excluded (blacklisted): {fileName}");
                excludedCount++;
                continue;
            }

            var relativePath = Path.GetRelativePath(publishDirectory, file);
            var entryName = $"{connectorName}-{version}/{relativePath}".Replace('\\', '/');

            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            using var fileStream = File.OpenRead(file);
            fileStream.CopyTo(entryStream);
            includedCount++;
        }

        onOutput($"  ✔ Package created: {includedCount} files included, {excludedCount} excluded");
        return zipPath;
    }
}
