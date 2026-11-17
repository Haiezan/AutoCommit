using System;
using System.Collections.Generic;
using System.IO;
using EnvDTE;
using Microsoft.VisualStudio.Shell;

namespace AutoCommit.Vsix;

internal static class RepositoryDiscovery
{
    public static string FindCurrent()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var dte = ServiceProvider.GlobalProvider.GetService(typeof(DTE)) as DTE;
        if (dte == null) throw new InvalidOperationException("无法获取当前 Visual Studio 项目，请手工选择仓库。");
        var paths = new List<string>();
        if (dte.ActiveSolutionProjects is Array projects)
            foreach (var item in projects)
                if (item is Project project && !string.IsNullOrEmpty(project.FullName)) paths.Add(project.FullName);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths) { var root = FindRoot(path); if (root != null) roots.Add(root); }
        if (roots.Count > 1) throw new InvalidOperationException("选中的项目属于多个仓库，请只选择一个项目或手工指定仓库。");
        foreach (var root in roots) return root;
        var solutionRoot = FindRoot(dte.Solution?.FullName);
        if (solutionRoot != null) return solutionRoot;
        var documentRoot = FindRoot(dte.ActiveDocument?.FullName);
        if (documentRoot != null) return documentRoot;
        if (paths.Count > 1) throw new InvalidOperationException("请选择一个 TFVC 项目，或手工指定签入目录。");
        var candidate = paths.Count == 1 ? paths[0] : dte.Solution?.FullName;
        if (string.IsNullOrEmpty(candidate)) candidate = dte.ActiveDocument?.FullName;
        if (!string.IsNullOrEmpty(candidate)) return Path.GetDirectoryName(candidate)!;
        throw new InvalidOperationException("请打开项目或手工指定 Git / TFVC 工作区目录。");
    }

    private static string? FindRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(folder)) {
            var git = Path.Combine(folder, ".git");
            if (Directory.Exists(git) || File.Exists(git)) return folder;
            folder = Directory.GetParent(folder)?.FullName;
        }
        return null;
    }
}
