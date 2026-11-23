using System.Collections.Concurrent;
using System.Threading.Channels;

namespace InteractionIntel.Api.Services;

/// <summary>
/// In-process publish/subscribe used to push real-time notifications to connected
/// SSE clients. Keyed by user id; each open SSE connection is one subscriber.
///
/// NOTE: this is in-memory and per-instance. With multiple container replicas an
/// event only reaches SSE clients connected to the SAME replica. The REST list
/// endpoint (backed by the ModuleJobs table) is the durable source of truth, so a
/// missed live event is always recoverable on the next fetch/reconnect.
/// </summary>
public class NotificationBroker
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<string>>> _subscribers = new();

    public (Guid Id, ChannelReader<string> Reader) Subscribe(string userId)
    {
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        var id = Guid.NewGuid();
        var map = _subscribers.GetOrAdd(userId, _ => new ConcurrentDictionary<Guid, Channel<string>>());
        map[id] = channel;
        return (id, channel.Reader);
    }

    public void Unsubscribe(string userId, Guid id)
    {
        if (_subscribers.TryGetValue(userId, out var map))
        {
            if (map.TryRemove(id, out var ch)) ch.Writer.TryComplete();
            if (map.IsEmpty) _subscribers.TryRemove(userId, out _);
        }
    }

    /// <summary>Push a payload to every live SSE connection for the given user.</summary>
    public void Publish(string userId, string payload)
    {
        if (string.IsNullOrEmpty(userId)) return;
        if (_subscribers.TryGetValue(userId, out var map))
        {
            foreach (var ch in map.Values)
                ch.Writer.TryWrite(payload);
        }
    }
}
