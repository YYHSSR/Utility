using Utility.Core.Models;

namespace Utility.Core.Services;

/// <summary>
/// Read-only classifier for Lenovo PC Manager, Legion Zone, and shared Lenovo components.
/// Never deletes files, registry keys, packages, services, or drivers.
/// </summary>
public static class LeftoverClassifier
{
    public const string PcManagerMainApp = "Lenovo PC Manager";
    public const string LegionZoneMainApp = "Legion Zone";

    private static readonly HashSet<string> SharedSystemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "LenovoFnAndFunctionKeys",
        "ImControllerService",
        "Lenovo ACPI-Compliant Virtual Power Controller",
        "Lenovo Fn and function keys",
        "System Interface Foundation Service",
        "LenovoPcDriver"
    };

    public static LenovoLeftoverFinding ClassifyScheduledTask(
        string taskName,
        string taskPath,
        string? executePath,
        bool executeFileExists,
        string taskState,
        bool isPcManagerInstalled,
        bool isLegionZoneInstalled)
    {
        var fullName = string.IsNullOrWhiteSpace(taskPath) ? taskName : $"{taskPath}{taskName}";
        var combined = $"{fullName} {executePath}";

        if (IsSharedComponent(combined))
        {
            return new LenovoLeftoverFinding(
                taskName,
                LeftoverArtifactKind.ScheduledTask,
                LeftoverClassification.SharedOrExcluded,
                fullName,
                $"Shared Lenovo component target: {executePath ?? "(none)"}",
                taskState,
                null,
                true);
        }

        var (mainApp, mainInstalled) = InferOwnerApp(combined, isPcManagerInstalled, isLegionZoneInstalled);

        if (!string.IsNullOrWhiteSpace(executePath) && !executeFileExists)
        {
            return new LenovoLeftoverFinding(
                taskName,
                LeftoverArtifactKind.ScheduledTask,
                LeftoverClassification.StaleAction,
                fullName,
                $"Scheduled task points to missing executable: {executePath}",
                $"{taskState} (target missing)",
                mainApp,
                mainInstalled);
        }

        var classification = mainApp is null
            ? LeftoverClassification.SharedOrExcluded
            : mainInstalled
                ? LeftoverClassification.ActiveInstalledComponent
                : LeftoverClassification.CandidateLeftover;

        return new LenovoLeftoverFinding(
            taskName,
            LeftoverArtifactKind.ScheduledTask,
            classification,
            fullName,
            $"Action target: {executePath ?? "(unspecified)"}",
            taskState,
            mainApp,
            mainInstalled);
    }

    public static LenovoLeftoverFinding ClassifyAppxPackage(
        string packageName,
        string packageFullName,
        string version,
        bool isPcManagerInstalled,
        bool isLegionZoneInstalled)
    {
        var (mainApp, mainInstalled) = InferOwnerApp($"{packageName} {packageFullName}", isPcManagerInstalled, isLegionZoneInstalled);
        var classification = mainApp is null
            ? LeftoverClassification.SharedOrExcluded
            : mainInstalled
                ? LeftoverClassification.ActiveInstalledComponent
                : LeftoverClassification.CandidateLeftover;

        return new LenovoLeftoverFinding(
            packageName,
            LeftoverArtifactKind.AppxPackage,
            classification,
            packageFullName,
            $"Current-user AppX package v{version}",
            mainInstalled ? "Installed (owner app present)" : "Installed (owner app absent)",
            mainApp,
            mainInstalled);
    }

    public static LenovoLeftoverFinding ClassifyService(
        string serviceName,
        string displayName,
        string status,
        string startType,
        bool isPcManagerInstalled,
        bool isLegionZoneInstalled)
    {
        if (IsSharedComponent(serviceName) || IsSharedComponent(displayName))
        {
            return new LenovoLeftoverFinding(
                serviceName,
                LeftoverArtifactKind.WindowsService,
                LeftoverClassification.SharedOrExcluded,
                $@"HKLM\SYSTEM\CurrentControlSet\Services\{serviceName}",
                $"Shared Lenovo/system service ({displayName}); excluded from cleanup suggestions",
                $"{status} / {startType}",
                null,
                true);
        }

        var (mainApp, mainInstalled) = InferOwnerApp($"{serviceName} {displayName}", isPcManagerInstalled, isLegionZoneInstalled);
        var classification = mainApp is null
            ? LeftoverClassification.SharedOrExcluded
            : mainInstalled
                ? LeftoverClassification.ActiveInstalledComponent
                : LeftoverClassification.CandidateLeftover;

        return new LenovoLeftoverFinding(
            serviceName,
            LeftoverArtifactKind.WindowsService,
            classification,
            $@"HKLM\SYSTEM\CurrentControlSet\Services\{serviceName}",
            $"Service display name: {displayName}",
            $"{status} / {startType}",
            mainApp,
            mainInstalled);
    }

    private static bool IsSharedComponent(string text)
    {
        foreach (var shared in SharedSystemNames)
        {
            if (text.Contains(shared, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static (string? MainApp, bool IsInstalled) InferOwnerApp(
        string text,
        bool isPcManagerInstalled,
        bool isLegionZoneInstalled)
    {
        if (text.Contains("A0Pcm", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("PCManager", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("LenovoPcManager", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("SystemIdleMonitor", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("PCIdleTriggerProcess", StringComparison.OrdinalIgnoreCase))
        {
            return (PcManagerMainApp, isPcManagerInstalled);
        }

        if (text.Contains("LegionZone", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("LegionLightingController", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("LZService", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("GAService", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Gaming AI", StringComparison.OrdinalIgnoreCase))
        {
            return (LegionZoneMainApp, isLegionZoneInstalled);
        }

        return (null, true);
    }
}
