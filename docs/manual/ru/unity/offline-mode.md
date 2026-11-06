# Офлайн-режим

В офлайн-режиме тот же API NetPulse работает с локальной комнатой в памяти. Сервера и сокета нет. Это позволяет одиночному режиму, обучению или тесту использовать тот же код, что и онлайн-игра.

Включайте его до подключения, пока состояние `Disconnected`:

```csharp
using NetPulse;
using NetPulse.Common;
using UnityEngine;

public sealed class StartGame : MonoBehaviour
{
    [SerializeField] private bool _singlePlayer;
    [SerializeField] private GameObject _playerPrefab;

    private async void Start()
    {
        Pulse.OfflineMode = _singlePlayer;
        await Pulse.Connect();
        await Pulse.JoinOrCreateRoom("main", new RoomOptions { MaxPlayers = 4 });
        Pulse.Instantiate(_playerPrefab, Vector3.zero, Quaternion.identity);
    }
}
```

Изменение `OfflineMode` в любом другом состоянии пишет ошибку в лог и ничего не делает. Чтобы переключиться, сначала вызовите `Pulse.Disconnect()`.

## Как API ведёт себя офлайн

| Область | Поведение |
|---|---|
| `Connect()` | Завершается сразу и поднимает `OnConnected`. Идентификатор пользователя берётся из настроек как обычно. |
| Лобби | `JoinLobby()` переводит в `InLobby`. `GetRoomList()` возвращает пустой список. |
| Комнаты | `CreateRoom`, `JoinRoom`, `JoinOrCreateRoom` и `JoinRandomRoom` создают локальную комнату (с именем `OfflineRoom`, если имя не передано). Локальный игрок получает актор 1 и становится мастером. Применяются лимиты `RoomOptions` и `CustomProperties`. Срабатывают `OnCreatedRoom` и `OnJoinedRoom`. |
| Свойства | Применяются локально и сразу. Compare-and-set ничего не сравнивает: новое значение просто записывается. |
| RPC, сообщения, `RaiseEvent` | Доставляются вашим собственным обработчикам, если получатели включают вас: `All`, `AllBuffered`, `MasterClient`, `Owner` или ваш собственный `Player`. `Others` и `OthersBuffered` не достигают никого. |
| Объекты | `Pulse.Instantiate` создаёт объект локально, реестр префабов не нужен. Состояние владельца никуда не отправляется, поэтому `OnSerialize` не вызывается. `SyncVar<T>` по-прежнему поднимает `Changed` для локальных записей. |
| Владение | `RequestOwnership()` сразу делает вас владельцем. Объекты `Fixed` по-прежнему отказывают. |
| Время и тики | `ServerTime` совпадает с локальными часами. Частота тиков равна 30, а `OnNetworkTick` работает как обычно. |
| Сцены | `Pulse.LoadScene` работает, потому что вы мастер. |
| Статистика | `RoundTripTimeMs` равно `0`, трафик не считается. |

Путь выполнения тот же, что и онлайн, поэтому ошибки игровой логики проявляются и офлайн. А вот всё, что зависит от второго игрока или от сети, офлайн не покажет: порядок между клиентами, борьбу за владение, интерполяцию чужих объектов.

## Советы

- Сведите разницу между онлайн- и офлайн-путями к одному `if` при старте, как выше. Всё после `Connect()` может остаться прежним.
- Если игра предлагает оба режима, дайте игроку выбрать, а при возвращении в меню вызывайте `Disconnect()` и переключайте `OfflineMode`.
- Поскольку офлайн владение всегда достаётся без отказа, проверяйте правила `Request` и `Fixed` ещё и на настоящем сервере.
