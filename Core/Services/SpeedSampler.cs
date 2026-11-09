using System.Diagnostics;
using StorageBench.Core.Models;

namespace StorageBench.Core.Services;

/// <summary>
/// Общий подсчёт скорости для всех движков: считает байты, раз в 250 мс
/// фиксирует точку графика, отправляет прогресс и в конце собирает результат.
/// </summary>
public sealed class SpeedSampler
{
    private const int IntervalMs = 250;
    private const double MinTailSeconds = 0.1;
    private const double MegaByte = 1024.0 * 1024.0;

    private readonly IProgress<ReadProgress>? _progress;
    private readonly Stopwatch _total = Stopwatch.StartNew();
    private readonly Stopwatch _interval = Stopwatch.StartNew();
    private readonly List<SpeedSample> _samples = new();

    private long _totalBytes;
    private long _intervalBytes;
    private double _min = double.MaxValue;
    private double _max;

    public SpeedSampler(IProgress<ReadProgress>? progress)
    {
        _progress = progress;
        StartedAt = DateTime.Now;
    }

    public DateTime StartedAt { get; }

    public long TotalBytes => _totalBytes;

    public bool IsTimeUp(TimeSpan limit) => _total.Elapsed >= limit;

    public void AddBytes(long bytes)
    {
        _totalBytes += bytes;
        _intervalBytes += bytes;

        if (_interval.ElapsedMilliseconds >= IntervalMs)
            CloseInterval();
    }

    private void CloseInterval()
    {
        double seconds = _interval.Elapsed.TotalSeconds;

        if (seconds <= 0)
            return;

        double current = _intervalBytes / seconds / MegaByte;

        _min = Math.Min(_min, current);
        _max = Math.Max(_max, current);

        TimeSpan elapsed = _total.Elapsed;

        _samples.Add(new SpeedSample(
            Math.Round(elapsed.TotalSeconds, 2),
            Math.Round(current, 2)));

        _progress?.Report(new ReadProgress
        {
            BytesRead = _totalBytes,
            CurrentSpeedMBps = current,
            AverageSpeedMBps = AverageSpeed(elapsed),
            Elapsed = elapsed,
            IsCompleted = false
        });

        _intervalBytes = 0;
        _interval.Restart();
    }

    private double AverageSpeed(TimeSpan elapsed) =>
        elapsed.TotalSeconds > 0
            ? _totalBytes / elapsed.TotalSeconds / MegaByte
            : 0;

    /// <summary>
    /// Останавливает замер и возвращает результат со статистикой и точками графика.
    /// Остальные поля (источник, тип, размер файла...) заполняет движок.
    /// </summary>
    public BenchmarkResult Finish()
    {
        _total.Stop();

        // Хвост последнего неполного интервала учитываем, только если он не слишком короткий,
        // иначе получаются ложные выбросы скорости.
        double tailSeconds = _interval.Elapsed.TotalSeconds;

        if (_intervalBytes > 0 && tailSeconds >= MinTailSeconds)
        {
            double tailSpeed = _intervalBytes / tailSeconds / MegaByte;

            _min = Math.Min(_min, tailSpeed);
            _max = Math.Max(_max, tailSpeed);

            _samples.Add(new SpeedSample(
                Math.Round(_total.Elapsed.TotalSeconds, 2),
                Math.Round(tailSpeed, 2)));
        }

        double average = AverageSpeed(_total.Elapsed);

        // Очень быстрый тест (меньше одного интервала) — одна точка со средней скоростью,
        // чтобы у записи в истории был хоть какой-то график.
        if (_samples.Count == 0 && _totalBytes > 0)
        {
            _samples.Add(new SpeedSample(
                Math.Round(Math.Max(_total.Elapsed.TotalSeconds, 0.01), 2),
                Math.Round(average, 2)));
        }

        if (_min == double.MaxValue)
            _min = average;

        if (_max == 0)
            _max = average;

        _progress?.Report(new ReadProgress
        {
            BytesRead = _totalBytes,
            CurrentSpeedMBps = average,
            AverageSpeedMBps = average,
            Elapsed = _total.Elapsed,
            IsCompleted = true
        });

        return new BenchmarkResult
        {
            AverageSpeedMBps = average,
            MinimumSpeedMBps = _min,
            MaximumSpeedMBps = _max,
            TotalBytesRead = _totalBytes,
            Duration = _total.Elapsed,
            StartedAt = StartedAt,
            Samples = _samples
        };
    }
}
