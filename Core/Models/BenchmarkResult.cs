using System.IO;
using System.Text.Json.Serialization;

namespace StorageBench.Core.Models;

public class BenchmarkResult
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Source { get; set; } = string.Empty;

    /// <summary>Local / Network / Internet.</summary>
    public string SourceKind { get; set; } = string.Empty;

    public string TestType { get; set; } = string.Empty;

    public double AverageSpeedMBps { get; set; }

    public double MinimumSpeedMBps { get; set; }

    public double MaximumSpeedMBps { get; set; }

    public long TotalBytesRead { get; set; }

    /// <summary>Полный размер источника, если он известен.</summary>
    public long? FileSizeBytes { get; set; }

    /// <summary>Размер буфера чтения в КБ.</summary>
    public int BufferSizeKB { get; set; }

    /// <summary>Устаревшее поле для совместимости со старыми записями истории.</summary>
    public int TimeLimitSeconds { get; set; }

    /// <summary>true — источник прочитан до конца.</summary>
    public bool ReachedEnd { get; set; }

    public TimeSpan Duration { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public string Status { get; set; } = string.Empty;

    public string ErrorText { get; set; } = string.Empty;

    /// <summary>Точки графика скорости (каждые ~250 мс).</summary>
    public List<SpeedSample> Samples { get; set; } = new();

    // ---- Только для отображения, в JSON не пишутся ----

    [JsonIgnore]
    public string SourceKindText => SourceKind switch
    {
        "Local" => "Локальный диск",
        "Network" => "Сеть",
        "Internet" => "Интернет",
        _ => "—"
    };

    [JsonIgnore]
    public string SourceName
    {
        get
        {
            if (Uri.TryCreate(Source, UriKind.Absolute, out Uri? uri) &&
                (uri.Scheme == Uri.UriSchemeHttp ||
                 uri.Scheme == Uri.UriSchemeHttps))
            {
                string last = uri.Segments.LastOrDefault()?.Trim('/') ?? string.Empty;

                return string.IsNullOrEmpty(last)
                    ? uri.Host
                    : $"{uri.Host} · {Uri.UnescapeDataString(last)}";
            }

            string name = Path.GetFileName(Source);

            return string.IsNullOrEmpty(name) ? Source : name;
        }
    }

    [JsonIgnore]
    public string TotalReadText => SizeFormatter.ToText(TotalBytesRead);

    [JsonIgnore]
    public string FileSizeText =>
        FileSizeBytes.HasValue ? SizeFormatter.ToText(FileSizeBytes.Value) : "неизвестен";

    [JsonIgnore]
    public string DurationText => Duration.ToString(@"mm\:ss\.f");

    [JsonIgnore]
    public string StatusText => string.IsNullOrWhiteSpace(Status)
        ? (ReachedEnd ? "OK" : "—")
        : Status;

    [JsonIgnore]
    public string CompletedAtText => CompletedAt?.ToString("dd.MM.yyyy HH:mm:ss") ?? "—";
}
