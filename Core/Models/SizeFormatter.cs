namespace StorageBench.Core.Models;

public static class SizeFormatter
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public static string ToText(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";

        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:F2} {Units[unit]}";
    }
}
