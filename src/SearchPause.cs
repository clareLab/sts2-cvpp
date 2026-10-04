namespace cvpp;

internal sealed class SearchPause
{
    private readonly object _gate = new();
    private TaskCompletionSource? _resume;

    internal bool Paused { get { lock (_gate) return _resume != null; } }

    internal void Set(bool paused)
    {
        lock (_gate)
        {
            if (paused) _resume ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            else { _resume?.TrySetResult(); _resume = null; }
        }
    }

    internal async Task Wait(CancellationToken token)
    {
        Task? pending;
        lock (_gate) pending = _resume?.Task;
        if (pending == null) return;
        try { await pending.WaitAsync(TimeSpan.FromSeconds(1), token); }
        catch (TimeoutException) { }
    }
}
