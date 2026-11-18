using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using DiffPlex;
using DiffPlex.Chunkers;
using DiffPlex.Model;

namespace AutoCommit.Core;

public sealed class TfvcFileChange
{
    public string Metadata { get; }
    public string PatchHeader { get; }
    public IReadOnlyList<string> Hunks { get; }
    public bool HasOmittedHunks { get; }

    public TfvcFileChange(string metadata, string patchHeader = "", IReadOnlyList<string>? hunks = null, bool hasOmittedHunks = false)
    {
        Metadata = metadata;
        PatchHeader = patchHeader;
        Hunks = hunks ?? Array.Empty<string>();
        HasOmittedHunks = hasOmittedHunks;
    }

    public void WriteTo(TextWriter writer)
    {
        var coverage = Hunks.Count == 0 ? "仅文件信息" : HasOmittedHunks ? "部分差异" : "完整差异";
        writer.Write(Metadata + "；模型输入：" + coverage + "\n");
        if (Hunks.Count > 0) {
            writer.Write(PatchHeader);
            foreach (var hunk in Hunks) writer.Write(hunk);
        }
        writer.Write('\n');
    }
}

public sealed class TfvcContent
{
    private const int MaxTextBytes = 1024 * 1024;
    private const int ContextLines = 3;
    private const int MaxComparedLines = 12_000;
    private const string NoNewline = "\n\\ No newline at end of file\n";

    public string Fingerprint { get; }
    public long Size { get; }
    private string? Text { get; }
    private string? UnavailableReason { get; }

    private TfvcContent(string fingerprint, long size, string? text = null, string? unavailableReason = null)
    {
        Fingerprint = fingerprint;
        Size = size;
        Text = text;
        UnavailableReason = unavailableReason;
    }

    public static TfvcContent ReadFile(string path, bool includeText)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var length = stream.Length;
        if (!includeText || length > MaxTextBytes)
            return new TfvcContent(Convert.ToBase64String(sha.ComputeHash(stream)), length,
                unavailableReason: length > MaxTextBytes ? "文件超过 1 MiB" : null);

