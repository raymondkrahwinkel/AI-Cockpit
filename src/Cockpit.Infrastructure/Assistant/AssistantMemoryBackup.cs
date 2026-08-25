using System.IO.Compression;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Assistant;

// A loose, light backup/restore for just the two assistant memory files (AC-657), separate from a full cockpit
// backup (`BackupService`). No manifest, no secrets scrubbing, no plugin selection: an operator carrying just
// the assistant's memory to another machine does not need the rest of that flow.
internal static class AssistantMemoryBackup
{
    // Writes whichever of the two files exist to a .zip at `archivePath`, overwriting whatever was there.
    public static IReadOnlyList<string> Write(string archivePath, string memoryPath, string statePath)
    {
        var files = new[] { memoryPath, statePath }.Where(File.Exists).ToList();
        if (files.Count == 0)
        {
            throw new InvalidOperationException("There is nothing to export: the assistant has not remembered anything yet.");
        }

        if (File.Exists(archivePath))
        {
            File.Delete(archivePath);
        }

        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            foreach (var file in files)
            {
                archive.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.Optimal);
            }
        }

        return files.Select(Path.GetFileName).ToList()!;
    }

    // Puts back whichever of the two files the archive carries. What is being replaced is copied aside with a
    // timestamp first, never deleted — the same "goes aside, not away" principle `BackupService._RestoreLooseFiles`
    // uses for the cockpit's own loose files.
    public static IReadOnlyList<string> Restore(string archivePath, string memoryPath, string statePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);

        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.GetFileName(memoryPath)] = memoryPath,
            [Path.GetFileName(statePath)] = statePath,
        };

        var restored = new List<string>();

        foreach (var entry in archive.Entries)
        {
            if (!targets.TryGetValue(entry.Name, out var target))
            {
                continue;
            }

            CockpitConfigPath.EnsurePrivateDirectory(Path.GetDirectoryName(target)!);

            if (File.Exists(target))
            {
                File.Copy(target, $"{target}.replaced-{DateTimeOffset.Now:yyyyMMdd-HHmmss}", overwrite: true);
            }

            entry.ExtractToFile(target, overwrite: true);
            restored.Add(entry.Name);
        }

        if (restored.Count == 0)
        {
            throw new InvalidOperationException(
                $"This archive carries neither {Path.GetFileName(memoryPath)} nor {Path.GetFileName(statePath)}, so nothing was restored.");
        }

        return restored;
    }
}
