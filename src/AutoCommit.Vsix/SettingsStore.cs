using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AutoCommit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutoCommit.Vsix;

internal enum KeyScope { Shared, Project, Anonymous }

internal static class SettingsStore
{
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoCommit");
    internal static readonly CredentialStore Credentials = new CredentialStore(Folder);
    private static string SettingsPath => Path.Combine(Folder, "settings.json");
    public static bool HasGlobalSettings => File.Exists(SettingsPath);
    public static ModelSettings Load() => HasGlobalSettings
        ? JsonConvert.DeserializeObject<ModelSettings>(File.ReadAllText(SettingsPath)) ?? new ModelSettings() : new ModelSettings();

    public static bool MigrateLegacyKey()
    {
        if (!File.Exists(Path.Combine(Folder, "key.dat"))) return false;
        var endpoint = HasGlobalSettings ? (string?)JObject.Parse(File.ReadAllText(SettingsPath)).GetValue("BaseUrl", StringComparison.OrdinalIgnoreCase) : null;
        return Credentials.MigrateLegacyKey(endpoint);
    }

    public static void Save(ModelSettings settings)
    {
        MigrateLegacyKey(); // Bind the old ciphertext before replacing its saved address.
        Directory.CreateDirectory(Folder);
        File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(settings, Formatting.Indented));
    }
}

internal sealed class CredentialStore
{
    private readonly string folder;
    // Existing project files contain raw DPAPI blobs, including encrypted empty strings.
    private static readonly byte[] AnonymousMarker = Encoding.ASCII.GetBytes("AutoCommit:anonymous:v1");
    internal CredentialStore(string folder) { this.folder = folder; }

    private string CredentialPath(string root, string endpoint)
    {
        var address = endpoint.Trim().TrimEnd('/');
        var directory = Path.Combine(folder, "credentials");
        var identity = address;
        if (root.Length == 0) directory = Path.Combine(directory, "shared");
        else identity = Path.GetFullPath(root).TrimEnd('\\', '/').ToUpperInvariant() + "\n" + address;
        using var hash = SHA256.Create();
        return Path.Combine(directory, BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-", "") + ".dat");
    }

    internal bool MigrateLegacyKey(string? endpoint)
    {
        var legacy = Path.Combine(folder, "key.dat");
        if (!File.Exists(legacy)) return false;
        string destination;
        var bound = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https");
        if (bound) {
            destination = CredentialPath("", endpoint!);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination)) destination = legacy + "." + Guid.NewGuid().ToString("N") + ".bak";
        } else destination = legacy + "." + Guid.NewGuid().ToString("N") + ".unbound";
        try { File.Move(legacy, destination); }
        catch (IOException) when (!File.Exists(legacy)) { } // Another VS instance migrated it.
        return !bound;
    }

    internal string Load(string root, string endpoint, out KeyScope scope)
    {
        scope = KeyScope.Shared;
        if (root.Length != 0) {
            var project = CredentialPath(root, endpoint);
            if (File.Exists(project)) {
                scope = KeyScope.Project;
                var bytes = File.ReadAllBytes(project);
                if (bytes.SequenceEqual(AnonymousMarker)) { scope = KeyScope.Anonymous; return ""; }
                var key = Decrypt(bytes);
                if (key.Length != 0) return key;
            }
        }
        scope = KeyScope.Shared;
        var shared = CredentialPath("", endpoint);
        return File.Exists(shared) ? Decrypt(File.ReadAllBytes(shared)) : "";
    }

    private static string Decrypt(byte[] bytes) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));

    internal void UseShared(string root, string endpoint)
    {
        var path = CredentialPath(root, endpoint);
        if (root.Length != 0 && File.Exists(path)) File.Delete(path);
    }

    internal void Save(string root, string endpoint, KeyScope scope, string value)
    {
        if (scope != KeyScope.Shared && root.Length == 0) throw new InvalidOperationException("请先读取项目。");
        var path = CredentialPath(scope == KeyScope.Shared ? "" : root, endpoint);
        var bytes = scope == KeyScope.Anonymous ? AnonymousMarker :
            ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            File.WriteAllBytes(temporary, bytes);
            try { File.Move(temporary, path); }
            catch (IOException) when (File.Exists(path)) { File.Replace(temporary, path, null); }
        } finally { File.Delete(temporary); }
    }
}
