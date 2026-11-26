using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutoCommit.Core;
using EnvDTE;
using Microsoft.TeamFoundation.VersionControl.Client;
using ServiceProvider = Microsoft.VisualStudio.Shell.ServiceProvider;
using ThreadHelper = Microsoft.VisualStudio.Shell.ThreadHelper;

namespace AutoCommit.Vsix;

internal sealed class TfvcSelectionException : InvalidOperationException
{
    internal TfvcSelectionException(string stage, Exception? cause = null)
        : base($"无法读取 TFVC 包含项（{stage}：{cause?.GetBaseException().GetType().Name ?? "接口数据不可用"}）。请在 Team Explorer 打开“挂起的更改”，等待清单加载后重试。", cause) { }
}

internal sealed class TfvcProvider
{
    private const string SelectionChanged = "Team Explorer 的包含项或挂起更改已变化，请重新读取并生成。";
    private const int MaxFileDiffCharacters = 100_000;
    private const int MaxSnapshotDiffCharacters = 2_000_000;

    public async Task<ChangeSnapshot> ReadAsync(string directory, CancellationToken token, bool includeDiff, TextWriter? capture, bool detectTfvcScope)
    {
        var root = Path.GetFullPath(directory);
        var selection = await GetSelectionAsync(root, token, detectTfvcScope).ConfigureAwait(false);
        root = selection.Root;
        var snapshot = await Task.Run(() => Read(root, selection.Workspace,
            GetCurrentChanges(selection.Workspace, selection.Changes), selection.IncludedCount, token, includeDiff, capture), token).ConfigureAwait(false);
        await EnsureSelectionUnchangedAsync(root, selection.Workspace, selection.Changes, token).ConfigureAwait(false);
        return snapshot;
    }

    private static async Task<(Workspace Workspace, PendingChange[] Changes, int IncludedCount, string Root)> GetSelectionAsync(string root, CancellationToken token, bool detectScope = false)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
        Workspace workspace;
        PendingChange[] included;
        var stage = "DTE";
        try {
            var dte = ServiceProvider.GlobalProvider.GetService(typeof(DTE)) as DTE ?? throw new TfvcSelectionException(stage);
            stage = "VersionControlExt";
            var versionControl = dte.GetObject("Microsoft.VisualStudio.TeamFoundation.VersionControl.VersionControlExt")
                ?? throw new TfvcSelectionException(stage);
            stage = "PendingChanges";
            var pendingChanges = GetAutomationProperty(versionControl, stage) ?? throw new TfvcSelectionException(stage);
            stage = "Workspace";
            workspace = GetAutomationProperty(pendingChanges, stage) as Workspace ?? throw new TfvcSelectionException(stage);
            stage = "IncludedChanges";
            included = (GetAutomationProperty(pendingChanges, stage) as PendingChange[])?.ToArray() ?? throw new TfvcSelectionException(stage);
        } catch (Exception ex) when (ex is COMException || ex is MissingMethodException || ex is TargetInvocationException) {
            throw new TfvcSelectionException(stage, ex);
        }

