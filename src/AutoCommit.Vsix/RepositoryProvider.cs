using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutoCommit.Core;

namespace AutoCommit.Vsix;

internal sealed class RepositoryProvider
{
    private readonly GitChangeProvider git = new GitChangeProvider();
    public bool IsTfvc { get; private set; }
    public Task<ChangeSnapshot> ReadAsync(string path, CancellationToken token)
    {
        var current = new DirectoryInfo(path);
        while (current != null) {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git"))) {
                IsTfvc = false; return git.ReadAsync(path, token);
            }
            current = current.Parent;
        }
        IsTfvc = true; TfvcRuntime.Initialize(); return ReadTfvcAsync(path, token);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static Task<ChangeSnapshot> ReadTfvcAsync(string path, CancellationToken token) => new TfvcProvider().ReadAsync(path, token);
    public Task<string> CommitAsync(ChangeSnapshot snapshot, string message, CancellationToken token) =>
        snapshot.Branch.StartsWith("TFVC / ", System.StringComparison.Ordinal)
        ? new TfvcProvider().CommitAsync(snapshot, message, token) : git.CommitAsync(snapshot, message, token);
}
