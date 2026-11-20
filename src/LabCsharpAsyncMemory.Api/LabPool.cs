using System.Diagnostics;

namespace LabCsharpAsyncMemory.Api;

public sealed class LabPool
{
    public const int WorkerCap = 2;
    public const int DelayMilliseconds = 150;

    private readonly SemaphoreSlim _workers = new(WorkerCap, WorkerCap);
    private readonly SemaphoreSlim _oneBurst = new(1, 1);
    private int _inUse;
    private int _peak;

    public async Task<BurstResult> RunAsync(string mode, int count, CancellationToken cancellationToken)
    {
        await _oneBurst.WaitAsync(cancellationToken);
        try
        {
            _ = Interlocked.Exchange(ref _peak, 0);
            Stopwatch clock = Stopwatch.StartNew();

            if (mode == "block")
            {
                Task[] tasks = new Task[count];
                for (int index = 0; index < count; index++)
                {
                    tasks[index] = Task.Run(Block, cancellationToken);
                }

                await Task.WhenAll(tasks);
                return new BurstResult("GetResult", Volatile.Read(ref _peak), clock.ElapsedMilliseconds, count);
            }

            Task[] awaited = new Task[count];
            for (int index = 0; index < count; index++)
            {
                awaited[index] = Task.Delay(DelayMilliseconds, cancellationToken);
            }

            await Task.WhenAll(awaited);
            return new BurstResult("await", Volatile.Read(ref _peak), clock.ElapsedMilliseconds, count);
        }
        finally
        {
            _ = _oneBurst.Release();
        }
    }

    private void Block()
    {
        _workers.Wait();
        int now = Interlocked.Increment(ref _inUse);
        int observed = Volatile.Read(ref _peak);
        while (now > observed && Interlocked.CompareExchange(ref _peak, now, observed) != observed)
        {
            observed = Volatile.Read(ref _peak);
        }

        try
        {
            Task.Delay(DelayMilliseconds).GetAwaiter().GetResult();
        }
        finally
        {
            _ = Interlocked.Decrement(ref _inUse);
            _ = _workers.Release();
        }
    }
}
