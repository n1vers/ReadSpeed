
namespace StorageBench.Core.Models;

public class ReadProgress
{
	public long BytesRead { get; set; }

	public double CurrentSpeedMBps { get; set; }

	public double AverageSpeedMBps { get; set; }

	public TimeSpan Elapsed { get; set; }

	public bool IsCompleted { get; set; }
}
