using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using Task = System.Threading.Tasks.Task;
using Microsoft.VisualStudio.Shell;

namespace AutoCommit.Vsix;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideToolWindow(typeof(CommitWindow))]
[Guid("2472be40-75aa-4ed7-bc15-4dde87907610")]
public sealed class AutoCommitPackage : AsyncPackage
{
    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var service = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
        service?.AddCommand(new MenuCommand((s, e) => {
            _ = JoinableTaskFactory.RunAsync(async () => {
                try { await ShowToolWindowAsync(typeof(CommitWindow), 0, true, DisposalToken); }
                catch (Exception ex) { ActivityLog.LogError("AutoCommit", ex.Message); }
            });
        }, new CommandID(new Guid("4dde208c-4bc5-43e6-b520-7953cd73788a"), 0x0100)));
    }
}

[Guid("28489e08-72de-4af4-9161-e20b8e721f41")]
public sealed class CommitWindow : ToolWindowPane
{
    public CommitWindow() : base(null) { Caption = "AutoCommit"; Content = new CommitControl(); }
}
