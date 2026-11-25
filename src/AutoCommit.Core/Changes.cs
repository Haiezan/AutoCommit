using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AutoCommit.Core;

public sealed class ChangeFile
{
    public string Path { get; }
    public string Operation { get; }
    public ChangeFile(string path, string operation = "") { Path = path; Operation = operation; }
}

public sealed class ChangeSnapshot
{
    public string Root { get; }
    public string Diff { get; }
    public string Fingerprint { get; }
    public string Context { get; }
    public string Branch { get; }
    public string Statistics { get; }
    public IReadOnlyList<ChangeFile> Files { get; }
    public IReadOnlyList<TfvcFileChange>? TfvcChanges { get; }
    public bool DiffIsPartial { get; }
    internal string StageableStatus { get; set; } = "";
    public bool CanStageAll => !HasChanges && StageableStatus.Length > 0;
    public bool HasChanges => TfvcChanges == null ? !string.IsNullOrWhiteSpace(Diff) : TfvcChanges.Count > 0;

    public ChangeSnapshot(string root, string diff, string context = "", string branch = "", string statistics = "",
        IReadOnlyList<ChangeFile>? files = null, IReadOnlyList<TfvcFileChange>? tfvcChanges = null, bool diffIsPartial = false)
    {
        Root = root; Diff = diff; Context = context; Branch = branch; Statistics = statistics;
        Files = files ?? Array.Empty<ChangeFile>();
        TfvcChanges = tfvcChanges;
        DiffIsPartial = diffIsPartial;
        using (var hash = SHA256.Create())
            Fingerprint = Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(context + "\n" + diff)));
    }
}

public interface IChangeProvider
{
    Task<ChangeSnapshot> ReadAsync(string directory, CancellationToken token);
}

// Only the index is analyzed: unstaged edits must never leak into a staged commit summary.
public sealed class GitChangeProvider : IChangeProvider
{
    private const int MaxPreviewCharacters = 2_000_000;

    public Task<ChangeSnapshot> ReadAsync(string directory, CancellationToken token) => ReadAsync(directory, token, null);

    public async Task<ChangeSnapshot> ReadAsync(string directory, CancellationToken token, TextWriter? capture)
    {
        var root = (await RunAsync(directory, "rev-parse --show-toplevel", token)).Trim();
        var unresolved = await RunAsync(root, "diff --name-only --diff-filter=U", token);
        if (!string.IsNullOrWhiteSpace(unresolved)) throw new InvalidOperationException("请先解决 Git 合并冲突。");
        var context = await RunAsync(root, "status --porcelain=v2 --branch --untracked-files=no", token);
        var index = await RunAsync(root, "ls-files --stage -z", token);
        var branch = "未知";
        foreach (var line in context.Split('\n'))
            if (line.StartsWith("# branch.head ", StringComparison.Ordinal)) branch = line.Substring(14).Trim();
        var statistics = await RunAsync(root, "diff --cached --stat=120,80,20 --no-ext-diff --no-textconv", token);
        var preview = new StringBuilder();
        var partial = false;
        var diffHash = await RunAsync(root, "diff --cached --no-ext-diff --no-textconv --no-color --src-prefix=a/ --dst-prefix=b/", token,
            readOutput: async output => {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var decoder = new GitPatchDecoder();
                var bytes = new byte[16384];
                int count;
                do {
                    count = await output.BaseStream.ReadAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    hash.AppendData(bytes, 0, count);
                    var text = decoder.Read(bytes, count);
                    var toKeep = Math.Min(text.Length, MaxPreviewCharacters - preview.Length);
                    preview.Append(text, 0, toKeep);
                    partial |= toKeep < text.Length;
                    if (capture != null) await capture.WriteAsync(text).ConfigureAwait(false);
                } while (count != 0);
                return Convert.ToBase64String(hash.GetHashAndReset());
            });
        var names = await RunAsync(root, "diff --cached --name-status -z --no-ext-diff --no-textconv", token);
        var stageableStatus = names.Length == 0
            ? await RunAsync(root, "status --porcelain=v1 -z --untracked-files=all --ignore-submodules=dirty", token)
            : "";
        var endContext = await RunAsync(root, "status --porcelain=v2 --branch --untracked-files=no", token);
        var endIndex = await RunAsync(root, "ls-files --stage -z", token);
        if (context != endContext || index != endIndex) throw new InvalidOperationException("读取期间 Git 状态发生变化，请重试。");
        var entries = names.Split('\0');
        var files = new List<ChangeFile>();
        for (var i = 0; i < entries.Length - 1;) {
            var status = entries[i++][0];
            if (status == 'R' || status == 'C') i++;
            var operation = status switch { 'A' => "新增", 'D' => "删除", 'M' => "修改", 'R' => "重命名", 'C' => "复制", 'T' => "类型变更", _ => "变更" };
            files.Add(new ChangeFile(entries[i++], operation));
        }
        return new ChangeSnapshot(root, preview.ToString(), context + index + "\n" + diffHash, branch, statistics, files, diffIsPartial: partial)
            { StageableStatus = stageableStatus };
    }

