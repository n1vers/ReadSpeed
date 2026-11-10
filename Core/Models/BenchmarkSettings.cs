namespace StorageBench.Core.Models;

public class BenchmarkSettings
{
    /// <summary>Размер буфера чтения в КБ.</summary>
    public int BufferSize { get; set; } = 1024;

    /// <summary>Количество независимых чтений одного источника.</summary>
    public int ThreadCount { get; set; } = 1;

    public string SourcePath { get; set; } = string.Empty;

    public bool DisableWindowsCache { get; set; } = true;

    public PauseController? PauseController { get; set; }
}
