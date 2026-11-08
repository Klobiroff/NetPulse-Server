# История изменений

Изменения сервера, SDK плагинов и общей библиотеки протокола (`NetPulse.Common`). У Unity-пакета свой список: см. [журнал изменений Unity-пакета](https://github.com/Klobiroff/NetPulse-Unity/blob/main/Packages/com.netpulse.unity/CHANGELOG.md). Клиенту версии 0.4 нужен сервер версии 0.4.

## 0.4.0 (2026-11-08)

Шифрование транспорта и усиление сервера. Клиенту 0.4 нужен сервер 0.4. При значении по умолчанию `Encryption:Mode` = `Disabled` формат на проводе остаётся таким же, как в 0.3, и клиент 0.3 по-прежнему подключается.

Шифрование:

- Весь UDP-поток, включая заголовки, подтверждения и сообщения об отключении LiteNetLib, можно шифровать и аутентифицировать. Алгоритмы фиксированы: X25519 с закреплённым ключом сервера через шаблон Noise `NK` (`NKpsk0` с предварительно разделённым ключом), записи ChaCha20-Poly1305 и SHA-256/HKDF. Ничего не согласуется.
- `NetPulse:Encryption`: `Mode` (`Disabled`, `Optional`, `Required`), `PrivateKeyFile` или `NETPULSE_SERVER_PRIVATE_KEY`, `PreviousPrivateKeyFiles` для ротации ключей, `PreSharedKey` и `PreSharedKeyFile`, `RequireCookie`, `CookieLifetimeMs`, `RekeyAfterPackets`, `RekeyAfterMs`, `ReplayWindow`, `SessionIdleTimeoutMs`, `ReplaceIdleMs`, `HandshakeTimeoutMs` и `PaddingBlock`. В режиме `Optional` или `Required` сервер не запустится без корректного ключа.
- `NetPulse.Server keygen [--out файл] [--force]` создаёт файл приватного ключа и печатает публичный.
- Обмен cookie выполняется до любого обмена ключами, и каждый ответ рукопожатия короче запроса, поэтому сервер нельзя использовать для усиления трафика. Сессию с того же адреса можно заменить только после того, как она промолчала `ReplaceIdleMs`, причём старый пир отключается первым, так что новая сессия никогда не наследует аутентифицированного пира.
- `NetPulse.Common.Security`: управляемые X25519 и ChaCha20-Poly1305 с постоянным временем и без выделений памяти на пути пакета, рукопожатие Noise, cookie, окно повторов, защита записей со сменой ключей, ядра клиента и сервера. Они проверены по векторам RFC 7748, 8439 и 5869 и сравнением с BouncyCastle, BCL и независимой реализацией Noise, только в тестовых проектах.
- SDK плагинов: `AuthRequest.IsEncrypted` и `AuthRequest.ChannelBinding` (32-байтовый хэш рукопожатия), `IActor.IsEncrypted`.
- Метрики `netpulse.handshakes_ok`, `handshakes_failed`, `handshakes_rate_limited`, `cookies_sent`, `decrypt_failures`, `replays`, `rekeys` и `plaintext_rejected`.
- `NetPulse.TestClient` умеет шифровать: `--encrypt --server-public-key КЛЮЧ [--psk КЛЮЧ] [--padding N]`.

Лимиты (`NetPulse:Security`, `0` выключает лимит):

- На адрес: `MaxConnectionsPerIp`, `ConnectionAttemptsPerIpPerSecond`, `HandshakesPerIpPerSecond`; на весь сервер `MaxHandshakesPerSecond`. Они работают и без шифрования там, где применимы.
- На пира: `AuthDeadlineMs`, `MaxAuthAttempts`, token bucket `MaxMessagesPerSecond`/`MessageBurst` и `MaxBytesPerSecond`/`ByteBurst`, а также `MaxMessageBytes`. Нарушение отключает пира с причиной `RateLimited`, `AuthTimeout` или `TooManyAuthAttempts`.
- Размеры: `MaxUserIdLength`, `MaxNickNameLength`, `MaxAuthTokenLength`, `MaxRoomNameLength`, `MaxPropertiesPerRoom`, `MaxPropertyBytes`, `MaxCachedEventsPerRoom`, `MaxTargetActors`. Запрос сверх лимита отклоняется с `InvalidRequest`, а не обрезается. Управляющие символы в идентификаторах пользователей, никнеймах и именах комнат отклоняются, а в тексте для лога заменяются.
- `RequireRejoinToken`: сервер выдаёт случайный токен при каждом входе и требует его, когда возвращается неактивный игрок, так что другой клиент с тем же `UserId` место не займёт. Новое поле протокола `RejoinToken` (`ParamKey` 56).
- `NetReader` отказывается читать больше 4096 вложенных контейнеров в одном сообщении.
- Новые метрики `netpulse.peers_rate_limited`, `connections_rejected`, `auth_timeouts` и `limit_violations`.

Исправления:

- Клиент, вошедший в лобби, мог сразу после полного списка получить устаревшую дельту списка комнат. Теперь лобби рассылает накопленные дельты, прежде чем добавить подписчика.

Тесты: всего 292, из них 112 для Common, 34 для генераторов и 146 для сервера. Новые тесты включают сквозные прогоны по реальному UDP в режимах `Disabled`, `Optional` и `Required` через записывающий прокси с повторёнными, изменёнными, удержанными и потерянными датаграммами.

## 0.3.0 (2026-11-06)

Поддержка облегчённого Unity API и генератора исходного кода.

Протокол и `NetPulse.Common`:

- Новый код типа значения `Extended` (18) с `NetTypeRegistry.RegisterExtended`, `IsExtendedRegistered` и `UnregisterExtended`. Расширенное значение несёт идентификатор `ushort` и длину. Сервер или клиент, не знающий идентификатор, сохраняет значение как `OpaqueValue` и пересылает без изменений.
- В `NetWriter` и `NetReader` добавлены методы для `sbyte`, `ulong` и `char`.
- Новые события: `Message` (244) для типизированных сообщений и `SyncVarSet` (245) для sync-переменных мастера.
- Новое зарезервированное свойство комнаты `_scene` и `RoomPropertyKeys.Scene`.
- `RaiseEventRequest.ObjectFlags` с флагом `PersistAcrossScenes`.
- Ответ на вход содержит `TickRate`.

Сервер:

- `SyncVarSet` и изменения `_scene` принимаются только от мастер-клиента.
- Смена сцены удаляет сценовые объекты и объекты, не помеченные как постоянные, вместе с их кэшированными событиями.
- `NetPulse:TickRate` теперь отправляется и клиентам, которые используют его для сетевого тика.

SDK плагинов:

- `INetworkObject.IsPersistent`.

Инструменты и тесты:

- `NetPulse.Unity.Generators`: генератор исходного кода Roslyn и анализаторы от NP0001 до NP0011 для Unity-пакета, а также `tools/build-generators.ps1` для копирования собранной dll в пакет.
- 159 тестов: 41 для Common, 34 для генераторов и 84 для сервера.

## 0.2.0 (2026-11-06)

Первая версия в истории git.

- Асинхронные комнаты: каждая комната работает на своём файбере, который дожидается каждого элемента очереди, поэтому медленная комната не задерживает ни другие комнаты, ни лобби, ни сетевой поток.
- Хуки плагинов асинхронные (`ValueTask` с `CancellationToken`) и имеют тайм-аут (`NetPulse:Plugins:HookTimeoutMs`). Синхронные хуки и `Defer()` удалены. Хук, завершившийся без решения, считается `Fail`.
- API хоста: асинхронные таймеры на `PeriodicTimer`, `Enqueue`, `HttpAsync` и `RunInBackground`.
- Защита от флуда для каждого пира (`PeerQueueLimit`) с границей очереди каждой комнаты (`RoomQueueCapacity`) и причиной отключения `Overloaded`.
- Проверка владения сетевыми объектами на сервере с тремя режимами (`Fixed`, `Takeover`, `Request`), операциями `RequestOwnership` и `RegisterSceneObjects` и событием `OwnershipRequested`. Хук `OnBeforeTransferOwnershipAsync` позволяет плагинам накладывать вето на передачу.
- Собственная аутентификация через `IAuthPlugin` с `AuthTimeoutMs` и сервер, который отказывается запускаться, если настроенный плагин аутентификации не загрузился. Плагин `AuthSample` имеет режимы HMAC и HTTP, а тестовый клиент умеет создавать токены командой `--make-token`.
- Горячая перезагрузка плагинов с изолированными контекстами загрузки и консольная команда `reload`.
- Лобби с дельтами списка комнат, случайный вход с фильтрами по свойствам, время жизни игрока и пустой комнаты, обновление свойств через сравнение и замену (CAS), группы интереса и кэш событий.
- Метрики под метром `NetPulse` и консольные команды `stats`, `plugins`, `reload` и `quit`.
- `NetPulse.TestClient`, клиент-бот для проверок.

## 0.1

Первоначальный протокол, `NetPulse.Common`, лобби и комнаты и первые хуки плагинов. Эта версия появилась до истории git.
