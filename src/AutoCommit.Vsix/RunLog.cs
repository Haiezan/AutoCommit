using System;
using System.Diagnostics;
using System.Collections.Generic;
using AutoCommit.Core;
using System.IO;

namespace AutoCommit.Vsix;

internal sealed class RunLog
{
    internal static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoCommit", "logs");
    private static readonly object Gate = new object();
    private readonly string id = Guid.NewGuid().ToString("N").Substring(0, 8);
    private readonly Stopwatch elapsed = Stopwatch.StartNew();

    private readonly Queue<string>? stages;
    private int omitted;
    internal RunLog(bool generation = false) { if (generation) stages = new Queue<string>(); }
    internal DateTime Started { get; } = DateTime.Now;
    internal string Context { get; set; } = "";
    internal int Requests;
    internal GenerationResult? Response { get; set; }
    internal bool Applied { get; set; }
    internal TimeSpan Duration => elapsed.Elapsed;
    internal void Finish() => elapsed.Stop();

    internal string StageText()
    {
        lock (Gate) return (omitted > 0 ? $"较早的 {omitted} 条记录见日志目录。\n" : "") +
            (stages == null ? "" : string.Join("\n", stages));
    }

    internal void Write(string message)
    {
        lock (Gate) {
            if (stages != null) {
                if (stages.Count == 200) { stages.Dequeue(); omitted++; }
                stages.Enqueue($"+{elapsed.Elapsed.TotalSeconds:F1}s  {message}");
            }
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
