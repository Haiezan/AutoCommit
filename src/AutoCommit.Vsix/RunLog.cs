using System;
using System.Diagnostics;
using System.IO;

namespace AutoCommit.Vsix;

internal sealed class RunLog
{
    internal static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoCommit", "logs");
    private static readonly object Gate = new object();
    private readonly string id = Guid.NewGuid().ToString("N").Substring(0, 8);
    private readonly Stopwatch elapsed = Stopwatch.StartNew();

    internal void Write(string message)
    {
        lock (Gate) {
            try {
                Directory.CreateDirectory(DirectoryPath);
                var path = Path.Combine(DirectoryPath, "generation.log");
                if (File.Exists(path) && new FileInfo(path).Length >= 1024 * 1024) {
                    File.Delete(path + ".1");
                    File.Move(path, path + ".1");
                }
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} [{id}] +{elapsed.ElapsedMilliseconds} ms {message}{Environment.NewLine}");
            } catch (IOException) {
                // Logging is best effort, including when another IDE instance rotates the file.
            } catch (UnauthorizedAccessException) { }
        }
    }
}
