using StorageBench.Core.Models;

namespace StorageBench.Core.Interfaces;

public interface IReadEngine
{
    Task<BenchmarkResult> RunAsync(
        BenchmarkSettings settings,
        IProgress<ReadProgress>? progress,
        CancellationToken cancellationToken);
}
