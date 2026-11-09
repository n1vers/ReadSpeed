namespace StorageBench.Core.Models;

public sealed class PauseController
{
    private readonly object _sync = new();
    private TaskCompletionSource _resumeSource = CreateCompletedSource();
    private bool _isPaused;

    public bool IsPaused
    {
        get
        {
            lock (_sync)
                return _isPaused;
        }
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (_isPaused)
                return;

            _isPaused = true;
            _resumeSource = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        lock (_sync)
        {
            if (!_isPaused)
                return;

            _isPaused = false;
            _resumeSource.TrySetResult();
        }
    }

    public Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (!_isPaused)
                return Task.CompletedTask;

            return _resumeSource.Task.WaitAsync(cancellationToken);
        }
    }

    private static TaskCompletionSource CreateCompletedSource()
    {
        var source = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
