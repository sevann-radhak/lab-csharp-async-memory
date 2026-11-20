using System.ComponentModel;

namespace LabCsharpAsyncMemory.Api;

public sealed record BurstResult(
    [property: Description("await when the wait released the worker. GetResult when the wait held it.")]
    string Pattern,
    [property: Description("Workers held at once during the wait. The lab pool has two.")]
    int PeakThreads,
    [property: Description("Milliseconds from the start of the burst until every call finished.")]
    long ElapsedMs,
    [property: Description("How many waits ran.")]
    int Count);

public sealed record WorkResult(
    [property: Description("True when the handler finished the requested wait.")]
    bool Completed,
    [property: Description("True when the token was cancelled before the wait finished.")]
    bool Cancelled,
    [property: Description("Milliseconds the handler actually ran.")]
    long ElapsedMs);

public sealed record BufferResult(
    [property: Description("Buffers created and not yet disposed.")]
    int LiveBuffers);

public sealed record AllocationResult(
    [property: Description("Length of the text both approaches build.")]
    int Length,
    [property: Description("Bytes allocated on this thread while building the text.")]
    long AllocatedBytes);
