# Проверки beta.3

Пользователь запретил Computer Use. Окна на его рабочем столе не открывались. Headless Application в тестах намеренно подавляет и потребляет queued startup, проверяет отсутствие desktop starts/Windows; это не опция пользовательского EXE. Реальные WPF HWND/render сценарии разрешены только в Windows GitHub Actions.

## Локальные проверки исходного состояния

Общая Release-сборка: 0 ошибок / 0 предупреждений после исправления deferred XAML RowHeight/ColumnHeaderHeight: используются явные Double.NaN, а все 22 role styles и их templates теперь материализуются в ресурсной регрессии. Компиляция сама по себе не подтвердила бы эту границу.

- 49 backend/core assertions: источники, independent metrics, reset/new identity, signal policy, fake-clock serialization/gaps, существующие trust/storage/transfer регрессии.
- 8 resource/contract + 18 MVVM assertions; 22 явных role styles и реальные deferred templates Light/Dark/System; Window не создаётся.
- 17 clock assertions; interruption, disable, unload и theme. Это механизм, не motion video.
- 13 operation/diagnostics/transfer assertions.
- 6 graph geometry assertions: реальные timestamps, irregular intervals, explicit gap, unknown metric, независимая memory, границы интервала.
- 16 monitor state/history assertions без Application/Window/SSH.
- 8 actual MonitorView polling assertions: controlled executor, hide cancellation, hidden quiet, cancellation-ignoring late reply, serialized resume, current result and Dispose. Непоказанный UserControl, без Window/SSH; [checkpoint2 logs](test-results/beta3-checkpoint2-headless-evidence.json).
- 7 opaque drag payload assertions: source session/user/files фиксируются до изменения navigation/selection, disposed source инвалидирует token. Это не OLE/Explorer gesture и не готовность реального SFTP drop target.

Числа — отдельные assertions, не количество полных пользовательских маршрутов. [Локальные логи](test-results/beta3-headless-evidence.json) относятся к разработческому состоянию с указанными бинарными хешами; не выдаются за acceptance финального release EXE.

## Read-only Linux измерение

[Отчёт](test-results/beta3-monitor-collection.json): существующий изолированный loopback Linux-стенд, около 83 процессов, восемь замеров до и восемь после. Средний сбор **2111,300 → 1873,182 мс**, уменьшение **11,28%** в этой среде; максимум после — 1917,663 мс. Утилита tr не запускается для каждого frame, если существующий base64 поддерживает nowrap; fallback сохранён. Два реальных encoding cases проверили 97-byte bound, UTF-8/NUL/newline, long payload framing и missing sentinel. На сервере не создавались файлы, не отправлялись сигналы, ничего не устанавливалось и не менялась конфигурация.

В обоих наборах CPU — Initial на первой точке и Available на 2–8; память доступна сразу. I/O у 82 процессов честно Unavailable. Длительность показана в «Сведения о данных». Это один локальный стенд, не производительность всех Linux-хостов/сетей. JSON явно обозначает uncommitted development snapshot и точные SHA бинарей; границы history позднее уточнены отдельной fake-clock регрессией.

## Windows CI и снимки

Pipeline сохраняет ordinary Add→PasswordBox→Trust→SFTP, fresh test process saved login, 110 profile STA assertions, wrong-password retry, cancellation/owner, точность/отложенную запись секрета, native ABI и packaged startup/normal close. Итог конкретного commit указывается после фактического прогона.

`scripts/Test-UiComposition.ps1` отдельно проверяет фактические размеры ячейки/заливки, отсутствие overlap шапки, initial/stale state, numeric sort, row identity/scroll, fixed sidebar и сохранение файлового контекста. Он рендерит настоящие laid-out WPF visuals показанных CI HWND, используя обезличенные offline data. Native caption/DWM не входят в RenderTargetBitmap. Это не desktop screenshot, не физический ввод и не SSH login proof.

Before harness компилируется против **скачанного опубликованного beta.2**, SHA ZIP `c1a77b2ac57225963bc4b4a26de5317258a0b2a737ad1d64e18beb94941846e6`; его версии/816 внутренних хешей проверяются. After использует изменённую сборку. Общие fixture, выделенный PID, сортировка, timestamp, Light, outer size 1320×850 / 760×850 совпадают; actual client DIP/DPI/font/OS/HC фиксируются в metadata. Изображения не генерируются и не ретушируются. Сравнение не объявляется pixel-perfect из-за неизвестных условий Windows-референсов.

Видеозапись, физический auth modal input, полные Explorer gestures, реальные переключения DPI/HC/system motion, multi-monitor и clean Windows — NOT RUN. [Текущая приёмка](../ACCEPTANCE.md).

Первый [CI 41660491790](https://github.com/Jetvac/ssh-explorer/actions/runs/41660491790), commit `bc39d8e`, прошёл сборку/core/auth/startup, но actual-UI assertion не нашёл MetricSurface по Name. Добавлены явные Name DP и строгая материализация LoadContent/ContentPresenter.FindName без окна; ресурсный и polling прогоны после исправления прошли. Следующий CI отдельно проверяет фактические размеры/снимки.
