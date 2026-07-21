namespace NextRdp.Desktop;

internal sealed class OrderedAsyncQueue
{
    private readonly object gate = new();
    private Task tail = Task.CompletedTask;

    public Task Enqueue(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (gate)
        {
            tail = RunAfterAsync(tail, work);
            return tail;
        }
    }

    public Task WhenIdleAsync()
    {
        lock (gate) return tail;
    }

    private static async Task RunAfterAsync(Task predecessor, Func<Task> work)
    {
        try { await predecessor.ConfigureAwait(false); }
        catch { /* The next state must still be processed after an earlier state failure. */ }
        await work().ConfigureAwait(false);
    }
}
