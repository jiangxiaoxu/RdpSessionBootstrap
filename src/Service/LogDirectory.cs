using System.Security.AccessControl;
using System.Security.Principal;

internal static class LogDirectory
{
    public static void Prepare(string directory, string accountSid)
    {
        Directory.CreateDirectory(directory);
        DirectoryAccess.Apply(directory, accountSid);

        foreach (string path in Directory.GetFiles(directory))
        {
            string name = Path.GetFileName(path);
            if (!name.Equals("service.log", StringComparison.OrdinalIgnoreCase) &&
                !(name.StartsWith("service-", StringComparison.OrdinalIgnoreCase) &&
                  name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))) continue;
            var file = new FileInfo(path);
            var access = file.GetAccessControl(AccessControlSections.Access);
            access.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
            foreach (FileSystemAccessRule rule in access.GetAccessRules(true, false, typeof(SecurityIdentifier)))
                access.RemoveAccessRuleSpecific(rule);
            access.SetAccessRuleProtection(false, false);
            file.SetAccessControl(access);
        }
    }
}
