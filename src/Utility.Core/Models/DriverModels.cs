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
    DriverCategory Category)
{
    public string? DeviceInstanceId { get; init; }
    public IReadOnlyList<string> HardwareIds { get; init; } = [];
    public bool IsHealthy { get; init; }
}

public sealed record LenovoDriverUpdate(string Id, string Title, string CurrentVersion, string NewVersion,
    string ReleaseDate, string ManifestHash);

public sealed record LenovoDriverScan(IReadOnlyList<LenovoDriverUpdate> Updates, int ComparedPackages, int FailedManifests);

public sealed record LenovoDriverInstallResult(bool Active, bool RestartRequired, int RemovedPackages, int RetainedPackages);
public sealed record LenovoDriverCleanupResult(int RemovedPackages, int PendingPackages);

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
