# SSH Explorer — приёмка

## 0.3.0 — принято пользователем

05.11.2026 пользователь проверил выпуск 0.3.0 и **полностью принял текущий функционал и интерфейс**. Это закрывает открытую ручную приёмку интерфейса для возможностей, вошедших в 0.3.0 (включая работу beta.6). Ниже сохранена история приёмки предыдущих итераций.


## Итерация beta.6 (история)

База — опубликованная beta.5 `03cf1479ab4aadd63389b57ffe8182313447b868`, ветка `claude/beta6-ui-rework`. Работа велась в облачном Linux-контейнере. Отзыв Computer Use из beta.5 действует, host-окна не открывались. Push в GitHub из сессии отклоняется прокси, поэтому Windows CI для beta.6 не запускался и prerelease не публиковался. Причина прежнего парольного crash по-прежнему неизвестна. Визуальную приёмку выполняет пользователь.

[Замечания → изменения → статус](docs/UI_ITERATION_BETA6.md) · [поставка и быстродействие](docs/PACKAGING_AND_PERFORMANCE.md) · [иконки](docs/design/ICONS.md) · [Rust-прототип](prototypes/rust-explorer/README.md).

| Граница | Статус / доказательство |
|---|---|
| Компиляция C# (Core, Monitoring, App, Tests, UiTests) | PASS своего уровня: Roslyn с WPF reference assemblies, 0 ошибок и 0 предупреждений. XAML (`App.xaml`) не компилировался |
| Core/Monitoring/Server headless | PASS: 78 проверок в Linux, включая протокол 2 на реальном `/proc`, read-only сведения «Сервер» в реальном shell, контракт истории графиков, автомат power-состояний на управляемом исполнителе |
| Стоимость сборщика до/после | PASS измерения: 3 305,7 → 22,1 мс (система) и 107,8 мс (процессы), 369 процессов, Linux dev-контейнер; [данные](docs/perf/collector-benchmark-dev-container.json). На Windows с реальным сервером не измерено |
| Вкладки после Show(), путь, caption, иконки в WPF, Параметры/Передачи/Сервер, клики заголовков | NOT RUN: тесты `--beta6-ui` написаны (SendInput на собственном CI-окне), Windows CI не запускался |
| Иконка в собранном EXE, состав и размер поставки beta.6 | NOT RUN: CI-шаг извлечения иконки и `Measure-Package.ps1` подготовлены |
| Реальные reboot/shutdown | NOT RUN: разрешённой одноразовой тестовой VM нет. Сервер со скриншота не используется |
| Rust-прототип: ядро и SSH | PASS в Linux: 21 тест, 8 из них против настоящего SSH-сервера (libssh2 + OpenSSL ↔ локальный paramiko) |
| Rust-прототип: Windows EXE, OpenSSH live | NOT RUN: `rust-prototype.yml` подготовлен |
| Регрессии подключения по паролю, отмены и таймаута, смены host key, DnD, remote editing, восстановления | NOT RUN в beta.6. Изменения не затрагивают эти модули напрямую, но полный CI обязателен перед выпуском |
| Физический ввод, native DWM/Snap, OS DPI/HC, анимации, чистая Windows | NOT RUN; ручная матрица ниже |

Ручная приёмка beta.6 (в дополнение к матрице beta.5):

