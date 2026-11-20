using System.Runtime;

namespace MonoGame.AgentTools.Diagnostics;

/// <summary>
/// Snapshot of the runtime counters that explain frame costs. Reading it doesn't allocate. JIT counts
/// are split: the calling thread's compiles block the frame; all-thread compiles include tiered
/// re-compilation in the background, which changes code speed but doesn't block. Allocations are counted
/// for the game thread and for all threads; the difference is garbage made by other threads (audio,
/// tasks, libraries).
/// </summary>
internal readonly record struct RuntimeCounters(
    long AllocatedBytes,
    int Gen0,
    int Gen1,
    int Gen2,
    long JitMethods,
    TimeSpan JitTime,
    long JitMethodsAllThreads,
    long AllocatedBytesAllThreads)
{
    public static RuntimeCounters Read() => new(
        GC.GetAllocatedBytesForCurrentThread(),
        GC.CollectionCount(0),
        GC.CollectionCount(1),
        GC.CollectionCount(2),
        JitInfo.GetCompiledMethodCount(currentThread: true),
        JitInfo.GetCompilationTime(currentThread: true),
        JitInfo.GetCompiledMethodCount(currentThread: false),
        // Precise costs about 3 us; the approximate count moves in allocation-context steps of about 8 KB.
        GC.GetTotalAllocatedBytes(precise: true));

    public RuntimeCounters Since(in RuntimeCounters start) => new(
        AllocatedBytes - start.AllocatedBytes, Gen0 - start.Gen0, Gen1 - start.Gen1, Gen2 - start.Gen2,
        JitMethods - start.JitMethods, JitTime - start.JitTime, JitMethodsAllThreads - start.JitMethodsAllThreads,
        AllocatedBytesAllThreads - start.AllocatedBytesAllThreads);

    /// <summary>Bytes other threads allocated since <paramref name="start"/>.</summary>
    public long OtherThreadsAllocatedSince(in RuntimeCounters start) =>
        Math.Max(0, AllocatedBytesAllThreads - start.AllocatedBytesAllThreads - (AllocatedBytes - start.AllocatedBytes));
}
