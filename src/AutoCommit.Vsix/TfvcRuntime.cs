using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace AutoCommit.Vsix;

internal static class TfvcRuntime
{
    private static bool registered;
    public static void Initialize()
    {
        if (registered) return;
        registered = true;
        var ide = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule!.FileName)!;
        var folder = Path.Combine(ide, "CommonExtensions", "Microsoft", "TeamFoundation", "Team Explorer");
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) => {
            var name = new AssemblyName(args.Name).Name!;
            if (!name.StartsWith("Microsoft.TeamFoundation.", StringComparison.Ordinal) &&
                !name.StartsWith("Microsoft.VisualStudio.Services.", StringComparison.Ordinal)) return null;
            var path = Path.Combine(folder, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
    }
}
