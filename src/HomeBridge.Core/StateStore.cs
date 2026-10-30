using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace HomeBridge.Core;

public sealed record LauncherEntry(string Name, string Path);
public sealed record TrustedPeer(string Fingerprint, string Name);
public sealed class UserSettings
{
    public int Version { get; set; } = 1;
    public string DeviceName { get; set; } = Environment.MachineName;
    public List<LauncherEntry> Applications { get; set; } = [];
    public List<TrustedPeer> Peers { get; set; } = [];
}

// One process owns this per-user store (enforced by the desktop's named mutex).
public sealed class StateStore
{
    private readonly object _gate = new();
    public string DirectoryPath { get; }
    public StateStore(string? directory = null)
    {
        DirectoryPath = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeBridge");
        Directory.CreateDirectory(DirectoryPath);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(DirectoryPath).SetAccessControl(security);
    }

    public UserSettings Load()
    {
        lock (_gate)
        {
            var path = Path.Combine(DirectoryPath, "settings.json");
            if (!File.Exists(path)) return new();
            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("Empty settings.");
            if (settings.Version != 1 || settings.Applications is null || settings.Peers is null || string.IsNullOrWhiteSpace(settings.DeviceName))
                throw new InvalidDataException("Unsupported or damaged settings. Back up settings.json before repairing it; trust will not be silently reset.");
            return settings;
        }
    }
    public void Save(UserSettings settings)
    {
        lock (_gate) AtomicWrite("settings.json", JsonSerializer.SerializeToUtf8Bytes(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
    public byte[]? ReadSecret(string name)
    {
        lock (_gate)
        {
            var path = Path.Combine(DirectoryPath, name);
            return File.Exists(path) ? ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser) : null;
        }
    }
    public void WriteSecret(string name, byte[] value)
    {
        lock (_gate) AtomicWrite(name, ProtectedData.Protect(value, null, DataProtectionScope.CurrentUser));
    }
    private void AtomicWrite(string name, byte[] bytes)
    {
        var path = Path.Combine(DirectoryPath, name);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
