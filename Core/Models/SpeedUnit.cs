namespace StorageBench.Core.Models;

public enum SpeedUnit
{
    MegabytesPerSecond,
    GigabytesPerSecond,
    MegabitsPerSecond,
    GigabitsPerSecond
}

public static class SpeedUnitFormatter
{
    public static double Convert(double megabytesPerSecond, SpeedUnit unit) => unit switch
    {
        SpeedUnit.GigabytesPerSecond => megabytesPerSecond / 1000,
        SpeedUnit.MegabitsPerSecond => megabytesPerSecond * 8,
        SpeedUnit.GigabitsPerSecond => megabytesPerSecond * 8 / 1000,
        _ => megabytesPerSecond
    };

    public static string GetLabel(SpeedUnit unit) => unit switch
    {
        SpeedUnit.GigabytesPerSecond => "GB/s",
        SpeedUnit.MegabitsPerSecond => "Mbps",
        SpeedUnit.GigabitsPerSecond => "Gbps",
        _ => "MB/s"
    };

    public static string Format(double megabytesPerSecond, SpeedUnit unit) =>
        $"{Convert(megabytesPerSecond, unit):F2} {GetLabel(unit)}";
}
