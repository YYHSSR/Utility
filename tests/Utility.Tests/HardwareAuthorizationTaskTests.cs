using Utility.Core.Models;
using Utility.Platform.Lenovo;
using Xunit;

namespace Utility.Tests;

public sealed class HardwareAuthorizationTaskTests
{
    [Fact]
    public void PreauthorizedTaskAllowsOnlyTheSelectedFirmwareOperations()
    {
        var allowed = Enum.GetValues<ElevatedOperation>().Where(HardwareAuthorizationTask.Allows);

        Assert.Equal(new[]
        {
            ElevatedOperation.ReadThermalSnapshot,
            ElevatedOperation.ReadFanTables,
            ElevatedOperation.ReadGraphicsMode,
            ElevatedOperation.ApplyOwnerPowerPreset,
            ElevatedOperation.SetGraphicsMode,
            ElevatedOperation.SetFanSpeedLevels
        }, allowed);
    }
}
