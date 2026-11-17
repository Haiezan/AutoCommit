using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutoCommit.Core;
using Microsoft.TeamFoundation.Client;
using Microsoft.TeamFoundation.VersionControl.Client;

namespace AutoCommit.Vsix;

internal sealed class TfvcProvider
{
    public Task<ChangeSnapshot> ReadAsync(string directory, CancellationToken token) => Task.Run(() => Read(directory, token), token);

    private static Workspace Open(string path)
    {
        var info = Workstation.Current.GetLocalWorkspaceInfo(path);
        if (info == null) throw new InvalidOperationException("此目录未映射到 TFVC 工作区。请先在 Team Explorer 连接 TFS 并映射工作区。");
        var collection = TfsTeamProjectCollectionFactory.GetTeamProjectCollection(info.ServerUri);
        collection.EnsureAuthenticated();
        return collection.GetService<VersionControlServer>().GetWorkspace(path);
    }

    private static ChangeSnapshot Read(string directory, CancellationToken token)
    {
        var root = Path.GetFullPath(directory);
        var workspace = Open(root);
        var changes = workspace.GetPendingChanges(root, RecursionType.Full).OrderBy(x => x.ServerItem, StringComparer.Ordinal).ToArray();
        var files = changes.Select(change => new ChangeFile(change.ServerItem, change.ChangeType.ToString())).ToArray();
        var body = new StringBuilder(); var identity = new StringBuilder();
        identity.Append(workspace.VersionControlServer.TeamProjectCollection.Uri).Append('|').Append(workspace.Name).Append('|').Append(workspace.OwnerName);
        foreach (var change in changes) {
            token.ThrowIfCancellationRequested();
            identity.Append('\n').Append(change.ServerItem).Append('|').Append(change.SourceServerItem).Append('|').Append(change.Version).Append('|').Append(change.ChangeType);
            body.AppendLine("文件：" + change.ServerItem + "；操作：" + change.ChangeType + "；原路径：" + change.SourceServerItem);
            if (change.ItemType == ItemType.Folder) continue;
            if (!change.IsAdd) {
                var temp = Path.GetTempFileName();
                try { change.DownloadBaseFile(temp); TfvcContent.AppendFile(temp, "工作区基线（修改前）", body, identity); }
                finally { File.Delete(temp); }
            }
            if (!change.IsDelete) {
                if (!File.Exists(change.LocalItem)) throw new InvalidOperationException("挂起文件不存在：" + change.LocalItem);
                TfvcContent.AppendFile(change.LocalItem, "本地内容（修改后）", body, identity);
            }
            if (body.Length > 200000) throw new InvalidOperationException("TFVC 变更过大，请手工指定更小的项目目录后重试。");
        }
        return new ChangeSnapshot(root, body.ToString(), identity.ToString(), "TFVC / " + workspace.Name,
            changes.Length + " 项挂起更改；范围：" + root + "（递归，不使用 Team Explorer 的勾选状态）", files);
    }

    public Task<string> CommitAsync(ChangeSnapshot expected, string message, CancellationToken token) => Task.Run(() => {
        if (string.IsNullOrWhiteSpace(message)) throw new InvalidOperationException("签入说明不能为空。");
        var current = Read(expected.Root, token);
        if (current.Fingerprint != expected.Fingerprint || string.IsNullOrWhiteSpace(current.Diff))
            throw new InvalidOperationException("TFVC 挂起更改已变化，请重新读取并生成。");
        var workspace = Open(expected.Root);
        var pending = workspace.GetPendingChanges(expected.Root, RecursionType.Full);
        var evaluation = workspace.EvaluateCheckin(CheckinEvaluationOptions.All, pending, pending, message, null, null);
        if (evaluation.Conflicts.Length != 0 || evaluation.NoteFailures.Length != 0 || evaluation.PolicyFailures.Length != 0 || evaluation.PolicyEvaluationException != null)
            throw new InvalidOperationException("TFVC 签入检查未通过（冲突、必填说明或团队策略）。说明已保留，请复制到 Team Explorer 完成检查及签入。");
        // Do not override policies, provide fabricated work items, or bypass gated check-in.
        var id = workspace.CheckIn(pending, message);
        if (id <= 0) throw new InvalidOperationException("未返回有效 Changeset，请在 Team Explorer 检查门控签入或服务器状态后再操作。");
        return "TFVC 已签入服务器，Changeset " + id;
    }, token);
}
