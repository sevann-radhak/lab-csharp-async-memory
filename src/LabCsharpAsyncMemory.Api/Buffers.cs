namespace LabCsharpAsyncMemory.Api;

public sealed class BufferTracker
{
    private int _live;

    public int Live => Volatile.Read(ref _live);

    public void Increment()
    {
        _ = Interlocked.Increment(ref _live);
    }

    public void Decrement()
    {
        _ = Interlocked.Decrement(ref _live);
    }
}

public sealed class ReportBuffer : IDisposable
{
    private readonly BufferTracker _tracker;
    private int _disposed;

    public ReportBuffer(BufferTracker tracker)
    {
        _tracker = tracker;
        tracker.Increment();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _tracker.Decrement();
        }
    }
}
