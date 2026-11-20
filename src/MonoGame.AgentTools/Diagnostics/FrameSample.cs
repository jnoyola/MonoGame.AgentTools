using Microsoft.Xna.Framework.Graphics;

namespace MonoGame.AgentTools.Diagnostics;

/// <summary>Everything measured for one frame.</summary>
internal struct FrameSample
{
    public int Frame;
    /// <summary>Game time at the frame (capture mode); NaN in live mode.</summary>
    public double SimSeconds;
    /// <summary>Wall time since the measurement started.</summary>
    public double WallSeconds;
    /// <summary>Capture: CPU time of Update + Draw. Live: wall time since the previous frame.</summary>
    public double FrameMs;
    /// <summary>Capture only; NaN in live mode.</summary>
    public double UpdateMs;
    /// <summary>Capture only; NaN in live mode.</summary>
    public double DrawMs;
    /// <summary>Bytes allocated by the game thread during the frame.</summary>
    public long AllocatedBytes;
    /// <summary>Bytes allocated by other threads since the previous frame.</summary>
    public long OtherThreadsAllocatedBytes;
    /// <summary>
    /// Capture only: bytes the capture itself allocated on the game thread after the frame (reading it
    /// back and saving it). Not the game's, but it's garbage on the same heap, so heap growth includes it.
    /// </summary>
    public long CaptureAllocatedBytes;
    public int Gen0Collections;
    public int Gen1Collections;
    public int Gen2Collections;
    public long HeapBytes;
    /// <summary>Methods compiled on the game thread during the frame (each one blocks the frame).</summary>
    public int JitMethods;
    public double JitMs;
    /// <summary>Methods compiled on any thread since the previous frame, including background tiering.</summary>
    public int JitMethodsAllThreads;
    public GraphicsMetrics Graphics;
    /// <summary>Graphics resources (textures, targets, buffers, effects) created and not yet disposed; -1 if unknown.</summary>
    public int GraphicsResources;
    /// <summary>Process private memory, sampled once a second (managed heap, native allocations, driver memory).</summary>
    public long PrivateBytes;

    /// <summary>Whether the frame did any work that a stable steady state shouldn't.</summary>
    public readonly bool AllocatedOrCollected => AllocatedBytes > 0 || Gen0Collections > 0;

    public static FrameSample Between(in RuntimeCounters before, in RuntimeCounters after) => new()
    {
        AllocatedBytes = after.AllocatedBytes - before.AllocatedBytes,
        Gen0Collections = after.Gen0 - before.Gen0,
        Gen1Collections = after.Gen1 - before.Gen1,
        Gen2Collections = after.Gen2 - before.Gen2,
        JitMethods = (int)(after.JitMethods - before.JitMethods),
        JitMs = (after.JitTime - before.JitTime).TotalMilliseconds,
        JitMethodsAllThreads = (int)(after.JitMethodsAllThreads - before.JitMethodsAllThreads),
    };
}
