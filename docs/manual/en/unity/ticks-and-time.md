# Ticks and time

All clients in a room share a notion of time. The server's clock is the reference, and each client keeps an estimate of it. On top of that clock the server defines a tick rate, which gives you a fixed-step loop that runs at the same pace everywhere.

## Server time

`Pulse.ServerTime` is the estimated server clock in seconds, as a `double`. Two clients that read it at the same moment get values that differ only by the estimation error, so it is the right time base for things that must line up: countdowns, interpolation, cooldowns that start from a shared event.

How it is estimated: the connection handshake gives a first offset. After that the client sends a ping every two seconds, takes the round-trip time of each answer, keeps the last eight samples and uses the offset of the sample with the lowest round trip. `Pulse.RoundTripTimeMs` is the transport's round-trip time. Before you connect the value means nothing, and in [offline mode](offline-mode.md) it follows the local clock.

A shared countdown is a few lines: the master stores a start time in a room property, and every client computes the remainder from the same clock.

```csharp
using NetPulse;
using UnityEngine;

public sealed class Countdown : MonoBehaviour
{
    private static readonly RoomKey<double> StartAt = new("startAt");

    public void Begin(float seconds) => Pulse.CurrentRoom.Set(StartAt, Pulse.ServerTime + seconds);

    private void Update()
    {
        var room = Pulse.CurrentRoom;
        if (room == null || !room.Properties.ContainsKey(StartAt.Name))
        {
            return;
        }

        var remaining = room.Get(StartAt) - Pulse.ServerTime;
        if (remaining > 0d)
        {
            Debug.Log($"Starting in {remaining:F1} s");
        }
    }
}
```

Use `ServerTime` rather than `Time.time` for anything you compare between clients. `NetRpcInfo.ServerTime` is the server time on the receiving client when the RPC ran, not when it was sent. To measure travel time, put `Pulse.ServerTime` into the payload of the message or RPC.

## Ticks

The server announces a tick rate when you join a room (30 per second unless the server is configured differently). The client uses 30 until then, and in offline mode.

| Member | Meaning |
|---|---|
| `Pulse.TickRate` | Ticks per second |
| `Pulse.Tick` | The current tick number, `floor(ServerTime * TickRate)`, as a `long` |
| `Pulse.TickDelta` | The length of one tick in seconds, `1 / TickRate` |

Because the number comes from the shared clock, every client agrees on which tick is current.

## OnNetworkTick

Override `OnNetworkTick(long tick)` in a `NetworkBehaviour` to run code once per tick. It is called for every spawned object, whether you own it or not, so check `IsMine` before you act on input.

```csharp
using NetPulse;
using UnityEngine;

public sealed class TickMover : NetworkBehaviour
{
    [SerializeField, Min(0f)] private float _speed = 5f;

    private Vector3 _direction;

    public void SetDirection(Vector3 direction) => _direction = direction;

    public override void OnNetworkTick(long tick)
    {
        if (IsMine)
        {
            transform.position += _direction * (_speed * (float)Pulse.TickDelta);
        }
    }
}
```

What is guaranteed:

- `OnNetworkSpawned` runs before the first `OnNetworkTick` of an object ([lifecycle](lifecycle.md)).
- Ticks run from the NetPulse runner in `Update`, before your own `Update`.
- If a frame took longer than one tick, the loop catches up, but at most 5 ticks run in one frame. If the application stalled for more than a second, the older ticks are skipped and only the last 5 run.
- The first frame after joining a room only records the starting tick. The first `OnNetworkTick` comes in a later frame.
- An exception in one `OnNetworkTick` is logged and does not stop the other objects.

## How ticks drive sending

Object state is sent on tick boundaries, right after the `OnNetworkTick` calls of that tick, so a change you make in a tick goes out in the same tick. Not every tick sends. The number of ticks between sends comes from two settings ([settings and editor](settings-and-editor.md)):

```text
rate = max(1, min(SendRate, SerializationRate))
ticksPerSend = max(1, round(TickRate / rate))
```

A `SerializationRate` of `0` stands for the tick rate in this formula.

| Tick rate | Send Rate | Serialization Rate | Sends |
|---|---|---|---|
| 30 | 20 (default) | 0 (default) | Every 2nd tick, 15 per second |
| 30 | 30 | 0 | Every tick |
| 30 | 10 | 0 | Every 3rd tick, 10 per second |
| 30 | 60 | 15 | Every 2nd tick |

The rate applies to all objects. When a player joins, owners resend their full state on the next tick, regardless of the schedule.

## Interpolation and time

Remote objects are shown slightly in the past so that there are always two snapshots to blend. `NetworkTransform` renders at `ServerTime` minus a delay. Each snapshot carries the server time of the owner's send, which `NetStream.Timestamp` exposes if you write your own `OnSerialize`. See [components](components.md) for the delay setting.
