using System.Security.AccessControl;
using System.Security.Principal;
using AgentMemory.McpHost.Coding.Client;

namespace AgentMemory.McpHost.Coding;

internal static class CodingJournalDirectory
{
    public static void Prepare(string path)
    {
        CodingHookState.RejectLinks(path);
        if (OperatingSystem.IsWindows()) PrepareWindows(path);
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var mode = File.GetUnixFileMode(path);
            if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                         UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                throw new ArgumentException("Journal directory must be private to the service account.");
        }
        foreach (var file in new[] { "receipts.db", "receipts.db-journal", "receipts.db-wal", "receipts.db-shm", "host.lock" })
            CodingHookState.RejectLinks(Path.Combine(path, file));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void PrepareWindows(string path)
    {
        using var current = WindowsIdentity.GetCurrent();
        var owner = current.User ?? throw new ArgumentException("Cannot identify the journal service account.");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var info = new DirectoryInfo(path);
        if (!info.Exists)
        {
            var acl = new DirectorySecurity();
            acl.SetOwner(owner);
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { owner, system, administrators })
                acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            info.Create(acl);
        }
        Check(info.GetAccessControl());
        foreach (var name in new[] { "receipts.db", "receipts.db-journal", "receipts.db-wal", "receipts.db-shm", "host.lock" })
        {
            var file = new FileInfo(Path.Combine(path, name));
            CodingHookState.RejectLinks(file.FullName);
            if (file.Exists) Check(file.GetAccessControl());
        }

        void Check(FileSystemSecurity existing)
        {
            foreach (FileSystemAccessRule rule in existing.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow && !rule.IdentityReference.Equals(owner) &&
                    !rule.IdentityReference.Equals(system) && !rule.IdentityReference.Equals(administrators))
                    throw new ArgumentException("Journal files and directory must be private to the service account and administrators.");
        }
    }
}