        return await Task.Run(() => {
            if (!workspace.IsLocalPathMapped(root))
                throw new InvalidOperationException("此目录未映射到 Team Explorer 当前显示的 TFVC 工作区。请切换工作区或选择已映射的目录。");
            var localChanges = included.Select(change => {
                token.ThrowIfCancellationRequested();
                var path = change.LocalItem;
                if (string.IsNullOrEmpty(path)) path = workspace.TryGetLocalItemForServerItem(change.ServerItem);
                return (Change: change, Path: string.IsNullOrEmpty(path) ? null : Path.GetFullPath(path));
            }).ToArray();

            if (detectScope) {
                foreach (var item in localChanges) {
                    if (item.Path == null || IsWithinDirectory(item.Path, root)) continue;
                    var ancestor = Directory.GetParent(root);
                    while (ancestor != null && workspace.IsLocalPathMapped(ancestor.FullName)) {
                        if (IsWithinDirectory(item.Path, ancestor.FullName)) {
                            root = ancestor.FullName;
                            break;
                        }
                        ancestor = ancestor.Parent;
                    }
                }
            }
            var changes = localChanges.Where(item => item.Path != null && IsWithinDirectory(item.Path, root))
                .Select(item => item.Change).OrderBy(ChangeIdentity, StringComparer.Ordinal).ToArray();
            return (workspace, changes, included.Length, root);
        }, token).ConfigureAwait(false);
    }

    private static bool IsWithinDirectory(string path, string directory)
    {
        var root = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static object? GetAutomationProperty(object? target, string name) =>
        target?.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);

    private static string WorkspaceIdentity(Workspace workspace) =>
        workspace.VersionControlServer.TeamProjectCollection.Uri + "|" + workspace.Name + "|" + workspace.OwnerName;

    private static string ChangeIdentity(PendingChange change) =>
        $"{change.ServerItem}|{change.SourceServerItem}|{change.LocalItem}|{change.PendingChangeId}|{change.Version}|{change.ChangeType}|{change.ItemType}|{change.Encoding}";

    private static async Task EnsureSelectionUnchangedAsync(string root, Workspace workspace, PendingChange[] changes, CancellationToken token)
    {
        var current = await GetSelectionAsync(root, token).ConfigureAwait(false);
        if (WorkspaceIdentity(current.Workspace) != WorkspaceIdentity(workspace) ||
            !current.Changes.Select(ChangeIdentity).SequenceEqual(changes.Select(ChangeIdentity)))
            throw new InvalidOperationException(SelectionChanged);
    }

    private static PendingChange[] GetCurrentChanges(Workspace workspace, PendingChange[] included)
    {
        if (included.Length == 0) return included;
        var paths = included.Select(change => change.ServerItem).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var pending = workspace.GetPendingChanges(paths, RecursionType.None, includeDownloadInfo: true).ToLookup(ChangeIdentity);
        return included.Select(change => pending[ChangeIdentity(change)].SingleOrDefault() ??
            throw new InvalidOperationException(SelectionChanged)).ToArray();
    }

    private static ChangeSnapshot Read(string root, Workspace workspace, PendingChange[] changes, int includedCount, CancellationToken token,
        bool includeDiff, TextWriter? capture = null)
    {
        var files = changes.Select(change => new ChangeFile(change.ServerItem, change.ChangeType.ToString())).ToArray();
        var diff = includeDiff ? new List<TfvcFileChange>(changes.Length) : null;
        var fileBudget = Math.Min(MaxFileDiffCharacters, MaxSnapshotDiffCharacters / Math.Max(1, changes.Length));
        var readText = includeDiff || capture != null;
        var identity = new StringBuilder(WorkspaceIdentity(workspace));
        foreach (var change in changes) {
            token.ThrowIfCancellationRequested();
            identity.Append('\n').Append(ChangeIdentity(change));
            TfvcContent? before = null;
            TfvcContent? after = null;
            if (change.ItemType != ItemType.Folder) {
                var baseEncoding = change.Encoding;
                if (readText && !change.IsAdd && (change.IsEncoding || change.Encoding == -2)
                    && change.ItemId > 0 && change.Version > 0)
                    baseEncoding = workspace.VersionControlServer.GetItem(change.ItemId, change.Version).Encoding;
                if (!change.IsAdd) {
                    var temp = Path.GetTempFileName();
                    try { change.DownloadBaseFile(temp); before = TfvcContent.ReadFile(temp, readText, baseEncoding); }
                    finally { File.Delete(temp); }
                }
                if (!change.IsDelete) {
                    if (!File.Exists(change.LocalItem)) throw new InvalidOperationException("挂起文件不存在：" + change.LocalItem);
                    after = TfvcContent.ReadFile(change.LocalItem, readText, change.Encoding == -2 ? baseEncoding : change.Encoding);
                }
                identity.Append('|').Append(before?.Fingerprint).Append('|').Append(after?.Fingerprint);
            }
            if (readText) {
                var detail = TfvcContent.CreateChange(change.ServerItem, change.ChangeType.ToString(), change.SourceServerItem,
                    before, after, capture == null ? fileBudget : int.MaxValue, token);
                diff?.Add(detail);
                if (capture != null) detail.WriteTo(capture);
            }
        }
        return new ChangeSnapshot(root, "", identity.ToString(), "TFVC / " + workspace.Name,
            $"当前目录 {changes.Length} 项，Team Explorer 共包含 {includedCount} 项。范围：{root}（递归）" +
            (changes.Length < includedCount ? "。可在“切换仓库 / 手工指定”中选择更大的映射目录。" : ""), files, diff);
    }

    public async Task<string> CommitAsync(ChangeSnapshot expected, string message, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new InvalidOperationException("签入说明不能为空。");
        var selection = await GetSelectionAsync(expected.Root, token).ConfigureAwait(false);
        var workspace = selection.Workspace;
        var pending = await Task.Run(() => {
            var changes = GetCurrentChanges(workspace, selection.Changes);
            var current = Read(expected.Root, workspace, changes, selection.IncludedCount, token, includeDiff: false);
            if (current.Fingerprint != expected.Fingerprint || changes.Length == 0)
                throw new InvalidOperationException(SelectionChanged);
            var evaluation = workspace.EvaluateCheckin(CheckinEvaluationOptions.All, changes, changes, message, null, null);
            if (evaluation.Conflicts.Length != 0 || evaluation.NoteFailures.Length != 0 || evaluation.PolicyFailures.Length != 0 || evaluation.PolicyEvaluationException != null)
                throw new InvalidOperationException("TFVC 签入检查未通过（冲突、必填说明或团队策略）。说明已保留，请复制到 Team Explorer 完成检查及签入。");
            return changes;
        }, token).ConfigureAwait(false);

        await EnsureSelectionUnchangedAsync(expected.Root, workspace, selection.Changes, token).ConfigureAwait(false);
        var id = await Task.Run(() => workspace.CheckIn(pending, message), token).ConfigureAwait(false);
        if (id <= 0) throw new InvalidOperationException("未返回有效 Changeset，请在 Team Explorer 检查门控签入或服务器状态后再操作。");
        return "TFVC 已签入服务器，Changeset " + id;
    }
}
