namespace Utility.Core.Models;

public sealed record LenovoFanStage(
    int Stage, int SpeedLevel, int CpuTemperatureC, int GpuTemperatureC,
    int? AuxiliaryCpuTemperatureC, int CpuRpm, int GpuRpm);

public sealed record LenovoFanCapture(string Source, IReadOnlyList<LenovoFanStage> Stages)
{
    public static bool AreWritableLevels(IReadOnlyList<int>? levels) =>
        levels is { Count: 10 } &&
        levels.All(level => level is >= 1 and <= 10) &&
        levels.Zip(levels.Skip(1)).All(pair => pair.First <= pair.Second);

    public static LenovoFanCapture OwnerReference { get; } = new(
        "Daily profile: screenshots and current power and fan read-back on Lenovo 83JJ / PTCN18WW",
        Array.AsReadOnly<LenovoFanStage>(
        [
            new(1, 1, 63, 48, 34, 1900, 1800),
            new(2, 2, 63, 48, 38, 2100, 2000),
            new(3, 3, 63, 48, 41, 2300, 2200),
            new(4, 4, 63, 48, 49, 2500, 2500),
            new(5, 5, 65, 51, null, 2800, 2800),
            new(6, 6, 68, 54, null, 3100, 3000),
            new(7, 7, 70, 57, null, 3200, 3300),
            new(8, 8, 73, 66, null, 3900, 3900),
            new(9, 9, 77, 69, null, 4300, 4400),
            new(10, 9, 87, 75, null, 4300, 4400)
        ]));

    public static LenovoFanCapture? From83JJTables(
        IReadOnlyList<int> levels,
        IReadOnlyList<FanCurvePoint> cpu,
        IReadOnlyList<FanCurvePoint> gpu,
        IReadOnlyList<FanCurvePoint> auxiliary)
    {
        const int count = 10;
        if (levels.Count != count || cpu.Count != count || gpu.Count != count || auxiliary.Count != count ||
            levels.Any(level => level < 1 || level > count))
        {
            return null;
        }

        var stages = new LenovoFanStage[count];
        for (var index = 0; index < count; index++)
        {
            var level = levels[index];
            var aux = auxiliary[index].TemperatureC;
            stages[index] = new LenovoFanStage(
                index + 1, level, cpu[index].TemperatureC, gpu[index].TemperatureC,
                aux == 127 ? null : aux,
                cpu[level - 1].FanSpeedValue, gpu[level - 1].FanSpeedValue);
        }

        return new LenovoFanCapture("83JJ live Lenovo WMI fan table", Array.AsReadOnly(stages));
    }
}
