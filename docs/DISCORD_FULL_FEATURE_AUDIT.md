# Полный разбор Discord (Desktop 2025–2026) и честная сверка с FluxCord P2P v2.2.0

> [!NOTE]
> Документ содержит: (1) полный каталог всех подсистем и фишек современного Discord Desktop с техническим описанием их реализации; (2) честную построчную сверку с реальным кодом `FluxCord P2P v2.2.0` (`C# .NET 10 NativeAOT` + `C++20 AVX2` + `Go Jetson Orin Hub`) с оценкой достоверности в `%`; (3) список выявленных заглушек/расхождений и приоритетную дорожную карту.

---

## Сводная таблица достоверности по подсистемам

| # | Подсистема | Вес для приватного P2P | Готовность `FluxCord P2P v2.2.0` | Честная оценка |
|---|---|---|---|---|
| 1 | **Голос и аудио DSP** | Критично | **78%** | Opus 48кГц, WASAPI, Jitter/FEC/PLC, 6 пресетов UI (но DFNet3/NvAFX — fallback на MMSE), нет AEC и Soundboard |
| 2 | **Стриминг экрана и видео (Go Live)** | Критично | **74%** | 4K60/120fps NVENC Zero-Copy, 4 слота просмотра, Process Loopback, **уникальный 2-Экрана 32:9 GPU**, но нет вебки, AV1, HDR10, клипов |
| 3 | **Сеть, транспорт и шифрование (E2EE)** | Критично | **90%** | Прямой 0-hop UDP P2P + Jetson SFU + AES-256-GCM MQTT Rendezvous + XChaCha20-Poly1305; `libdave` MLS встроен, но выключен в P2P (`ver=0`) |
| 4 | **Текстовый чат и сообщения** | Высокий | **52%** | База (ответы/редактирование/удаление/пины/реакции/файлы/Ctrl+V) есть; нет инлайн-Markdown, тредов, опросов, ГС, пагинации >80 сообщений |
| 5 | **Серверы, каналы, ЛС и роли** | Средний | **32%** | Один Jetson-хаб (`g_p2p`), создание текстовых/голосовых каналов и 1-на-1 ЛС; нет мульти-серверов, ролей/прав, групповых ЛС, форумов |
| 6 | **Профили и кастомизация** | Средний | **62%** | Аватарки на Jetson + авто-импорт из Discord, ники, статусы, масштаб UI 80–140%; нет тем, баннеров, биндов клавиш, Rich Presence игр |
| 7 | **Десктоп-оболочка, трей и ОС** | Высокий | **82%** | Нативный Direct2D 1.1 (`76 МБ RAM`, `1.0 мс`), 5 динамических иконок трея, глобальные хоткеи, автозапуск, автообновление, `Setup.exe`; нет игрового оверлея |
| 8 | **Соц-экосистема (Боты, Активности, Магазин, Модерация)** | Низкий | **0%** | Не актуально для приватной P2P-комнаты с друзьями (Nitro/Shop/Quests/AutoMod/Apps) |

**Итоговая достоверность:**
- **Как приватный голосовой/стриминговый клиент «для своих» (Core Voice + 4K60 Go Live + Chat):** **~76%** (а по задержке `1 мс`, потреблению `76 МБ RAM` и режиму «2 Экрана сразу» — **превосходит официальный Discord**).
- **Как 1-в-1 клон всего публичного Discord (с форумами, тредами, ботами, оверлеем, вебками, темами, клипами):** **~48%**.

---

## 1. Голос и аудио DSP (Voice & Audio Pipeline)

### Как реализовано в официальном Discord
- **Движок:** кастомный нативный `webrtc::Call` (C++), отделённый от Chromium, с минимальной обвязкой через Voice Gateway (`wss`) вместо тяжёлого SDP.
- **Кодек:** **Opus** 48 кГц (моно/стерео, 64–384 кбит/с на буст-серверах), In-Band FEC, адаптивный битрейт и размер фреймов.
- **Шумоподавление:** встроенный лицензированный **Krisp AI** (нейросеть на CPU/NPU, вырезает клавиатуру, собак, дыхание, фоновые голоса) + классический WebRTC NS.
- **Эхоподавление (AEC):** системный Windows Loopback AEC (отсекает звук из колонок и со второго монитора, чтобы собеседник не слышал себя или твоё видео).
- **Обработка микрофона:** High-Pass Filter, Auto Gain Control (AGC2), Voice Activity Detection (VAD) с настраиваемым порогом дБ, Push-to-Talk (PTT) с задержкой отпускания (release delay), Attenuation (приглушение других программ, когда кто-то говорит).
- **Soundboard (Саундпад):** короткие MP3/OGG-клипы до 5 сек, воспроизводимые сервером в общий микс голосового канала, плюс личный «звук входа» (Entrance Sound) и отдельный ползунок громкости саундпада.
- **Индикация и управление:** громкость каждого участника `0–200%`, локальный мут юзера, приоритетный спикер, тест микрофона с индикатором, иконки типов устройств, динамическая иконка в трее.

