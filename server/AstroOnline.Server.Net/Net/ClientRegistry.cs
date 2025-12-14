using System.Collections.Concurrent;
using System.Net;

namespace AstroOnline.Server.Net.Net;

public sealed class ClientRegistry
{
    private long _nextId = 0;
    private readonly ConcurrentDictionary<IPEndPoint, ClientInfo> _byEndPoint = new();

    public ClientInfo GetOrAdd(IPEndPoint ep)
    {
        return _byEndPoint.GetOrAdd(ep, e =>
        {
            var id = (ulong)Interlocked.Increment(ref _nextId);
            var ci = new ClientInfo(e, id)
            {
                LastSeenUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            return ci;
        });
    }

    public bool TryGet(IPEndPoint ep, out ClientInfo info)
        => _byEndPoint.TryGetValue(ep, out info!);

    public void Touch(IPEndPoint ep, long nowUnixMs)
    {
        if (_byEndPoint.TryGetValue(ep, out var info))
            info.LastSeenUnixMs = nowUnixMs;
    }

    public bool TryRemove(IPEndPoint ep, out ClientInfo info)
        => _byEndPoint.TryRemove(ep, out info!);

    /// <summary>
    /// Removes all clients where (now - lastSeen) >= timeoutMs.
    /// Returns number removed.
    /// </summary>
    public int EvictStale(long nowUnixMs, long timeoutMs)
    {
        int removed = 0;

        foreach (var kv in _byEndPoint)
        {
            var info = kv.Value;
            if (nowUnixMs - info.LastSeenUnixMs >= timeoutMs)
            {
                if (_byEndPoint.TryRemove(kv.Key, out _))
                    removed++;
            }
        }

        return removed;
    }

    // Host uses this to broadcast snapshots.
    public ClientInfo[] SnapshotAll()
        => _byEndPoint.Values.ToArray();

    public sealed class ClientInfo
    {
        public IPEndPoint EndPoint { get; }
        public ulong ClientId { get; }
        public long LastSeenUnixMs { get; set; }

        public ClientInfo(IPEndPoint endPoint, ulong clientId)
        {
            EndPoint = endPoint;
            ClientId = clientId;
        }
    }
}
