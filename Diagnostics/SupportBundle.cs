using System.IO;
using System.IO.Compression;
using System.Text;

namespace R2PrismRuntime.Diagnostics;

/// <summary>
/// Packs everything a tester should send back into one zip in the logs folder: a summary of the
/// current state, every session log and diagnostic report, and the settings file.
/// </summary>
public static class SupportBundle
{
    public static string Create(string summary)
    {
        Directory.CreateDirectory(Log.LogDirectory);
        string path = Path.Combine(Log.LogDirectory, $"Prismforge_Support_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
        int files = 0;
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("summary.txt").Open(), Encoding.UTF8))
                w.Write(summary);

            var logs = new DirectoryInfo(Log.LogDirectory).GetFiles()
                .Where(f => f.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase)
                         || (f.Name.StartsWith("Prismforge_Diag_", StringComparison.OrdinalIgnoreCase) && f.Extension == ".txt"))
                .OrderByDescending(f => f.LastWriteTimeUtc);
            foreach (var f in logs)
                if (Add(zip, f.FullName, f.Name)) files++;
            if (Add(zip, Path.Combine(AppInfo.DataDirectory, "settings.json"), "settings.json")) files++;
        }
        Log.Info($"Support zip written: {Path.GetFileName(path)} ({files} file(s) plus summary).");
        return path;
    }

    /// The current session log is still open for writing, so it is read with a shared handle.
    private static bool Add(ZipArchive zip, string source, string name)
    {
        try
        {
            if (!File.Exists(source)) return false;
            using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var dst = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
            src.CopyTo(dst);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Support zip: skipped {name}: {ex.Message}");
            return false;
        }
    }
}
