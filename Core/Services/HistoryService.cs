using System.IO;
using System.Text.Json;
using StorageBench.Core.Models;

namespace StorageBench.Core.Services;

/// <summary>
/// Хранит историю проверок в %LOCALAPPDATA%\StorageBench\history.json.
/// </summary>
public sealed class HistoryService
{
    public const int MaxEntries = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public HistoryService()
    {
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StorageBench");

        FilePath = Path.Combine(folder, "history.json");
    }

    public string FilePath { get; }

    public List<BenchmarkResult> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new List<BenchmarkResult>();

            using FileStream stream = File.OpenRead(FilePath);

            return JsonSerializer.Deserialize<List<BenchmarkResult>>(stream, JsonOptions)
                   ?? new List<BenchmarkResult>();
        }
        catch (JsonException)
        {
            // Файл повреждён: сохраняем копию, чтобы следующая запись его не затёрла.
            TryBackupCorruptFile();
            return new List<BenchmarkResult>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<BenchmarkResult>();
        }
    }

    public void Save(IEnumerable<BenchmarkResult> items)
    {
        string? folder = Path.GetDirectoryName(FilePath);

        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);

        string json = JsonSerializer.Serialize(
            items.Take(MaxEntries).ToList(),
            JsonOptions);

        // Пишем во временный файл и подменяем, чтобы сбой не оставил полфайла.
        string tempPath = FilePath + ".tmp";

        File.WriteAllText(tempPath, json);
        File.Move(tempPath, FilePath, overwrite: true);
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            File.Copy(FilePath, FilePath + ".corrupt", overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
