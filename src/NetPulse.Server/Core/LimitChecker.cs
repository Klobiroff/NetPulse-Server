using NetPulse.Common;
using NetPulse.Server.Configuration;

namespace NetPulse.Server.Core;

public sealed class LimitChecker
{
    [ThreadStatic]
    private static NetWriter? CachedWriter;

    private readonly SecurityOptions _limits;

    public LimitChecker(SecurityOptions limits) => _limits = limits ?? throw new ArgumentNullException(nameof(limits));

    public bool RequireRejoinToken => _limits.RequireRejoinToken;

    private static NetWriter Writer => CachedWriter ??= new NetWriter(256);

    public string? CheckRoomName(string? name)
    {
        if (name is null)
        {
            return null;
        }

        if (_limits.MaxRoomNameLength > 0 && name.Length > _limits.MaxRoomNameLength)
        {
            return "Room name is too long";
        }

        return LogText.ContainsControl(name) ? "Room name must not contain control characters" : null;
    }

    public string? CheckRoomOptions(RoomOptions? options) => options is null ? null : CheckProperties(options.CustomProperties, null);

    public string? CheckProperties(PropertyTable? delta, PropertyTable? existing)
    {
        if (delta is null)
        {
            return null;
        }

        if (_limits.MaxPropertyBytes > 0)
        {
            foreach (var pair in delta)
            {
                if (pair.Value is not null && SizeOf(pair.Value) > _limits.MaxPropertyBytes)
                {
                    return "A property value is too large";
                }
            }
        }

        if (_limits.MaxPropertiesPerRoom <= 0)
        {
            return null;
        }

        var projected = existing is null ? new PropertyTable() : new PropertyTable(existing);
        projected.Merge(delta);
        return projected.Count > _limits.MaxPropertiesPerRoom ? "Too many properties" : null;
    }

    public string? CheckEvent(RaiseEventRequest request, int cachedEvents)
    {
        if (_limits.MaxTargetActors > 0 && request.TargetActors is { } targets && targets.Length > _limits.MaxTargetActors)
        {
            return "Too many target actors";
        }

        var caches = request.Caching is CachingOption.AddToRoomCache or CachingOption.AddToRoomCacheGlobal;
        return caches && _limits.MaxCachedEventsPerRoom > 0 && cachedEvents >= _limits.MaxCachedEventsPerRoom ? "The room event cache is full" : null;
    }

    private static int SizeOf(object value)
    {
        var writer = Writer;
        writer.Reset();

        try
        {
            writer.WriteObject(value);
            return writer.Length;
        }
        catch (Exception ex) when (ex is NetSerializationException or ArgumentException or InvalidOperationException)
        {
            return int.MaxValue;
        }
    }
}
