using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AutoCommit.Core;

public sealed class ChangeSnapshot
{
    public string Root { get; }
    public string Diff { get; }
    public string Fingerprint { get; }
    public string Context { get; }
    public string Branch { get; }
    public string Statistics { get; }
    public ChangeSnapshot(string root, string diff, string context = "", string branch = "", string statistics = "")
    {
        Root = root; Diff = diff; Context = context; Branch = branch; Statistics = statistics;
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
    public async Task<ChangeSnapshot> ReadAsync(string directory, CancellationToken token)
    {
        var root = (await RunAsync(directory, "rev-parse --show-toplevel", token)).Trim();
        var unresolved = await RunAsync(root, "diff --name-only --diff-filter=U", token);
        if (!string.IsNullOrWhiteSpace(unresolved)) throw new InvalidOperationException("请先解决 Git 合并冲突。");
        var context = await RunAsync(root, "status --porcelain=v2 --branch --untracked-files=no", token);
        var index = await RunAsync(root, "ls-files --stage -z", token);
        var branch = "未知";
        foreach (var line in context.Split('\n'))
            if (line.StartsWith("# branch.head ", StringComparison.Ordinal)) branch = line.Substring(14).Trim();
        var statistics = await RunAsync(root, "diff --cached --stat --no-ext-diff --no-textconv", token);
        var diff = await RunAsync(root, "diff --cached --no-ext-diff --no-textconv --no-color --src-prefix=a/ --dst-prefix=b/", token);
        return new ChangeSnapshot(root, diff, context + index, branch, statistics);
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

    private static async Task<string> RunAsync(string directory, string arguments, CancellationToken token, string? input = null, bool allowFailure = false)
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
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (input != null) { var bytes = new UTF8Encoding(false).GetBytes(input); await process.StandardInput.BaseStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false); process.StandardInput.Close(); }
                await Task.WhenAll(output, error).ConfigureAwait(false);
                await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0 && !allowFailure) {
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
