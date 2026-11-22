using Utility.Core.Models;
using Utility.Core.Services;
using Xunit;

namespace Utility.Tests;

public sealed class LeftoverClassifierTests
{
    [Fact]
    public void ClassifiesSystemIdleMonitorMissingTargetAsStaleAction()
    {
        var finding = LeftoverClassifier.ClassifyScheduledTask(
            "SystemIdleMonitor",
            "\\",
            @"C:\Program Files (x86)\Lenovo\PCManager\5.1.170.2282\Modules\SE\plugins\SEHeadphonePlugin\PCIdleTriggerProcess64.exe",
            executeFileExists: false,
            "Ready",
            isPcManagerInstalled: true,
            isLegionZoneInstalled: true);

        Assert.Equal(LeftoverClassification.StaleAction, finding.Classification);
        Assert.True(finding.IsMainAppInstalled);
    }

    [Fact]
    public void DistinguishesActiveComponentsFromCandidateLeftoversAfterUninstall()
    {
        var active = LeftoverClassifier.ClassifyAppxPackage("LegionZoneExt", "LegionZoneExt_1.0.0.0", "1.0.0.0", true, true);
        Assert.Equal(LeftoverClassification.ActiveInstalledComponent, active.Classification);

        var leftoverPkg = LeftoverClassifier.ClassifyAppxPackage("A0PcmShellExtension", "A0PcmShellExtension_1.0.0.0", "1.0.0.0", false, true);
        Assert.Equal(LeftoverClassification.CandidateLeftover, leftoverPkg.Classification);

        var leftoverSvc = LeftoverClassifier.ClassifyService("LZService", "LZService", "Stopped", "Manual", true, false);
        Assert.Equal(LeftoverClassification.CandidateLeftover, leftoverSvc.Classification);

        var lightingActive = LeftoverClassifier.ClassifyAppxPackage(
            "Legion Lighting Controller", "LegionLightingController_1.0.0.0_x64__ra2g5j82mn5h8", "1.0.0.0", true, true);
        Assert.Equal(LeftoverClassification.ActiveInstalledComponent, lightingActive.Classification);

        var lightingLeftover = LeftoverClassifier.ClassifyAppxPackage(
            "Legion Lighting Controller", "LegionLightingController_1.0.0.0_x64__ra2g5j82mn5h8", "1.0.0.0", true, false);
        Assert.Equal(LeftoverClassification.CandidateLeftover, lightingLeftover.Classification);
    }

    [Fact]
    public void ExcludesSharedLenovoSystemDriversAndServices()
    {
        var fn = LeftoverClassifier.ClassifyService("LenovoFnAndFunctionKeys", "Lenovo Fn and function keys service", "Running", "Automatic", false, false);
        var imc = LeftoverClassifier.ClassifyService("ImControllerService", "System Interface Foundation Service", "Running", "Automatic", false, false);

        Assert.Equal(LeftoverClassification.SharedOrExcluded, fn.Classification);
        Assert.Equal(LeftoverClassification.SharedOrExcluded, imc.Classification);
    }
}
