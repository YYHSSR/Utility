namespace Utility.Core.Models;

/// <summary>Installation scope of an inventoried software package.</summary>
public enum InstallScope
{
    Unknown = 0,
    MachineX64 = 1,
    MachineX86 = 2,
    CurrentUser = 3,
    StoreAppx = 4
}

/// <summary>Installed desktop or Store package read without touching Win32_Product.</summary>
public sealed record InstalledAppEntry(
    string StableId,
    string DisplayName,
    string Publisher,
    string Version,
    InstallScope Scope,
    string? PackageFamilyName,
    bool SupportsWindowsAppsDeepLink)
{
    public DesktopUninstallRegistration? DesktopRegistration { get; init; }
}

public sealed record DesktopUninstallRegistration(
    string RegistryKeyName, string? Command, string? InstallLocation, bool IsMsi, bool NoRemove,
    string? DisplayIcon = null);

/// <summary>Origin of a Windows startup entry.</summary>
public enum StartupEntrySource
{
    UserRunRegistry = 0,
    MachineRunRegistry = 1,
    UserStartupFolder = 2,
    MachineStartupFolder = 3,
    ScheduledTask = 4
}

/// <summary>Read-only Windows startup inventory entry.</summary>
public sealed record StartupEntry(
    string StableId,
    string Name,
    string CommandOrTarget,
    StartupEntrySource Source,
    bool? IsEnabled,
    string Location);

/// <summary>Classification for read-only Lenovo leftover scan results.</summary>
public enum LeftoverClassification
{
    ActiveInstalledComponent = 0,
    StaleAction = 1,
    CandidateLeftover = 2,
    SharedOrExcluded = 3
}

/// <summary>Kind of artifact inspected during a Lenovo leftover scan.</summary>
public enum LeftoverArtifactKind
{
    ScheduledTask = 0,
    AppxPackage = 1,
    WindowsService = 2,
    UninstallEntry = 3,
    Directory = 4
}

/// <summary>Single read-only Lenovo leftover scan finding.</summary>
public sealed record LenovoLeftoverFinding(
    string Name,
    LeftoverArtifactKind Kind,
    LeftoverClassification Classification,
    string Location,
    string Evidence,
    string CurrentState,
    string? RelatedMainApp,
    bool IsMainAppInstalled);

/// <summary>Aggregated software, startup, and on-demand Lenovo scan result.</summary>
public sealed record SoftwareInventorySnapshot(
    IReadOnlyList<InstalledAppEntry> InstalledApps,
    IReadOnlyList<StartupEntry> StartupEntries,
    IReadOnlyList<LenovoLeftoverFinding> LenovoFindings,
    DateTimeOffset CapturedAtUtc);