### Сверка с `FluxCord P2P v2.2.0`

| Фишка Discord | Статус | Дост. | Реализация в `FluxCord P2P` и честные пробелы |
|---|---|---|---|
| Opus 48 кГц + FEC/PLC + адаптивный Jitter Buffer | ✅ Полностью | **95%** | [VoiceEngine.cpp](file:///C:/Users/User/Projects/FluxCordP2P/native/src/VoiceEngine.cpp): Opus 48 кГц VOIP 128 кбит/с, In-Band FEC, адаптивный буфер 40→60→240 мс, 64-bit seq unwrap, авто-восстановление при отключении гарнитуры. |
| WASAPI Event-Driven + MMCSS Pro Audio | ✅ Полностью | **100%** | [VoiceEngine.cpp](file:///C:/Users/User/Projects/FluxCordP2P/native/src/VoiceEngine.cpp): нативный shared-mode WASAPI capture/render, поток `Pro Audio`, soft-tanh лимитер. |
| Шумоподавление (Krisp / AI) | 🟡 Частично | **60%** | [NoiseSuppressor.cpp](file:///C:/Users/User/Projects/FluxCordP2P/native/src/NoiseSuppressor.cpp): Studio DSP (HPF 80Гц + Gate + AGC) + собственный 512-pt COLA STFT MMSE-LSA + KeyKill + опциональная загрузка `rnnoise.dll`. **Пробел:** пункты `DeepFilterNet3` и `NVIDIA Broadcast AFX` есть в UI ([ShellOverlays.cs](file:///C:/Users/User/Projects/FluxCordP2P/src/FluxCord/ShellOverlays.cs)), но DLL не грузятся — идёт тихий fallback на встроенный MMSE+KeyKill. |
| Эхоподавление микрофона (AEC) | ❌ Нет | **0%** | Для микрофона AEC не реализован (если сидеть на колонках без наушников, микрофон будет ловить звук). Для стримов эхо голоса решается через Process-Exclusion Loopback. |
| Громкость каждого участника (0–200%) + сохранение | ✅ Полностью | **100%** | [VoiceEngine.cpp](file:///C:/Users/User/Projects/FluxCordP2P/native/src/VoiceEngine.cpp) + [DiscordClient.cs](file:///C:/Users/User/Projects/FluxCordP2P/src/FluxCord/DiscordClient.cs) + [Settings.cs](file:///C:/Users/User/Projects/FluxCordP2P/src/FluxCord/Settings.cs) (`UserVolumes`). |
| Тест микрофона + 40-сегментный VU-метр | ✅ Полностью | **100%** | [ShellOverlays.cs](file:///C:/Users/User/Projects/FluxCordP2P/src/FluxCord/ShellOverlays.cs) + локальный loopback в `VoiceEngine`. |
| Push-to-Talk (PTT) | 🟡 Частично | **55%** | Работает в [Shell.cs](file:///C:/Users/User/Projects/FluxCordP2P/src/FluxCord/Shell.cs) через `GetAsyncKeyState`, но **нет UI для переназначения клавиши** (по умолчанию `RControl`) и нет настройки release delay. |
| Индикация «кто говорит» (кольца, эквалайзер, HUD) | ✅ Полностью | **100%** | [Shell.cs](file:///C:/Users/User/Projects/FluxCordP2P/src/FluxCord/Shell.cs) + `fc_voice_is_ssrc_speaking`: зелёные кольца, 3-полосный эквалайзер, Speaking HUD поверх полноэкранного стрима. |
| Звуки интерфейса (вход/выход/мут/стрим/сообщение) | ✅ Полностью | **95%** | [SoundFx.cs](file:///C:/Users/User/Projects/FluxCordP2P/src/FluxCord/SoundFx.cs): синтезированный 48 кГц PCM без внешних WAV-файлов, отдельная громкость и кнопка теста. |
| Soundboard (саундпад) и Entrance Sounds | ❌ Нет | **0%** | Не реализовано ни на Jetson (`server/main.go`), ни в клиенте. |
| Attenuation (приглушение других приложений) | ❌ Нет | **0%** | Не реализовано. |

---

## 2. Видео, стриминг экрана (Go Live) и клипы

### Как реализовано в официальном Discord
- **Go Live (демонстрация экрана/окна/игры):** захват через DXGI Desktop Duplication, Windows Graphics Capture (WGC) или DLL-хук игры (DX11/12/Vulkan/OpenGL), отдельная ветка захвата звука конкретного процесса (включая дочерние процессы игровых движков).
- **Кодеки и качество:** аппаратное кодирование **H.264, H.265 (HEVC), AV1** (NVENC/AMF/QSV) до **4K 60fps** (в Nitro), режим **HDR10**, **Simulcast** (несколько слоёв качества) + `Media Sink Wants` (зритель сам просит нужный битрейт/разрешение).
- **Мультистриминг:** просмотр нескольких стримов одновременно в сетке или фокус на один стрим + Picture-in-Picture (PiP) мини-окно при переходе в текстовый чат.
- **Веб-камера и видеозвонки:** захват с камеры, виртуальные фоны, размытие фона, маски/реакции.
- **Клипы и Auto Clips:** кольцевой буфер GPU в памяти (последние 15–120 сек геймплея + отдельные дорожки голоса/игры), ручное сохранение хоткеем или автоклип по пику смеха/крика, встроенный триммер и отправка в чат.

### Сверка с `FluxCord P2P v2.2.0`

| Фишка Discord | Статус | Дост. | Реализация в `FluxCord P2P` и честные пробелы |
|---|---|---|---|
| Захват монитора до 4K 120fps (Zero-Copy NVENC) | ✅ Полностью | **100%** | [ScreenEngine.cpp](file:///C:/Users/User/Projects/FluxCordP2P/native/src/ScreenEngine.cpp): DXGI Desktop Duplication → D3D11 VideoProcessor NV12 → Hardware MFT NVENC (до 35 Мбит/с, 120fps) без копирования в RAM. |
| Захват отдельного окна + живые превью 3×2 | ✅ Полностью | **90%** | [ScreenEngine.cpp](file:///C:/Users/User/Projects/FluxCordP2P/native/src/ScreenEngine.cpp) + [FluxCordMedia.cpp](file:///C:/Users/User/Projects/FluxCordP2P/native/src/FluxCordMedia.cpp): `PrintWindow` + сетка превью в [ShellOverlays.cs](file:///C:/Users/User/Projects/FluxCordP2P/src/FluxCord/ShellOverlays.cs). (Нет DLL-хука для эксклюзивного полноэкранного DX12). |
| **Режим «2 Экрана сразу (1 + 2)» в 32:9** | ⚡ Лучше Discord | **120%** | Уникальная фишка [ScreenEngine.cpp](file:///C:/Users/User/Projects/FluxCordP2P/native/src/ScreenEngine.cpp): GPU HLSL BGRA-композитор склеивает 2 монитора в `3840×1080 @ 60fps` в одном стриме (в официальном Discord этого нет вообще). |
| Звук стрима без эха голоса | ✅ Полностью | **95%** | [ScreenEngine.cpp](file:///C:/Users/User/Projects/FluxCordP2P/native/src/ScreenEngine.cpp): Win10/11 WASAPI Process Loopback (включая исключение процесса FluxCord, чтобы не двоить голоса) + Opus 128k stereo. |
| Мультистриминг (до 4 стримов одновременно) | ✅ Полностью | **90%** | 4 независимых слота [StreamReceiver.cpp](file:///C:/Users/User/Projects/FluxCordP2P/native/src/StreamReceiver.cpp) + сетка/фокус + громкость/мут каждого стрима. **Пробел:** нет мини-окна PiP при возврате в текстовый чат. |
| Кодеки H.264 / HEVC / AV1 + HDR10 | 🟡 Частично | **45%** | H.264 NVENC/NVDEC работает на 100%. В [StreamReceiver.cpp](file:///C:/Users/User/Projects/FluxCordP2P/native/src/StreamReceiver.cpp) и [VideoDepacketizer.h](file:///C:/Users/User/Projects/FluxCordP2P/native/src/VideoDepacketizer.h) есть приёмник HEVC, но энкодер и клиент жёстко зафиксированы на H.264. **AV1 и HDR10 не реализованы.** |
| Simulcast / адаптивный битрейт под слабого зрителя | ❌ Нет | **0%** | Отправляется один поток заданного битрейта (отлично для 2–3 друзей с хорошим интернетом, но нет даунскейла под плохой канал одного зрителя). |
| Веб-камера, размытие и виртуальные фоны | ❌ Нет | **0%** | Захват камер (`MediaFoundation` / DirectShow) не реализован. |
| Клипы (GPU ring buffer) и триммер | ❌ Нет | **0%** | Не реализовано. |

---

## 3. Сеть, транспорт, NAT Traversal и E2EE

### Как реализовано в официальном Discord
- **Архитектура:** строго клиент–сервер через глобальную сеть **SFU (Selective Forwarding Units)** — скрывает IP пользователей, масштабируется на сотни участников, но в РФ упирается в блокировки РКН/DPI и высокий пинг (`120–230 мс` через VPN/Мумбаи).
- **Протокол:** WebSocket Gateway (сигналинг, zlib-stream, Resume) + UDP RTP/RTCP с `aead_xchacha20_poly1305_rtpsize`.
- **DAVE E2EE:** сквозное шифрование аудио и видео на базе **MLS (Messaging Layer Security)** с динамической ротацией ключей (ratchets) и верификацией кодов безопасности между участниками.

### Сверка с `FluxCord P2P v2.2.0`

| Фишка Discord | Статус | Дост. | Реализация в `FluxCord P2P` и честные пробелы |
|---|---|---|---|
| Зашифрованный RTP/RTCP транспорт (`XChaCha20-Poly1305`) | ✅ Полностью | **100%** | [VoiceTransport.cpp](file:///C:/Users/User/Projects/FluxCordP2P/native/src/VoiceTransport.cpp) + [StreamReceiver.cpp](file:///C:/Users/User/Projects/FluxCordP2P/native/src/StreamReceiver.cpp): `libsodium` AEAD XChaCha20-Poly1305, QoS DSCP EF46/AF41, RTCP PLI, 8 МБ сокет-буферы. |
| **Прямой 0-hop UDP P2P + Jetson Orin SFU + Zero-Config NAT Punch** | ⚡ Лучше Discord | **115%** | [server/main.go](file:///C:/Users/User/Projects/FluxCordP2P/server/main.go) + [DiscordClient.cs](file:///C:/Users/User/Projects/FluxCordP2P/src/FluxCord/DiscordClient.cs) + [NetTurbo.h](file:///C:/Users/User/Projects/FluxCordP2P/native/src/NetTurbo.h): `1 мс` LAN / прямой Cone-NAT пробив через AES-256-GCM MQTT-over-TLS-WS без проброса портов на роутере и без РКН. |
| Авто-реконнект и восстановление звонка | ✅ Полностью | **95%** | Экспоненциальный реконнект в `DiscordClient.cs` + полное восстановление звонка и стримов после обновления (`Updater.cs` `resume_session.json`). |
| DAVE MLS E2EE (динамические ключи на каждого участника) | 🟡 Частично | **40%** | Библиотека `libdave.dll` полностью обвязана в C++ (`VoiceTransport.cpp`, `StreamReceiver.cpp`), **но в P2P-режиме вызывается с `protocol_version = 0` (passthrough)** — шифрование идёт общим 256-битным ключом комнаты `XChaCha20-Poly1305` (для приватной комнаты безопасно, но нет MLS-ротации ключей). |

---

## 4. Текстовый чат и сообщения

### Как реализовано в официальном Discord
- **Форматирование (Markdown + WYSIWYG):** `**жирный**`, `*курсив*`, `__подчёркнутый__`, `~~зачёркнутый~~`, `` `инлайн-код` ``, блоки кода с подсветкой синтаксиса, `||спойлеры||`, заголовки `# / ## / ###`, списки, цитаты `> / >>>`, таймстампы `<t:unix:R>`, кликабельные ссылки `[текст](url)`.
- **Интерактив:** ответы (Reply), редактирование, удаление, закрепление (Pin), **пересылка сообщений (Forward)**, **тихие сообщения (`@silent`)**, **ветки (Threads)**, **опросы (Polls)**, **голосовые сообщения (Voice Messages)** с волной.
- **Эмодзи, стикеры, GIF:** полный поиск по Unicode-эмодзи, кастомные и анимированные эмодзи сервера, супер-реакции, Tenor GIF-пикер, стикеры.
- **Вложения и Embeds:** множественные файлы, предпросмотр видео/аудио прямо в чате, автоматическое разворачивание ссылок (OpenGraph / YouTube / Twitter Embeds).
- **Поиск и история:** серверный полнотекстовый поиск с фильтрами (`from:`, `has:image`, `before:`, `in:`), бесконечная подгрузка истории вверх, синхронизированные счётчики непрочитанных (`ack`) и упоминания (`@user` с автодополнением).

### Сверка с `FluxCord P2P v2.2.0`

| Фишка Discord | Статус | Дост. | Реализация в `FluxCord P2P` и честные пробелы |
|---|---|---|---|
| Отправка, ответы, редактирование, удаление, пины, реакции | ✅ Полностью | **95%** | [Shell.cs](file:///C:/Users/User/Projects/FluxCordP2P/src/FluxCord/Shell.cs) + [server/main.go](file:///C:/Users/User/Projects/FluxCordP2P/server/main.go): работает end-to-end с сохранением в `hub_state.json`. |
| Файлы, картинки, `Ctrl+V` скриншоты, Drag&Drop, Лайтбокс | ✅ Полностью | **90%** | [W.cs](file:///C:/Users/User/Projects/FluxCordP2P/src/FluxCord/W.cs) + [DiscordClient.cs](file:///C:/Users/User/Projects/FluxCordP2P/src/FluxCord/DiscordClient.cs): до 30 МБ по LAN HTTP / до 6 МБ по WS, скачивание и локальное открытие файлов. **Пробел:** нет встроенного видео/аудиоплеера прямо в ленте чата. |
| Индикатор «печатает...» и разделители дат | ✅ Полностью | **100%** | Реализовано в [Shell.cs](file:///C:/Users/User/Projects/FluxCordP2P/src/FluxCord/Shell.cs) и `server/main.go`. |
| Markdown и кликабельные ссылки | 🟡 Частично | **20%** | Только цельноблочный ` ```код``` ` и `> цитата` в начале сообщения; URL открывается только кнопкой «Ссылка» в ховере. **Нет `**жирного**`, `*курсива*`, `||спойлеров||`, инлайн-кода, подсветки синтаксиса, кликабельных ссылок в тексте.** |
| Эмодзи, GIF и стикеры | 🟡 Частично | **20%** | В [ShellOverlays.cs](file:///C:/Users/User/Projects/FluxCordP2P/src/FluxCord/ShellOverlays.cs) фиксированная сетка на 24 эмодзи. **Нет поиска эмодзи, кастомных эмодзи, GIF-пикера и стикеров.** |
| Авто-Embeds ссылок (OpenGraph / YouTube) | 🟡 Частично | **15%** | Рендер карточек `DiscordEmbed` в [Shell.cs](file:///C:/Users/User/Projects/FluxCordP2P/src/FluxCord/Shell.cs) готов, но сервер `main.go` не парсит OpenGraph-метаданные ссылок. |
| История сообщений и поиск | 🟡 Частично | **30%** | Сервер хранит 500 сообщений/канал, но при `welcome` отдаёт только последние 80; `LoadOlderMessagesAsync` — пустая заглушка; `LocalCache.cs` (SQLite) не используется; поиск `Ctrl+F` ищет только по загруженным 80 сообщениям в текущем канале. |
| Упоминания (`@user`) и непрочитанные | 🟡 Частично | **35%** | Локальный счётчик непрочитанных в RAM; кнопка «Упомянуть» вставляет `<@uid>`, но **нет `@`-автодополнения и замены `<@uid>` на цветное `@Имя` в тексте**. |
| Ветки (Threads), Опросы (Polls), Голосовые сообщения (ГС), Пересылка | ❌ Нет | **0%** | Не реализовано. |

---

## 5. Серверы, каналы, ЛС и права

### Как реализовано в официальном Discord
- **Мульти-серверы и папки:** до 100–200 серверов, папки серверов, перетаскивание, настройка уведомлений на каждый сервер/канал, закрепление избранных каналов.
- **Типы каналов:** Текстовые, Голосовые (со встроенным текстовым чатом!), Трибуны (Stage), Форумы, Медиа-каналы, Новости, Спойлер-каналы, Категории с drag-and-drop сортировкой.
- **Управление каналами и правами:** создание/переименование/удаление каналов в UI, битовая система ролей и прав (`RolePerms`, `ChannelOverwrites`), медленный режим (Slowmode), NSFW.
- **ЛС, Групповые ЛС, Друзья:** заявки в друзья, блокировка, групповые чаты до 10 человек со звонками, инвайты со сроком действия и лимитом использований.

### Сверка с `FluxCord P2P v2.2.0`

| Фишка Discord | Статус | Дост. | Реализация в `FluxCord P2P` и честные пробелы |
|---|---|---|---|
| Текстовые и голосовые каналы + категории | ✅ Полностью | **80%** | [server/main.go](file:///C:/Users/User/Projects/FluxCordP2P/server/main.go): каналы и категории хранятся в `hub_state.json`, сворачиваются в сайдбаре. |
| Создание / редактирование / удаление каналов | 🟡 Частично | **40%** | Создание есть через `/channel Имя` и `/voice Имя`. **Нет кнопки `+` в сайдбаре, нет переименования, удаления и сортировки каналов.** |
| Личные сообщения (ЛС) и список друзей | 🟡 Частично | **45%** | 1-на-1 ЛС (`dm_<a>_<b>`) работают и сохраняются на Jetson; все участники комнаты автоматически видны во вкладке «Друзья». **Нет групповых ЛС и звонков напрямую из ЛС.** |
| Приглашения (Invites) | ✅ Полностью | **75%** | Ссылка `fluxp2p://<secret>@<ip>:37400` + персонализированные `Setup.exe`. Нет одноразовых/истекающих инвайтов. |
| Мульти-серверы (несколько комнат/хабов) | ❌ Нет | **0%** | Сейчас жёстко одна комната `"g_p2p"` на один `Room Secret`. |
| Роли, права доступа, Форумы, Трибуны, Текстовый чат внутри Voice | ❌ Нет | **0%** | Все участники комнаты равноправны (кроме редактирования/удаления чужих сообщений). |

---

## 6. Профили, кастомизация и настройки

### Как реализовано в официальном Discord
- **Профиль:** аватар (статичный/GIF), баннер, цвет акцента, «Обо мне» (Bio), кастомный статус с эмодзи и таймером, украшения аватара (Avatar Decorations), эффекты профиля, бейджи, привязанные аккаунты (Steam/Twitch/Spotify/GitHub).
- **Rich Presence / Game Activity:** автоматическое определение запущенной игры/IDE и отображение «Играет в ...» с таймером и статусом из игры.
- **Темы и внешний вид:** Светлая / Тёмная / Ash / Onyx (AMOLED) + градиентные темы, компактный/уютный режим чата, масштаб шрифта и интерфейса.
- **Хоткеи (Keybinds):** полноценный конструктор любых сочетаний клавиш (Mute, Deafen, PTT, Toggle Overlay, Save Clip, Soundboard).

### Сверка с `FluxCord P2P v2.2.0`

| Фишка Discord | Статус | Дост. | Реализация в `FluxCord P2P` и честные пробелы |
|---|---|---|---|
| Аватарки + авто-импорт из Discord | ✅ Полностью | **95%** | [ImageCache.cs](file:///C:/Users/User/Projects/FluxCordP2P/src/FluxCord/ImageCache.cs) + [DiscordClient.cs](file:///C:/Users/User/Projects/FluxCordP2P/src/FluxCord/DiscordClient.cs): кроп 192×192 PNG через WIC, хранение на Jetson (не боится РКН), авто-импорт из локального Discord. |
| Никнеймы, статусы (`online/idle/dnd/invisible`), кастом-текст | ✅ Полностью | **90%** | `/nick`, `/status`, попап статуса, персистентность в `hub_state.json`. **Пробел:** смена ника пока через чат `/nick`, нет отдельного поля ввода прямо в профиле. |
| Масштаб интерфейса (UI Scale 80–140%) | ✅ Полностью | **100%** | Живой ползунок в [ShellOverlays.cs](file:///C:/Users/User/Projects/FluxCordP2P/src/FluxCord/ShellOverlays.cs) + `WM_DPICHANGED`. |
| Баннеры профиля | 🟡 Частично | **30%** | Автоматический доминантный цвет по аватарке ([ImageCache.cs](file:///C:/Users/User/Projects/FluxCordP2P/src/FluxCord/ImageCache.cs)), нельзя загрузить свою картинку-баннер. |
| Выбор цветовых тем (Obsidian / AMOLED / Midnight / Light) | ❌ Нет | **0%** | Поле `Theme` в [Settings.cs](file:///C:/Users/User/Projects/FluxCordP2P/src/FluxCord/Settings.cs) не используется — палитра `Th` в [UI.cs](file:///C:/Users/User/Projects/FluxCordP2P/src/FluxCord/UI.cs) захардкожена как `const uint`. |
| Конструктор хоткеев (Keybinds UI) | ❌ Нет | **0%** | Только тумблер глобальных хоткеев (`Ctrl+Shift+M` / `Ctrl+Shift+D`) и тумблер PTT (`RControl`). |
| Game Activity / Rich Presence (авто-детект игры) | ❌ Нет | **0%** | Нет сканера запущенных процессов / RPC-сокета для игр. |

---

## 7. Десктоп-оболочка, трей, оверлей и ОС-интеграция

### Как реализовано в официальном Discord
- **Оболочка:** Electron + Chromium (`400–900 МБ RAM` в простое).
- **Системный трей:** сворачивание в трей, контекстное меню, динамическая иконка состояния голоса (в голосе / говорит / мут / дефен / непрочитанные).
- **Игровой оверлей (Overlay v3):** отдельный процесс + инъекция/композитинг поверх DX11/DX12/Vulkan игр: виджет голоса (кто говорит), чат, просмотр стрима друга в углу, всплывающие уведомления.
- **Уведомления Windows:** нативные Windows Toast Notifications с текстом сообщения и быстрым ответом + мигание таскбара.
- **Обновления и установка:** Squirrel/Host auto-updater + `.exe` установщик.

### Сверка с `FluxCord P2P v2.2.0`

| Фишка Discord | Статус | Дост. | Реализация в `FluxCord P2P` и честные пробелы |
|---|---|---|---|
| Производительность и рендер UI | ⚡ Лучше Discord | **150%** | [Gfx.cs](file:///C:/Users/User/Projects/FluxCordP2P/src/FluxCord/Gfx.cs): нативный Direct2D 1.1 + DirectWrite + DXGI FlipDiscard, **76 МБ RAM**, **1.0 мс/кадр**, `0% CPU` в простое (в ~8× легче Electron Discord). |
| Динамическая иконка в трее (5 состояний) | ✅ Полностью | **100%** | [App.cs](file:///C:/Users/User/Projects/FluxCordP2P/src/FluxCord/App.cs): Idle / В голосе / Говоришь (неоново-зелёное кольцо) / Мут (красный слэш) / Дефен (красное кольцо + X), обновляется каждые `60 мс` даже в фоне. |
| Глобальные хоткеи поверх игр | ✅ Полностью | **85%** | Win32 `RegisterHotKey` (`Ctrl+Shift+M` / `Ctrl+Shift+D`), работают в полноэкранных играх. (Нет кастомизации клавиш). |
| Автозапуск с Windows + старт в трее (`--minimized`) | ✅ Полностью | **100%** | `HKCU\...\Run` в [W.cs](file:///C:/Users/User/Projects/FluxCordP2P/src/FluxCord/W.cs) + переключатели в Настройках. |
| Автообновление с GitHub + возврат в звонок | ✅ Полностью | **100%** | [Updater.cs](file:///C:/Users/User/Projects/FluxCordP2P/src/FluxCord/Updater.cs): SHA-256 проверка, атомарный swap за `<1с`, откат при ошибке, автовозврат в канал и стримы. |
| Нативный установщик `Setup.exe` | ✅ Полностью | **100%** | [setup_main.cpp](file:///C:/Users/User/Projects/FluxCordP2P/installer/setup_main.cpp): тёмный Win32 GUI, без UAC, ярлыки, запись в «Удаление программ». |
| Уведомления при свёрнутом окне | 🟡 Частично | **45%** | Работают `FlashWindow` в таскбаре + звук `UiSound.Message`. **Нет системных Windows Toast баннеров** с текстом сообщения. |
| Игровой оверлей поверх игр (DX11/DX12/Vulkan) | ❌ Нет | **0%** | Есть только Speaking HUD поверх полноэкранного стрима внутри самого FluxCord. Настоящий поверх-игровой оверлей (Borderless TopMost HUD или DX hook) не реализован. |

---

## 8. Найденные «тихие» расхождения и мёртвый код в `FluxCord P2P`

1. **Шумодавы `DeepFilterNet3` и `NVIDIA Broadcast AFX` в UI** ([ShellOverlays.cs](file:///C:/Users/User/Projects/FluxCordP2P/src/FluxCord/ShellOverlays.cs)) — реально не загружают эти SDK, а тихо переключаются на встроенный `COLA MMSE + KeyKill` в [NoiseSuppressor.cpp](file:///C:/Users/User/Projects/FluxCordP2P/native/src/NoiseSuppressor.cpp).
2. **`LoadOlderMessagesAsync` — пустая заглушка** ([DiscordClient.cs](file:///C:/Users/User/Projects/FluxCordP2P/src/FluxCord/DiscordClient.cs)): на Jetson хранится 500 сообщений/канал, но клиент при входе получает 80 и проскроллить выше 80 не может.
3. **Неиспользуемые файлы от форка Discord:** `RemoteAuthQr.cs`, `QrCode.cs`, `LocalCache.cs` — собираются в бинарник, но в P2P не участвуют (увеличивают размер `.exe`).
4. **Неиспользуемые поля настроек:** `Theme`, `DesktopNotifications`, `StreamerMode` в `Settings.cs` сохраняются, но ни на что не влияют.

---

## 9. Рекомендуемая дорожная карта (что реально стоит допилить)

### Приоритет P0 — Максимальный кайф для ежедневных посиделок с друзьями
1. **Лёгкий TopMost Игровой Оверлей (Borderless Click-Through HUD)** — отдельное прозрачное окно поверх Borderless-игр (`WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST`), показывающее кто говорит (аватарка + зелёное кольцо + ник) прямо в углу игры без инъекции DLL (не триггерит античиты EAC/Vanguard!).
2. **Инлайн-Markdown + кликабельные ссылки + `@упоминания`** — `**жирный**`, `*курсив*`, `||спойлер||`, `` `код` ``, клик прямо по URL в тексте, авто-разворачивание картинок/YouTube (OpenGraph на Jetson).
3. **Саундпад (Soundboard)** — загрузка коротких `.mp3/.ogg/.wav` на Jetson + проигрывание в голосовой канал для всех с отдельной громкостью и кнопками внизу.
4. **Конструктор хоткеев (Keybinds UI)** — возможность задать любую клавишу/кнопку мыши на Mute, Deafen, Push-to-Talk и Toggle Overlay прямо в настройках.

### Приоритет P1 — Удобство и полировка уровня Discord
5. **Пагинация истории сообщений + нормальный поиск по всем 500+ сообщениям** на Jetson.
6. **Удаление/переименование каналов + кнопка `+`** в сайдбаре.
7. **Цветовые темы** (Obsidian / AMOLED Pure Black / Midnight Blue / Nord) — перевести `Th` из `const` в динамические свойства.
8. **Нативные Windows Toast уведомления** (баннер в углу экрана при сообщении в фоне) + PiP мини-окно стрима при переходе в текстовый чат.
9. **Реальное переключение на HEVC/AV1** для экономии трафика на 4K60 стримах.

### Приоритет P2 — Если захочется «вообще всё»
10. **Веб-камера** (Media Foundation capture + H.264), **Голосовые сообщения (ГС)**, **Опросы (Polls)**, **Авто-детект запущенных игр** (Rich Presence), **Локальные клипы** (30-секундный GPU ring buffer).
11. **Вычистить мёртвый код** (`RemoteAuthQr.cs`, `QrCode.cs`, `LocalCache.cs`) и честно подписать движки шумоподавления (или подложить `rnnoise.dll` в релиз).
