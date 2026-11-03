# Проверки beta.3

**Текущий проверенный код: `aec9fa0f83aa4cf61142647c80abf1dfe79b6ca1`. Полный [CI 41669051537](https://github.com/Jetvac/ssh-explorer/actions/runs/41669051537) — SUCCESS; [UI preflight 41669053200](https://github.com/Jetvac/ssh-explorer/actions/runs/41669053200) — SUCCESS.** [Сохранённые безопасные логи/JSON/хеши](test-results/beta3-ci-aec9fa0-evidence.json). Последующий docs-only release commit проходит собственный CI тега; точные version/commit/ZIP/SHA и проверка опубликованных файлов находятся в [GitHub Release](https://github.com/Jetvac/ssh-explorer/releases/tag/v0.2.0-beta.3). Исторические неуспехи ниже не являются статусом этого checkpoint.

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

Второй [CI 41663155814](https://github.com/Jetvac/ssh-explorer/actions/runs/41663155814), commit `7c9bd5c`, подтвердил **32 actual WPF UI/graph assertions**: обе оси metric fill, selection/palette, first/stale, numeric sort, narrow viewport, UIFont role 22, sidebar/context и ordinary profile footer 430 DIP. [Лог](test-results/beta3-ui-7c9bd5c.log). App DLL снимков SHA `63bffac4508018f41edf1220aa1c907f88bd662e42b31c1cad995db85930cd39` совпал с DLL portable-каталога этого CI. Pipeline не объявлен PASS: helper baseline не загрузил App.xaml у subclass. Исправлен exact App(false) из уже опубликованной beta.2; самостоятельная компиляция against beta.2 — 0/0, без запуска на host.

CI desktop ограничивает фактический размер окна: при запросе outer 1320×850 получен WPF client 1044×788, DPI96; narrow client 760×788. Поэтому metadata записывает actual/requested dimensions и primary screen отдельно. Native caption/DWM не рендерится; прозрачный участок его области в PNG не является дефектом клиентского окна и не закрашивается. Статические кадры снимаются после 300 ms dispatcher settle, без принудительной перемотки clocks или ретуши.


## Завершённая приёмка доступного уровня

34 actual WPF UI/graph assertions — PASS: viewport 500 / видимые колонки 500 / uncovered 0 DIP при star name; raw template filler не трактуется как видимая пустота. Header UIFont22 — actual99 / natural106,63 DIP, ellipsis и полный tooltip вместо raw clipping. Height/natural text, full-cell обеих осей, palette без нового sample, selection, ненулевая прокрутка, sidebar/tree, initial/stale и validation430 проверены отдельно. Реальные before/after PNG сверены SHA; common clients1044×788 и760×788, 96DPI, UIFont14, одинаковые fixture/selection/time/sort.

Все этапы полного checkpoint CI прошли: 49 core/backend; resource/MVVM; 17 clocks; 13 operations; 6 graph geometry; 16 monitor state; 8 controlled poll; 7 drag payload; Windows layout/components; 110 profile STA; real network/ordinary controls/fresh process; native ABI; packaged EXE startup/normal close Exit0. Числа не суммируются в число пользовательских сценариев. Main UI render App DLL SHA совпал с portable App DLL; [evidence](test-results/beta3-ci-aec9fa0-evidence.json).

Independent review после кадров потребовал исправить narrow filler и большой header; оба исправления подтверждены новыми кадрами/геометрией. Baseline helper использует exact App(false), одного владельца MonitorView и штатное закрытие. Прежние failed CI сохранились как история диагностики, итог этого кода — SUCCESS.

Полные [снимки и измерения финального тега](https://github.com/Jetvac/ssh-explorer/releases/download/v0.2.0-beta.3/SSHExplorer-0.2.0-beta.3-ui-evidence.zip), [проверка скачанных release files](https://github.com/Jetvac/ssh-explorer/releases/download/v0.2.0-beta.3/SSHExplorer-0.2.0-beta.3-verification.json). Статические кадры не доказывают движение, native caption/Snap или физический ввод.
