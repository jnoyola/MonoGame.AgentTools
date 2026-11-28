using System.Reflection;
using Microsoft.Xna.Framework.Graphics;

namespace Synthapixel.MonoGame.AgentTools.Diagnostics;

/// <summary>
/// Counts that grow when a game keeps creating things it doesn't release: live graphics resources and
/// process private memory. Private bytes is the standard leak counter on Windows (Performance Monitor's
/// Process\Private Bytes); live resources catch textures or render targets created every frame and never
/// disposed, which the managed heap barely shows.
/// </summary>
internal sealed class ResourceCounters : IDisposable
{
    /// <summary>How often process memory is read (and a memory-history point recorded).</summary>
    public const double PrivateBytesIntervalSeconds = 1.0;

    private readonly object? _resourcesLock;
    private readonly ICollection<WeakReference>? _resources;
    private readonly ProcessMemory _processMemory = new();
    private double _sampledAt = double.NegativeInfinity;

    public ResourceCounters(GraphicsDevice device)
    {
        // MonoGame tracks every GraphicsResource in a private set for device reset and disposal; there's no
        // public count. Missing in another version: reported as -1.
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        _resources = typeof(GraphicsDevice).GetField("_resources", flags)?.GetValue(device) as ICollection<WeakReference>;
        _resourcesLock = typeof(GraphicsDevice).GetField("_resourcesLock", flags)?.GetValue(device);
    }

    public long PrivateBytes { get; private set; }

    public int GraphicsResources()
    {
        if (_resources is null || _resourcesLock is null)
            return -1;
        lock (_resourcesLock)
            return _resources.Count;
    }

    /// <summary>Whether reading process memory allocates on this OS (only where no allocation-free API is used).</summary>
    public bool SamplingAllocates => _processMemory.Allocates;

    /// <summary>Refreshes <see cref="PrivateBytes"/> when it's due. Returns whether it did.</summary>
    public bool SamplePrivateBytes(double wallSeconds)
    {
        if (wallSeconds - _sampledAt < PrivateBytesIntervalSeconds)
            return false;
        _sampledAt = wallSeconds;
        PrivateBytes = _processMemory.Read();
        return true;
    }

    public void Dispose() => _processMemory.Dispose();
}
