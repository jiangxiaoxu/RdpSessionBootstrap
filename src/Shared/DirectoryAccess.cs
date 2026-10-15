using System.Security.AccessControl;
using System.Security.Principal;

public static class DirectoryAccess
{
    public static void Apply(string path, string? readerSid = null, bool traversalOnly = false)
        => new DirectoryInfo(path).SetAccessControl(CreatePolicy(readerSid, traversalOnly));

    public static DirectorySecurity CreatePolicy(string? readerSid = null, bool traversalOnly = false)
    {
        var security = new DirectorySecurity();
        security.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
        security.SetAccessRuleProtection(true, false);
        const InheritanceFlags children = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (string sid in new[] { "S-1-5-18", "S-1-5-32-544" })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid),
                FileSystemRights.FullControl, children, PropagationFlags.None, AccessControlType.Allow));
        if (readerSid != null)
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(readerSid),
                traversalOnly ? FileSystemRights.Traverse | FileSystemRights.ReadAttributes : FileSystemRights.ReadAndExecute,
                traversalOnly ? InheritanceFlags.None : children, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    public static void ApplyPrivateFile(string path)
        => new FileInfo(path).SetAccessControl(CreateFilePolicy());

    public static FileSecurity CreateFilePolicy(string? readerSid = null)
    {
        var security = new FileSecurity();
        security.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
        security.SetAccessRuleProtection(true, false);
        foreach (string sid in new[] { "S-1-5-18", "S-1-5-32-544" })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid),
                FileSystemRights.FullControl, AccessControlType.Allow));
        if (readerSid != null)
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(readerSid),
                FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        return security;
    }

    public static void ApplyTree(string directory, string? readerSid = null)
    {
        Apply(directory, readerSid);
        foreach (string path in Directory.GetFiles(directory))
            new FileInfo(path).SetAccessControl(CreateFilePolicy(readerSid));
        foreach (string path in Directory.GetDirectories(directory))
            ApplyTree(path, readerSid);
    }
}
