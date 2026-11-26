using System.Security.AccessControl;
using System.Security.Principal;
using Utility.Platform.Native;
using Utility.Platform.Providers;

namespace Utility.Platform.Lenovo;

internal static class ElevatedWorkerInstallation
{
    private const FileSystemRights GenericAll = (FileSystemRights)0x10000000;
    private const FileSystemRights GenericWrite = (FileSystemRights)0x40000000;
    private const FileSystemRights ReplaceRights = FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | GenericAll;
    private const FileSystemRights ModifyRights = ReplaceRights | FileSystemRights.Delete |
        FileSystemRights.Write | GenericWrite;

    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemAccount = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier TrustedInstaller =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    public static bool IsProtected(string directory)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrEmpty(programFiles) || !DiskCleanupService.IsChild(directory, programFiles) ||
            !DiskCleanupService.IsFixedDrive(directory) || !DiskCleanupService.HasSafeParents(directory))
            return false;

        var folder = new DirectoryInfo(directory);
        for (var parent = folder; parent is not null; parent = parent.Parent)
        {
            var rights = DiskCleanupService.IsInOrEqual(parent.FullName, programFiles) ? ModifyRights : ReplaceRights;
            if (!HasTrustedPermissions(parent.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), rights))
                return false;
        }

        // Check the actual payload as well, for example when files retained their original ACLs during a move.
        return HasProtectedContents(folder);
    }

    private static bool HasProtectedContents(DirectoryInfo directory)
    {
        foreach (var item in directory.EnumerateFileSystemInfos())
        {
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                return false;

            const AccessControlSections sections = AccessControlSections.Access | AccessControlSections.Owner;
            if (item is DirectoryInfo child)
            {
                if (!HasTrustedPermissions(child.GetAccessControl(sections), ModifyRights) || !HasProtectedContents(child))
                    return false;
            }
            else if (item is not FileInfo file || !HasTrustedPermissions(file.GetAccessControl(sections), ModifyRights))
                return false;
        }
        return true;
    }

    private static bool HasTrustedPermissions(FileSystemSecurity security, FileSystemRights rights)
    {
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (!IsTrusted(descriptor.Owner) || descriptor.DiscretionaryAcl is not { } acl)
            return false;

        foreach (var entry in acl)
        {
            if ((entry.AceFlags & AceFlags.InheritOnly) != 0)
                continue;
            if (entry is not QualifiedAce ace)
                return false;
            if (ace.AceQualifier == AceQualifier.AccessAllowed && (ace.AccessMask & (int)rights) != 0 &&
                !IsTrusted(ace.SecurityIdentifier))
                return false;
        }
        return true;
    }

    private static bool IsTrusted(SecurityIdentifier? sid) =>
        sid == Administrators || sid == SystemAccount || sid == TrustedInstaller;

    public static bool HasConsistentSignature(string directory, string workerPath)
    {
        var appPath = Path.Combine(directory, "Utility.exe");
        var appStatus = WinTrustInterop.VerifyAuthenticodeStatus(appPath);
        var workerStatus = WinTrustInterop.VerifyAuthenticodeStatus(workerPath);
        if (appStatus == WinTrustInterop.TrustENoSignature && workerStatus == WinTrustInterop.TrustENoSignature)
            return true;
        return appStatus == 0 && workerStatus == 0 &&
            WinTrustInterop.ReadSignerSha256(appPath) is { } appSigner &&
            string.Equals(appSigner, WinTrustInterop.ReadSignerSha256(workerPath), StringComparison.Ordinal);
    }
}