1. Вкладки. Запустить с одной «Главной» → открыть сервер без изменения размера окна → видны обе вкладки и «+». Открыть 5 и 20 вкладок → стрелки и список. Закрыть лишние → нормальная ширина без resize.
2. Путь. Щелчок по пустой части поля → каретка и выделение → ввод или вставка пути с пробелами и кириллицей → Enter. Ошибочный путь → текст сохранён → исправить → Enter. Esc возвращает прежний путь. Ctrl+L и Alt+D из таблицы, дерева и поиска; в терминале Ctrl+L остаётся у оболочки.
3. Caption. Наведение на Maximize → Snap Layouts. Drag, двойной щелчок, Alt+Space, неактивное окно, Light/Dark/High Contrast, DPI 100/150/200.
4. Иконки. Восемь режимов просмотра узнаваемы. Знак в Проводнике, на панели задач, в Alt+Tab и «О приложении» — на новой распакованной папке.
5. Окна. Параметры: все разделы, ошибка ввода числа. Передачи: пусто → загрузка → отмена → ошибка → повтор; выбор и прокрутка не сбрасываются.
6. Графики. Медленный сервер даёт непрерывную линию. Обрыв сети даёт разрыв. Пауза и смена интервала 1/2/10/60 с.
7. Сортировка. Щелчки по заголовкам каждой таблицы → направление, индикатор, порядок переживает обновление (file2/file10, 900 КБ/1 МБ, PID 2/10, «—» в конце).
8. Сервер. Сведения и ограничения. Перезагрузка и выключение — только на одноразовой VM: подтверждение, отказ без прав, неизвестный результат, проверка `boot_id`.

## Историческая итерация: beta.5

База — опубликованная beta.4 `1fb919fbf87ff0baa6ecf6fcca242a6b68e614fb`. Пользователь разрешил Computer Use 04.11.2026 и затем запретил дальнейшее использование. Уже выполненный живой тест учитывается только с его development DLL; после отзыва — код/headless/Windows CI. Причина прежнего парольного crash остаётся неизвестной. Визуальную приёмку выполняет пользователь.

[Редактирование и его границы](docs/REMOTE_EDIT.md) · [сопоставление](docs/UI_PARITY.md) · [CI и provenance выпуска](docs/CI_BETA5.md).

| Граница | Статус / доказательство |
|---|---|
| Byte-preserving remote-edit lifecycle | PASS:15 целевых component checks, сериализация/immutable snapshots/journal/recovery, strict text/format, empty/missing, conflict и10МиБ; входят в64 core checkpoint, не суммируются повторно |
| Реальный SFTP и SFTP-only | PASS указанного объёма:6 normal-development и11 SFTP-only checks, общий набор пересекается; mode0755, hash-conflict при той жеmtime, explicit symlink/retarget, permission denied, root owner, FIFO, ENOSPC, cancel |
| Настоящий установленный Блокнот | PASS development-сборки: открыл `.sh` без выполнения, инструмент ввёл текст и нажал Ctrl+S; независимый SFTP hash/mode/baseline совпали; после закрытия клиента редактор открыт. Notepad11.2607.14.0; не proof финального EXE/обычного auth/физического человека |
| Native Shell icons | PASS:реальные16/32/48/256, shared cache,10 000 моделей без фонового шторма, DPI buckets/invalidation; рисунок ассоциации зависит от установленной Windows |
| Реальные WPF виды/вкладки/документы | PASS checkpoint CI своего уровня; точные final screenshots/assertions/commit в release verification. Controlled transport/launcher в document gallery явно не выдаётся за Notepad |
| Прежние файловые/auth/terminal/monitor/transfer механизмы | Сохранены;48 monitor/shell,38 Files,24 Forms/Tabs, profile/STA/auth/fresh child/native ABI и packaged startup проходят полный CI checkpoint. Финальная сборка проверяется отдельно |
| Дальнейший host Computer Use/HWND | NOT RUN после отзыва; существующий внешний редактор не закрывали |
| Полный physical auth/Explorer OLE/DWM/Snap/motion/DPI/HC/clean Windows | NOT RUN как полная матрица; шаги ниже. Native caption не восстанавливается из WPF client PNG |

Ручная приёмка текста: открыть `.sh`/`.conf`/`.txt` без расширения/Unicode/10МиБ → Ctrl+S → увидеть подтверждённый серверный hash; быстрые сохранения/atomic replacement/пустой файл → один актуальный результат; второй клиент меняет содержимое с той же длиной/mtime → конфликт → отмена/получить серверную/сохранить рядом/явная замена → новое внешнее изменение требует нового решения; недоступная сеть/ENOSPC/права → локальная копия цела; close/cancel/restart → редактор не завершён, старый draft сам не отправлен. Отдельно проверить UTF-8/BOM/UTF-16 и формат, внешний Save As, symlink/retarget/special files, одинаковые имена двух серверов и изменение профиля.

