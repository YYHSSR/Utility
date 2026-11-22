namespace Utility.Core.Models;

/// <summary>Logical grouping for installed PnP signed drivers.</summary>
public enum DriverCategory
{
    All = 0,
    Graphics = 1,
    WirelessNetwork = 2,
    Audio = 3,
    Chipset = 4,
    LenovoControl = 5,
    Other = 6
}

/// <summary>Signed driver inventory entry from Win32_PnPSignedDriver / SetupAPI.</summary>
public sealed record InstalledDriverEntry(
    string DeviceName,
    string ProviderName,
    string DriverVersion,
    string DriverDate,
    string InfName,
    string HardwareId,
    DriverCategory Category);

/// <summary>Verified official resource links for driver acquisition and OS updates.</summary>
public sealed record OfficialDriverLinks(
    string Lenovo83JJDriverPage,
    string NvidiaDriverPage,
    string WindowsUpdateUri,
    string WindowsOptionalUpdatesUri)
{
    public static OfficialDriverLinks Default => new(
        "https://pcsupport.lenovo.com/us/en/products/laptops-and-netbooks/legion-series/legion-5-15irx9/83jj/downloads/driver-list",
        "https://www.nvidia.com/en-us/drivers/",
        "ms-settings:windowsupdate",
        "ms-settings:windowsupdate-optionalupdates");
}

/// <summary>Raw metadata extracted from a user-selected local driver package prior to launch evaluation.</summary>
public sealed record DriverPackageInspection(
    string FilePath,
    string FileExtension,
    long FileSizeBytes,
    string Sha256Hex,
    bool IsSignatureValid,
    string SignatureStatusDetail,
    string? SignerSubject,
    string? SignerIssuer,
    string? ProductName,
    string? FileDescription,
    string? FileVersion,
    string? CompanyName,
    string? PeArchitecture);

/// <summary>Applicability and provenance verdict for a local driver package.</summary>
public sealed record DriverPackageDecision(
    bool IsAllowedToLaunch,
    bool RequiresInteractiveUserConfirm,
    bool IsBiosOrFirmwarePackage,
    string VerdictCode,
    string Reason,
    string VerifiedSha256Hex,
    IReadOnlyList<string> ChecksPassed,
    IReadOnlyList<string> ChecksFailed);
