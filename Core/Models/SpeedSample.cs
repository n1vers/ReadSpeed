namespace StorageBench.Core.Models;

/// <summary>Одна точка графика: скорость в момент времени от начала теста.</summary>
public class SpeedSample
{
    public double TimeSeconds { get; set; }

    public double SpeedMBps { get; set; }

    public SpeedSample()
    {
    }

    public SpeedSample(double timeSeconds, double speedMBps)
    {
        TimeSeconds = timeSeconds;
        SpeedMBps = speedMBps;
    }
}
