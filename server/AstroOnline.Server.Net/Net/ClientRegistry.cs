using System.Collections.Concurrent;
using System.Net;

namespace AstroOnline.Server.Net.Net;

public sealed class ClientRegistry
{
    private long _nextId = 0;
    private readonly ConcurrentDictionary<IPEndPoint, ClientInfo> _byEndPoint = new();

    public int Count => _byEndPoint.Count;

    public bool Contains(IPEndPoint ep)
        => _byEndPoint.ContainsKey(ep);

    public ClientInfo GetOrAdd(IPEndPoint ep)
    {
        return _byEndPoint.GetOrAdd(ep, e =>
        {
            var id = (ulong)Interlocked.Increment(ref _nextId);
            return new ClientInfo(e, id)
            {
                LastSeenUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        });
    }

    public bool TryGet(IPEndPoint ep, out ClientInfo info)
        => _byEndPoint.TryGetValue(ep, out info!);

    public void Touch(IPEndPoint ep, long nowUnixMs)
    {
        if (_byEndPoint.TryGetValue(ep, out var info))
            info.LastSeenUnixMs = nowUnixMs;
    }

    public int EvictStale(long nowUnixMs, long timeoutMs)
    {
        int removed = 0;

        foreach (var kv in _byEndPoint)
        {
            if (nowUnixMs - kv.Value.LastSeenUnixMs >= timeoutMs)
            {
                if (_byEndPoint.TryRemove(kv.Key, out _))
                    removed++;
            }
        }

        return removed;
    }

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
