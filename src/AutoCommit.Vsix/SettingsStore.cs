using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AutoCommit.Core;
using Newtonsoft.Json;

namespace AutoCommit.Vsix;

internal static class SettingsStore
{
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoCommit");
    public static ModelSettings Load() => File.Exists(Path.Combine(Folder, "settings.json"))
        ? JsonConvert.DeserializeObject<ModelSettings>(File.ReadAllText(Path.Combine(Folder, "settings.json"))) ?? new ModelSettings() : new ModelSettings();
    public static string LoadKey() => File.Exists(Path.Combine(Folder, "key.dat"))
        ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(Folder, "key.dat")), null, DataProtectionScope.CurrentUser)) : "";
    public static void Save(ModelSettings settings, string key)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "settings.json"), JsonConvert.SerializeObject(settings, Formatting.Indented));
        File.WriteAllBytes(Path.Combine(Folder, "key.dat"), ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser));
    }

    // Bind credentials to both project and endpoint. A shared config cannot redirect an existing key.
    private static string ProjectKeyPath(string root, string endpoint)
    {
        using (var hash = SHA256.Create()) {
            var identity = Path.GetFullPath(root).TrimEnd('\\', '/').ToUpperInvariant() + "\n" + endpoint.Trim().TrimEnd('/');
            var name = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-", "");
            return Path.Combine(Folder, "credentials", name + ".dat");
        }
    }
    public static string LoadProjectKey(string root, string endpoint)
    {
        var path = ProjectKeyPath(root, endpoint);
        return File.Exists(path) ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser)) : "";
    }
    public static void SaveProject(string root, ModelSettings settings, string key)
    {
        var path = ProjectKeyPath(root, settings.BaseUrl);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser));
        ProjectConfiguration.Save(root, settings);
    }
}