Ручная визуальная матрица: две/много одинаково названных вкладок, active close и «+», восемь файловых видов, текущий view glyph, длинное имя/inline rename/group/context menu; Light/Dark, настоящий OS text scale и DPI100/125/150/200/High Contrast; live Explorer dragWindows↔сервер/A↔B/внутри сервера, Copy/Move/None/tree/tab/Escape/close; native caption/Snap/multi-monitor; motion с быстрым прерыванием/системным отключением; чистая offline Windows с полным portable. Программный font22 — отдельная компонентная проверка, не OS DPI.

## Историческая итерация: beta.4

Baseline: опубликованная `v0.2.0-beta.3`, `e8ef6815dd3417dcbda632077ed8408c77576d04`. Успешный пользовательский запуск относится к beta.2; оценка beta.3 была по изображениям. Причина прежнего парольного вылета остаётся неизвестной. При повторении он вновь становится первым приоритетом. Визуальная приёмка остаётся за пользователем.

[Изменения](docs/UI_ITERATION_BETA4.md) · [тройное сопоставление Windows → beta.3 → beta.4](docs/UI_PARITY.md) · [проверки текущего результата](docs/CI_BETA4.md).

| Граница | Статус и доказательство |
|---|---|
| Files: inline rename/create/address/selection/menu/overflow | PASS указанного уровня: 38 actual WPF checks, controlled executor и CI-only software SendInput; [checkpoint/финальная поставка](docs/CI_BETA4.md) |
| Создание и конфликт на настоящем SFTP | PASS: 5 новых проверок на изолированном Linux, два независимых клиента; только собственный временный каталог, без установки ПО или сигналов |
| Сохранение beta.3 | PASS: прежние 34 assertions входят в 48 monitor/shell, точный опубликованный baseline проверен и повторно отрисован |
| Общие поля, popup ComboBox, вкладки, мониторинг | PASS указанного уровня: 24 actual Forms/Tabs + 48 monitor/shell; измерены применённые templates, собственный lifecycle, header sort и persistent preferences |
| Защищённый вход, DPAPI, отмена, обычный auth route | PASS full CI checkpoint df85964: 113 profile/STA, auth component/network, ordinary WPF route/fresh child, native ABI и packaged startup; точный tag CI и опубликованные ZIP связаны в release verification |
| Пользовательский рабочий стол / Computer Use | NOT RUN: прямой запрет сохраняется; host окон не открывали |
| Физический F2/Enter/Ctrl/Shift, настоящий Explorer OLE, drag-tab/tree | NOT RUN физически; программные события/controller/payload assertions имеют отдельный статус |
| DWM/caption/Snap, полная DPI/text scale/HC матрица, motion video, чистая Windows | NOT RUN; CI client render/clock tests не доказывают эти свойства |

Для ручной приёмки: F2 обычного файла/папки/dotfile в Details и плитках → ошибка/исправление/Enter/Esc; Ctrl+Shift+N → задержка/конфликт/Esc; ошибочный адрес → перевод фокуса/исправление/Enter/Esc; Ctrl/Shift/рамка+edge scroll; контекст выбранной группы; узкие Files/Home и клавиатурный overflow; dropdown/focus/error/disabled форм; много длинных вкладок и закрытие соседей; мониторинг при долгом сборе и повторном запуске с сохранёнными колонками. Протокол физического DnD: Windows↔сервер, A↔B, внутри сервера, Ctrl/Shift/None, дерево/вкладка, Escape и закрытие участвующей сессии; проверить исходник после отмены/ошибки.

## Историческая итерация: beta.3

Пользователь сообщил об успешном запуске beta.2 до рабочего экрана. Неизвестная причина прежнего crash не объявлена исправленной; сохраняется наблюдение и прежние защиты. Интерфейс пока не принят пользователем. [Целевая композиция](docs/UI_COMPOSITION_BETA3.md), [актуальная UI-матрица](docs/UI_PARITY.md).