        using var reader = new BinaryReader(stream);
        var bytes = reader.ReadBytes((int)length);
        var fingerprint = Convert.ToBase64String(sha.ComputeHash(bytes));
        return Decode(bytes, fingerprint);
    }

    private static TfvcContent Decode(byte[] bytes, string fingerprint)
    {
        try {
            string text;
            if (bytes.Length >= 2 && bytes[0] == 255 && bytes[1] == 254)
                text = new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2);
            else if (bytes.Length >= 2 && bytes[0] == 254 && bytes[1] == 255)
                text = new UnicodeEncoding(true, true, true).GetString(bytes, 2, bytes.Length - 2);
            else {
                var offset = bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191 ? 3 : 0;
                text = new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
            }
            if (!text.Contains("\0"))
                return new TfvcContent(fingerprint, bytes.Length, text.Replace("\r\n", "\n").Replace('\r', '\n'));
        } catch (DecoderFallbackException) {
            // Unsupported encodings and binary content are described by metadata.
        }
        return new TfvcContent(fingerprint, bytes.Length, unavailableReason: "二进制或不支持的文本编码");
    }

    public static TfvcFileChange CreateChange(string path, string operation, string? sourcePath,
        TfvcContent? before, TfvcContent? after, int maxDiffCharacters, CancellationToken token)
    {
        var metadata = $"文件：{path}；操作：{operation}";
        if (!string.IsNullOrEmpty(sourcePath) && !string.Equals(path, sourcePath, StringComparison.Ordinal))
            metadata += $"；原路径：{sourcePath}";
        if (before != null) metadata += $"；修改前 {before.Size} 字节";
        if (after != null) metadata += $"；修改后 {after.Size} 字节";

        var reasons = new[] { before?.UnavailableReason, after?.UnavailableReason }.Where(reason => reason != null).Distinct();
        var unavailable = string.Join("、", reasons);
        if (unavailable.Length > 0) return new TfvcFileChange(metadata + "；" + unavailable);

        var oldText = before?.Text ?? "";
        var newText = after?.Text ?? "";
        if (oldText == newText) {
            if (before != null && after != null)
                metadata += before.Fingerprint == after.Fingerprint ? "；文件文本相同" : "；文本相同，文件字节存在差异";
            return new TfvcFileChange(metadata);
        }

        token.ThrowIfCancellationRequested();
        var oldLines = LineEndingsPreservingChunker.Instance.Chunk(oldText);
        var newLines = LineEndingsPreservingChunker.Instance.Chunk(newText);
        var prefix = 0;
        while (prefix < oldLines.Count && prefix < newLines.Count && oldLines[prefix] == newLines[prefix]) prefix++;
        var oldEnd = oldLines.Count;
        var newEnd = newLines.Count;
        while (oldEnd > prefix && newEnd > prefix && oldLines[oldEnd - 1] == newLines[newEnd - 1]) { oldEnd--; newEnd--; }
        var oldCount = oldEnd - prefix;
        var newCount = newEnd - prefix;
        if (oldCount > 0 && newCount > 0 && oldCount + newCount > MaxComparedLines)
            return new TfvcFileChange(metadata + "；差异计算规模过大");

        IList<DiffBlock> blocks = oldCount == 0 || newCount == 0
            ? new[] { new DiffBlock(0, oldCount, 0, newCount) }
            : Differ.Instance.CreateDiffs(string.Concat(oldLines.Skip(prefix).Take(oldCount)),
                string.Concat(newLines.Skip(prefix).Take(newCount)), false, false, LineEndingsPreservingChunker.Instance).DiffBlocks;

        var originalPath = string.IsNullOrEmpty(sourcePath) ? path : sourcePath;
        var header = $"--- {(before == null ? "/dev/null" : originalPath)}\n+++ {(after == null ? "/dev/null" : path)}\n";
        var hunks = new List<string>();
        var remaining = maxDiffCharacters - header.Length;
        var omitted = false;
        for (var start = 0; start < blocks.Count;) {
            token.ThrowIfCancellationRequested();
            var end = start + 1;
            while (end < blocks.Count && blocks[end].DeleteStartA -
                (blocks[end - 1].DeleteStartA + blocks[end - 1].DeleteCountA) <= 2 * ContextLines) end++;
            var hunk = FormatHunk(oldLines, newLines, blocks, start, end, prefix, remaining);
            if (hunk == null) omitted = true;
            else { hunks.Add(hunk); remaining -= hunk.Length; }
            start = end;
        }

        if (omitted) metadata += "；有差异块超过读取预算";
        return new TfvcFileChange(metadata, header, hunks, omitted);
    }

    private static string? FormatHunk(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines, IList<DiffBlock> blocks,
        int start, int end, int offset, int budget)
    {
        var first = blocks[start];
        var last = blocks[end - 1];
        var lead = Math.Min(ContextLines, Math.Min(first.DeleteStartA + offset, first.InsertStartB + offset));
        var oldStart = first.DeleteStartA + offset - lead;
        var newStart = first.InsertStartB + offset - lead;
        var oldEnd = last.DeleteStartA + last.DeleteCountA + offset;
        var newEnd = last.InsertStartB + last.InsertCountB + offset;
        var tail = Math.Min(ContextLines, Math.Min(oldLines.Count - oldEnd, newLines.Count - newEnd));
        var oldLength = oldEnd + tail - oldStart;
        var newLength = newEnd + tail - newStart;
        var builder = new StringBuilder($"@@ -{(oldLength == 0 ? oldStart : oldStart + 1)},{oldLength} +{(newLength == 0 ? newStart : newStart + 1)},{newLength} @@\n");
        if (builder.Length > budget) return null;
        var oldIndex = oldStart;
        for (var i = start; i < end; i++) {
            var block = blocks[i];
            var deleteStart = block.DeleteStartA + offset;
            var insertStart = block.InsertStartB + offset;
            if (!AppendLines(builder, oldLines, oldIndex, deleteStart, ' ', budget) ||
                !AppendLines(builder, oldLines, deleteStart, deleteStart + block.DeleteCountA, '-', budget) ||
                !AppendLines(builder, newLines, insertStart, insertStart + block.InsertCountB, '+', budget)) return null;
            oldIndex = deleteStart + block.DeleteCountA;
        }
        return AppendLines(builder, oldLines, oldIndex, oldEnd + tail, ' ', budget) ? builder.ToString() : null;
    }

    private static bool AppendLines(StringBuilder builder, IReadOnlyList<string> lines, int start, int end, char prefix, int budget)
    {
        for (var i = start; i < end; i++) {
            var line = lines[i];
            var hasNewline = line.EndsWith("\n", StringComparison.Ordinal);
            if (builder.Length + 1 + line.Length + (hasNewline ? 0 : NoNewline.Length) > budget) return false;
            builder.Append(prefix).Append(line);
            if (!hasNewline) builder.Append(NoNewline);
        }
        return true;
    }
}