    public async Task<ChangeSnapshot> StageAllAsync(ChangeSnapshot expected, CancellationToken token)
    {
        var current = await ReadAsync(expected.Root, token).ConfigureAwait(false);
        if (!expected.CanStageAll || !current.CanStageAll || current.Fingerprint != expected.Fingerprint ||
            current.StageableStatus != expected.StageableStatus ||
            !string.Equals(current.Root, expected.Root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("仓库或暂存范围已变化，请刷新后重试。");
        await RunAsync(current.Root, "add --all", token).ConfigureAwait(false);
        return await ReadAsync(current.Root, token).ConfigureAwait(false);
    }

    public async Task<string> CommitAsync(ChangeSnapshot expected, string message, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new InvalidOperationException("提交说明不能为空。");
        var current = await ReadAsync(expected.Root, token).ConfigureAwait(false);
        if (current.Fingerprint != expected.Fingerprint || string.IsNullOrWhiteSpace(current.Diff))
            throw new InvalidOperationException("暂存内容或分支已变化，请重新读取并生成。");
        // Preserve Git hooks and signing. Never stage files, bypass hooks, or push implicitly.
        return await RunAsync(expected.Root, "-c i18n.commitEncoding=utf-8 commit --file=-", token, message).ConfigureAwait(false);
    }

    private static async Task<string> RunAsync(string directory, string arguments, CancellationToken token, string? input = null,
        Func<StreamReader, Task<string>>? readOutput = null)
    {
        token.ThrowIfCancellationRequested();
        using (var process = new Process())
        {
            process.StartInfo = new ProcessStartInfo("git", arguments) {
                WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input != null,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            process.StartInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            process.Start();
            using (token.Register(() => { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } }))
            {
                var output = readOutput == null ? process.StandardOutput.ReadToEndAsync() : readOutput(process.StandardOutput);
                var error = process.StandardError.ReadToEndAsync();
                if (input != null) { var bytes = new UTF8Encoding(false).GetBytes(input); await process.StandardInput.BaseStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false); process.StandardInput.Close(); }
                try { await output.ConfigureAwait(false); }
                catch { if (!process.HasExited) process.Kill(); throw; }
                await error.ConfigureAwait(false);
                await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0) {
                    var detail = await error;
                    var hint = detail.Contains("Author identity unknown") || detail.Contains("unable to auto-detect email") ? "请先配置 Git user.name 和 user.email。" :
                        detail.Contains("gpg failed") || detail.Contains("failed to sign") ? "提交签名失败，请检查 GPG/SSH 签名配置。" :
                        input != null ? "提交未完成，请检查下面的 Git 输出（可能包含 hook 拒绝原因）。" : "请检查仓库路径及 Git 状态。";
                    throw new InvalidOperationException(hint + "\n" + detail);
                }
                return await output;
            }
        }
    }
}