| Проверка | Текущий статус / граница |
|---|---|
| Композиция мониторинга/левая панель/общие роли | PASS — 34 actual WPF UI/graph checks, source aec9fa0, [логи/границы](docs/CI_BETA3.md); UI-приёмка пользователя открыта |
| Metric states, independent sources, timestamp/gaps, selection | PASS — 49 backend/core + 16 monitor state + 6 graph + actual UI; [checkpoint evidence](docs/test-results/beta3-ci-aec9fa0-evidence.json) |
| Ordinary auth/retry/cancel/owner/secret/native ABI | PASS — полный CI aec9fa0, включая обычные контролы+SSH и fresh process; не физический ввод |
| Парольный crash | Пользователь дошёл до рабочего экрана; причина прежнего случая НЕ УСТАНОВЛЕНА |
| Computer Use / desktop host | NOT RUN по прямому указанию пользователя; временное разрешение beta.2 не продлевается |
| CI WPF-render снимки | PASS своего уровня: одинаковые actual dimensions/DPI/font/fixture и SHA; physical input и пользовательская приёмка отдельно |
| Полные Explorer gestures, live auth, DPI/HC, motion video, clean Windows | NOT RUN в этой итерации; воспроизводимые шаги описаны в матрице |

Финальная beta.2 уже опубликована и скачана: [отчёт](https://github.com/Jetvac/ssh-explorer/releases/download/v0.2.0-beta.2/SSHExplorer-0.2.0-beta.2-verification.json), 816 portable files / 252 source blobs, [финальный CI](https://github.com/Jetvac/ssh-explorer/actions/runs/41651021287). Нижележащие PARTIAL/«ещё не проверен» относятся к историческому checkpoint ab01976, не к итоговому релизу.

[Полный успешный CI beta.3](https://github.com/Jetvac/ssh-explorer/actions/runs/41669051537). Финальные исходники, повторно скачанные ZIP, хеши и tag CI связаны в [релизе](https://github.com/Jetvac/ssh-explorer/releases/tag/v0.2.0-beta.3); исторические результаты ниже не переносятся на него автоматически.

## Исторические результаты beta.2

03.11.2026. База — `v0.2.0-beta.1` / `eda137a734e8fc259eb97fca300b2072d11232a8`. **P0 парольного crash открыт.** Новая beta — промежуточный выпуск, не пользовательское принятие. После начального ограничения пользователь разрешил Computer Use на 40 минут: [фактический живой прогон](docs/LIVE_BETA2.md).

Исправлена ошибка изоляции: WPF вызывает OnStartup из очереди конструктора Application даже без Run. У старых профильных прогонов отозвана отметка «без окон». Новый внутренний test constructor подавляет desktop startup; queued callback намеренно выполняется с проверкой desktop starts=0, windows/HWND=0 и неизменного isolated Store. Это не опция обычного EXE. [Подробности](docs/PROFILE_FLOW_BETA2.md).

## Beta.2: фактические уровни доказательства

| Сценарий | Статус | Доказательство и предел |
|---|---|---|
| Повторный парольный crash актуального EXE | **BLOCKED инструментом** | После Connect, до Trust. Owned modal не принимает надёжный ввод через инструмент; [живой отчёт](docs/LIVE_BETA2.md). [Расследование](docs/PROFILE_CRASH.md); неизвестная причина не объявлена исправленной |
| Два последних Windows Application Error | **PASS** | Версия в событиях 0.1.0.0, `0xC00000FD`; [метаданные](docs/test-results/beta2-os-crash-provenance.json). Это происхождение событий, не диагноз beta.1 |
| Auth callbacks и transport lifecycle | **PASS** | 13 component + 26 loopback network assertions; password/key MFA, OTP cancel, reject trust, wrong→correct password, timeout, exec/SFTP/PTY/export. [AUTH_NETWORK](docs/AUTH_NETWORK.md) |
| Linux OpenSSH и существующие операции | **PASS** | 5 auth assertions (password, encrypted Ed25519, auxiliary channels); 54 существующих assertions = 36 component + 18 Linux integration. [Лог](docs/test-results/beta2-core-linux-regression.log). Нет установки компонентов на сервер |
| Пароль, RAM handoff, atomic DPAPI, opt-out/stale results | **PASS** | Повторены в новом наборе **94 isolated component assertions**: прежние 85 + 7 baseline + 2 startup/isolation guard. Реальные DPAPI/отказы хранения; [лог](docs/test-results/beta2-profiles-isolated-headless.log). Непоказанные контролы не равны живому вводу |
| Совместимость опубликованного Core beta.1 | **PASS** | 7 baseline assertions входят в те же 94, повторно не суммируются. Старый Core создаёт isolated store, beta.2 изменяет копию, original bytes/DLL неизменны. [Отчёт](docs/PROFILE_FLOW_BETA2.md), [метаданные](docs/test-results/beta2-profiles-isolated-headless.meta.json) |
| Add→PasswordBox→Trust→SFTP, fresh test process | **PASS — CI STA/network** | 8 parent assertions + успешный свежий test process; обычные команды без injected credentials/trust. [Лог](docs/test-results/beta2-ci-ab01976-ordinary-sta.log). Не физический ввод и не парольный вход опубликованного EXE |
| Modal cancellation и owner close | **PASS — CI STA** | В составе 110 профильных assertions: реальные Credentials/Trust/Interactive/Profile диалоги, exact owner, worker cancel и owner close. [Журнал](docs/test-results/beta2-ci-ab01976-components.log) |
| Motion interruption/disable/unload/theme | **PASS** | [17 actual WPF clock assertions, isolated repeat](docs/test-results/beta2-motion-isolated.log), без Window/Popup. [Контракт](docs/MOTION_SPEC.md). OS toggle, HC и визуальное качество отдельно |
| Стили, иконки, binding/state | **PASS** | 8 resource + 18 MVVM assertions, [isolated repeat](docs/test-results/beta2-resources-isolated.log). Resource load не доказывает живой HC |
| Layout, row/focus/tree, восемь видов | **PASS — CI components** | 6 WPF layout + 18 MVVM + 12 UI component assertions. [CI отчёт](docs/CI_BETA2.md); сопоставимые screenshots и все DPI отдельно |
| Dedup/cancel, durable private diagnostics, stable transfer rows | **PASS** | [13 isolated headless assertions](docs/test-results/beta2-connection-isolated.log) в совокупности; без сброса всего списка при update |
| Native COM ABI новой сборки | **PASS — CI ABI-only** | Native out-of-process QI/FILEDESCRIPTORW/IStream/EndOperation, synthetic zero-byte fixture. [Журнал](docs/test-results/beta2-ci-ab01976-components.log). Не новый real-SFTP/>2 ГБ/Explorer-прогон |
| Новый ZIP: версия, commit, внутренние хеши | **PARTIAL — CI package PASS** | CI portable каталог: version/commit/dirty=false, 785 файлов проверены. [JSON](docs/test-results/beta2-ci-ab01976-portable-verification.json). Скачанный опубликованный beta.2 ZIP ещё не проверен |
| Настоящий packaged EXE startup/normal close | **PASS — CI process** | [Результат](docs/test-results/beta2-ci-ab01976-portable-startup.json): fresh settings, exact build/PID, own HWND, normal exit 0, без forced termination. Не password input, повторный вход или чистая Windows |
| Clean pipeline checkpoint beta.2 | **PASS** | [CI 41646603882](https://github.com/Jetvac/ssh-explorer/actions/runs/41646603882), commit `ab019761db7cb2d0ae401dcebb1c3ca2ec9cb34d`; все этапы SUCCESS. [Уровни и пределы](docs/CI_BETA2.md). Поздние изменения требуют собственного CI |
| Live filesystem/terminal/monitor screenshots | **PASS частично** | [LIVE_BETA2](docs/LIVE_BETA2.md): реальные снимки и recovery, явный fixture login; не ordinary password proof |
| Live password input / published EXE credential restart | **BLOCKED инструментом** | Owned modal фокус/индексы; CI контролы не подменяют физический ввод |
| Motion video, Explorer gestures, все DPI/HC/system motion off | **NOT RUN** | В предоставленное окно подтверждены только перечисленные live-сценарии; полная матрица остаётся открытой |
| Чистая Windows, offline terminal, multi-monitor | **NOT RUN** | Отдельная среда не предоставлена. CI runner не объявляется пользовательским desktop |

Числа — assertions, не число пользовательских маршрутов. Логи host относятся к разработческим исходным состояниям (metadata отдельных проверок сохранены); финальный commit/версия дополнительно фиксируются clean CI, тегом, `build-info.json` и SHA-манифестом. CI результаты будут внесены после прогона.

## История beta.1 — не результаты beta.2

03.11.2026. Пользовательская 0.1.0 не принята. Разработка 0.2.0-beta.1 идёт в https://github.com/Jetvac/ssh-explorer, ветка `codex/windows11-hardening`. **Итерация и обязательная приёмка ещё открыты.** Публикация beta не означает пользовательского принятия.

## Проверено в этой итерации

| Область | Доказательство | Фактический статус |
|---|---|---|
| Причина падения | DLL старой поставки SHA-256 `769acec50c113e5de5236f08400809ee9696078c7b50def68c496f1dc3c09a62`; exit `0xC00000FD`; [стек](docs/test-results/profile-crash-baseline.txt) | Рекурсия DataGrid.SelectionChanged → обработчик TabControl → RefreshHome. Исправление причины отправлено коммитом `62e85c4` |
| Профили | [PROFILE_CRASH](docs/PROFILE_CRASH.md), 65 STA assertions, 34 регрессии ядра, 6 свежих SSH/SFTP-сценариев | Реальные WPF-форма и MainWindow Add→Save→выбор прошли STA; store перечитывался в том же тестовом процессе. Это не перезапуск опубликованного EXE. Живой ввод в owned modal через инструмент не завершён |
| UI | [Матрица](docs/UI_PARITY.md), [светлая файловая область](docs/screenshots/v02/files-light.jpg), [native system menu](docs/screenshots/v02/native-system-menu.jpg) | Fullwidth navigation/commands, caption tabs, компактная форма. Живо: нативные maximize/restore, Alt+Space, Win+Arrow. Полный Snap-hover, resize, все экраны/темы/DPI не засчитаны |
| Native Shell export | Новый FILEDESCRIPTORW/FILECONTENTS/IStream с async capability, отдельный out-of-process consumer, реальный SFTP и SHA | Native COM, отмена, source-close, потерянный target, Skip, zero-byte validation и сетевой путь проверены: [подробности](docs/DRAG_DROP.md). Малый файл фактически получен настоящим Explorer: [SHA/результат](docs/test-results/explorer-native-small.json). Инструмент двух межоконных жестов сообщал outside-window-bounds, поэтому вся матрица одного жеста этим не закрыта |
| Живой DnD между серверами | [Результаты A↔B и same-session](docs/test-results/live-cross-session.json) | По одному обычному drag на заголовок другой вкладки в обоих направлениях: Completed, SHA совпали, исходники сохранены. Внутри сессии drag README в строку Target: источник исчез, назначение SHA совпало |
| Native >2 ГБ | [DRAG_DROP](docs/DRAG_DROP.md), native-com-large.log | Реальный out-of-process IStream: 2 148 532 224 байта, SHA источника/назначения совпал, наблюдавшаяся память 63 + 35 МиБ. Это протокол и SFTP, а не полный GUI/WebView2/Explorer-жест |
| Код и CI | Baseline `f15c2cd`, исправление `62e85c4`, интеграция `8800aa9`, CI `06458bc` | Этапы отправлены в GitHub. Первый CI выявил гонку STA-теста ожидания сохранения. Исправленный `38ee23a` прошёл [повторный CI](https://github.com/Jetvac/ssh-explorer/actions/runs/41633232457) с комплектом и отчётом. Для коммита выпуска обязателен собственный чистый прогон: его результат указан в GitHub Releases и Actions |

Живой GUI проводил только root. По просьбе пользователя «Продолжайте код и CI, окна позже» дальнейшие проверки рабочего стола отложены. Ни один оставшийся пункт не засчитывается по одному наличию обработчика или тесту ядра.

## Обязательные проверки, которые остаются открыты

- Полный исходный пользовательский сценарий профиля из скачанной опубликованной beta, с реальным клавиатурным вводом и ошибками хранения.
- Windows → сервер, все row/tree/current-folder цели, Ctrl/Shift, удержание hover над вкладкой, дерево/автопрокрутка, multi-select, сеть/закрытие/Cancel в полном живом OLE-пути.
- Большой (>2 ГБ) и медленный drag с пустым кэшем, память процесса, целостность, отмена и безопасное закрытие в настоящем Explorer. 64-битный descriptor и прежний backend >2 ГБ не заменяют этот сценарий.
- Сопоставимые снимки остальных экранов/состояний, 100/125/150/200%, High Contrast, другой монитор. Загрузка HC resource dictionary не считается проверкой доступности.
- Отдельная чистая Windows 11 и полностью offline запуск терминала: доступна только пользовательская Ubuntu VM, Windows Sandbox отсутствует. `DOTNET_ROOT` не считается чистой ОС.
- Пользовательское принятие результата; решение может дать только пользователь.

## Сохранённые результаты предыдущей итерации 0.1.0

Таблица ниже относится к прежним испытаниям и не подменяет повторную приёмку 0.2. При затронутом поведении приоритет имеет новый статус выше.

| № | Обязательный сценарий | Реально выполнено | Статус |
|---|---|---|---|
| 1 | Профили, restart, секреты | Два профиля/настройки после atomic rewrite; DPAPI roundtrip и отсутствие открытого секрета во всех файлах store | Unit пройдены; полный ручной CRUD профилей не засчитан |
| 2 | Host key и endpoint | Reject/session/permanent, endpoint/port/algorithm, coalescing; реальный handshake reject/wrong password/changed key | Ядро пройдено; auth-диалоги не автоматизировались |
| 3 | Независимость и lifecycle | Реальные независимые сессии, отдельные пути/PTY/lifetime. Закрытие медленного запроса за 404 мс, ноль proxy connections; in-place reconnect переносит контекст | Ядро пройдено; полная ручная матрица вкладок открыта |
| 4 | Терминал | Реальные PTY, split UTF-8, кириллица/emoji, resize 37×121, Ctrl+C/D, persistent shell. В опубликованном EXE реальный UTF-8 и ANSI red через комплектный WebView2 | Указанные проверки пройдены; весь набор less/nano/vim/sudo/paste вручную не засчитан |
| 5 | Навигация | Реальное дерево/home/root/сегменты; MVVM late reply, failed navigation/back, history/selection/scroll | Реализовано и проверено указанным объёмом; вся клавиатурная матрица открыта |
| 6 | Восемь видов и колонки | Все 8 видов визуально выбраны; 6 WPF layout + 16 MVVM state/binding тестов; virtualized rows/icons/list, фильтры и natural sort | Виды и автоматические проверки пройдены; полная ручная матрица колонок/рамки/клавиш открыта |
| 7 | Файловые операции и undo | Реальные CRUD/copy/move/merge/chmod/chown, undo и отказ при внешнем изменении той же длины/mtime | Сетевые сценарии пройдены; каждый GUI-способ каждой операции отдельно не засчитан |
| 8 | Обмен с Windows | Настоящие CF_HDROP→SFTP и SFTP→CF_HDROP, SHA-256, существующий локальный файл, opaque token без CLR serialization. OLE list/tree/outbound/cross-tab реализованы | Системный протокол и UI-команды пройдены; **живой Explorer drag/drop, hover-expand и cross-tab drag не подтверждены** |
| 9 | Сбои передач | Replace/skip/keep-both/merge/cancel; задержка 50 мс, обрыв после 524288 байт, отсутствие autoretry, явный retry с конфликтом, ENOSPC, cross-filesystem success/failure | Пройдено; оригиналы и SHA-256 сохранены при отказах |
| 10 | Имена и ссылки | Кириллица, пробелы, кавычки, начальный дефис и shell-метасимволы; relative/broken/cycle links, exact paths, Windows reserved/case mapping | Реальные сценарии пройдены |
| 11 | PNG/JPEG | Реальные миниатюры, PNG fit/100%, JPEG next, corrupt error без зависания; лимиты/cancel/cache/generation реализованы | Основные UI-сценарии пройдены; все предельные и changed-during-download случаи открыты |
| 12 | Мониторинг | Реальный /proc без Python/htop; память сравнена отдельно; CPU 96,93% против 96,67%; реальные SIGTERM/KILL своим процессам и PID mismatch | Ядро и оба UI-экрана пройдены указанными измерениями |
| 13 | Ограниченный доступ | Обычный пользователь, SFTP-only сохраняет CRUD; shell-only сохраняет exec/PTY; без sudo | Пройдено restricted-набором |
| 14 | Нагрузка и закрытие | Реальные 10000 файлов: SFTP 0,75 с. Опубликованный GUI: 10000 записей / 16 созданных строк, около 184 МиБ. 2148532224 байта upload+download SHA-256; transfer runner RSS 78–114 МиБ. Отмена 388 мс / close 404 мс | Пройдено указанным объёмом; RSS передач относится к backend runner, не ко всей GUI/WebView2 |
| 15 | Визуальная матрица | Реальные снимки файлов/монитора/терминала. Светлая/системная при светлой Windows и тёмная, 1320×850 и 900×650; тёмное меню | **Полная DPI 100/125/150/200 и High Contrast матрица не проверена** |
| 16 | Самостоятельная поставка | Self-contained .NET 10.0.14 и Fixed WebView2 156.0.4314.58. Опубликованный EXE с несуществующим DOTNET_ROOT использует свои hostfxr/coreclr/WebView2Loader; SFTP/CF_HDROP/терминал работают | Текущий хост пройден; **отдельная чистая Windows 11 и полностью offline испытание ещё требуются** |
| 17 | Нет установки на управляемый сервер | Клиентские команды ограничены SSH/SFTP и bounded /proc, без install/setup/config/systemd/cron/sudo. Установка SSH была только подготовкой отдельной disposable VM | Инспекция команд и стенд подтверждены; production snapshot-аудит не проводился |

## Доказательства

- [TEST_REPORT](docs/TEST_REPORT.md): 34 unit, реальные Linux-наборы, независимые метрики, нагрузка и 6 сетевых отказов. Повторные прогоны не складываются в искусственное общее число.
- [Core smoke](docs/CORE_LIMITATIONS.md): **44/44** — 17 trust/links, 15 key/PTY/restricted, 12 ENOSPC/cross-filesystem.
- [UI/MVVM](docs/UI_IMPLEMENTATION.md): реальные интерфейсные сценарии, **6 WPF + 16 state/binding** проверок и исправленные дефекты.
- [Терминал поставки](docs/test-results/published-terminal.json), [10000 файлов](docs/test-results/published-file-load.json), [Windows CF_HDROP](docs/test-results/published-clipboard.json).
- [Реальные снимки](docs/screenshots): данные из изолированного стенда, без демонстрационных серверов в обычном запуске.

## Подтверждённые ограничения и открытая приёмка

WPF .NET 10 вместо WinUI обоснован штатной DataGrid и OLE FileDrop; MVVM фактически подключён. В 0.1 исходящий FileDrop требовал предварительного скачивания и повторного жеста. В 0.2 этот путь заменён native virtual files; подготовленный TransferCache используется только отдельной командой Ctrl+C. Границы новой живой приёмки указаны выше.

SFTP v3 не обещает atomic compare-and-rename/no-follow/fsync; fallback без POSIX rename может оставить собственный backup после обрыва. Resume с середины не реализован (задание допускает начало заново): retry создаёт новый temporary и снова спрашивает о конфликтах. Unix symlink при скачивании не подменяется целью. Точные границы — CORE_LIMITATIONS.

**Перечисленные непроверенные обязательные сценарии приёмки остаются незавершёнными**, особенно живой Explorer/OLE, полная DPI/High Contrast матрица и отдельная чистая Windows. Реальный Explorer-прогон не засчитан: окно изменялось/закрывалось во время параллельного использования рабочего стола. Сборка не подписана.
