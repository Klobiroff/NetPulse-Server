# Components

Three ready-made components cover the common cases: `NetworkTransform`, `NetworkRigidbody` and `NetworkAnimator`. Each one is a `NetworkBehaviour`, so the GameObject also needs a `NetworkObject` on its root. They are found under `Add Component > NetPulse`. All of them send state from the owner and apply it on everyone else, using `OnSerialize` underneath ([state sync](state-sync.md)).

## NetworkTransform

Synchronizes position, rotation and scale, and smooths the motion on remote clients by interpolation. Only one per object.

Sync:

| Option | Default | Meaning |
|---|---|---|
| Sync Position | on | Send and apply the position |
| Sync Rotation | on | Send and apply the rotation |
| Sync Scale | off | Send and apply the local scale |
| Use Local Space | on | Use `localPosition` and `localRotation`. Turn it off to sync world space. For a root object both are the same. |
| Reliable | off | Off sends with `UnreliableSequenced`: old packets are dropped and a loss does not stall anything. On sends `ReliableOrdered`. |

Send thresholds (owner side):

| Option | Default | Meaning |
|---|---|---|
| Position Threshold | 0.001 | A move smaller than this distance is not sent. It also applies to scale. |
| Rotation Threshold | 0.1 | A rotation smaller than this angle in degrees is not sent |

Receive (other clients):

| Option | Default | Meaning |
|---|---|---|
| Interpolation Delay | 0 | Seconds to render in the past. `0` means automatic, see below. |
| Extrapolate | off | When no newer snapshot has arrived, keep moving along the last velocity |
| Max Extrapolation Time | 0.25 | Longest extrapolation in seconds |
| Teleport Distance | 0 | If the gap between the current and the target position exceeds this, the object snaps to the target and the buffer is cleared. `0` disables the check. |

Remote clients keep a buffer of timestamped snapshots and show the object at `ServerTime - delay`, blending between the two snapshots around that time. Snapshots that are older than the last one are discarded. After an ownership change the buffer is cleared.

The automatic delay is `2 / max(1, min(Send Rate, Serialization Rate))` seconds. With the default `Serialization Rate` of `0` the formula evaluates to 2 seconds, which is a long lag. Set `Interpolation Delay` explicitly to about two send intervals: for example 0.13 s when the object is sent 15 times per second ([ticks and time](ticks-and-time.md#how-ticks-drive-sending)).

## NetworkRigidbody

Synchronizes a `Rigidbody` and requires one on the same object. It always uses `UnreliableSequenced` delivery.

| Option | Default | Meaning |
|---|---|---|
| Sync Velocity | on | Also send linear and angular velocity, which improves extrapolation |
| Interpolation Delay | 0 | Seconds to render in the past. `0` means automatic: `2 / max(1, Serialization Rate)`, which is 2 seconds with the default `Serialization Rate` of `0`. Set a value of your own. |
| Teleport Distance | 5 | A gap larger than this snaps the body to the target. `0` disables it. |

On the owner the body behaves normally. On other clients NetPulse makes it kinematic (clearing its velocity first) and moves it with `MovePosition` and `MoveRotation` in `FixedUpdate`, extrapolating up to a quarter of a second when snapshots run late. When ownership changes, the new owner gets back the `Is Kinematic` value the prefab had, and the old owner turns kinematic. Pair the component with a `Takeover` or `Request` [ownership mode](ownership.md) if players should be able to pick the object up and throw it.

Use either `NetworkTransform` or `NetworkRigidbody` to move an object, not both.

## NetworkAnimator

Synchronizes the parameters of an `Animator`, so remote copies play the same transitions.

| Option | Default | Meaning |
|---|---|---|
| Animator | auto | The `Animator` to sync. If empty, the component on the same GameObject is used. |
| Sync Layer Weights | off | Also sync the weights of layers 1 and up |

- `bool`, `int` and `float` parameters are streamed with the object's state. The owner reads them and everyone else sets them.
- Triggers are not part of the stream. Call `networkAnimator.SetTrigger("Attack")` on the owner. It fires the trigger locally and sends an RPC that fires it on every other client. Calls from a non-owner are ignored. `ResetTrigger` is local only.
- The parameter list is read from the controller in `Awake`, so assign the controller before the object is created.

```csharp
using NetPulse;
using UnityEngine;

public sealed class AttackAnimation : MonoBehaviour
{
    [SerializeField] private NetworkAnimator _networkAnimator;
    [SerializeField] private Animator _animator;

    public void Attack(float speed)
    {
        _animator.SetFloat("Speed", speed);
        _networkAnimator.SetTrigger("Attack");
    }
}
```

## Build your own

For other cases, write a `NetworkBehaviour` with `SyncVar<T>` or `OnSerialize` ([state sync](state-sync.md)). The three components above are ordinary scripts of that kind, and reading `NetworkTransform` is a good starting point for a custom interpolator.
