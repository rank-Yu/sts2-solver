using System.IO.Compression;

namespace Sts2Solver;

internal sealed record DiagnosticLogExport(string? ZipPath, string? Error);

/// <summary>Packages existing diagnostic files; called on the combat log writer after prior writes.</summary>
internal static class DiagnosticLogExporter
{
    internal static DiagnosticLogExport Write(string combatRoot, string settingsPath, string logsRoot,
        string exportRoot)
    {
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(exportRoot);
            string zip = Path.Combine(exportRoot, $"sts2solver-logs-{DateTime.Now:yyyyMMdd-HHmmss-fffffff}.zip");
            temporary = zip + ".tmp";
            using (FileStream file = new(temporary, FileMode.CreateNew, System.IO.FileAccess.Write, FileShare.None))
            using (ZipArchive archive = new(file, ZipArchiveMode.Create))
            {
                AddDirectory(archive, combatRoot, "sts2solver/combat-logs");
                if (File.Exists(settingsPath))
                    AddFile(archive, settingsPath, "sts2solver/settings.json");
                AddDirectory(archive, logsRoot, "logs");
            }
            File.Move(temporary, zip);
            return new(zip, null);
        }
        catch (IOException error)
        {
            return new(null, error.Message);
        }
        catch (UnauthorizedAccessException error)
        {
            return new(null, error.Message);
        }
        finally
        {
            if (temporary != null && File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static void AddDirectory(ZipArchive archive, string root, string prefix)
    {
        if (!Directory.Exists(root))
            return;
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            AddFile(archive, path, prefix + "/" + relative);
        }
    }

    private static void AddFile(ZipArchive archive, string path, string name)
    {
        // Game logs remain open and may grow while exporting. Copy only the length seen at open.
        using FileStream source = new(path, FileMode.Open, System.IO.FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        long remaining = source.Length;
        using Stream destination = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
        byte[] buffer = new byte[81920];
        while (remaining > 0)
        {
            int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0)
                break;
            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }
}
