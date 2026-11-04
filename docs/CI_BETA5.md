# Проверки beta.5

Baseline: immutable `v0.2.0-beta.4` / `1fb919fbf87ff0baa6ecf6fcca242a6b68e614fb`, portable SHA256 `08474ef3ac57dc1f7938c39050469666d9cda63d6d8a739d974adba709442c71`. Перед render старый ZIP проверяется целиком и используется отдельным baseline harness; пересборка старого кода не подменяет выпуск.

## Конкретные уровни доказательств

| Уровень | Результат / граница |
|---|---|
| Документные компоненты | 15 целевых PASS: strict encoding/BOM/UTF-16, empty/missing, immutable snapshots, rapid/atomic saves, same-length/mtime conflict и повторный конфликт, format approval, pre/post-commit failure/recovery, cancellation/retry, paused restore, identity/dedup, 10 МиБ. Это controlled transport, не внешний редактор. [Лог](test-results/beta5-development-document-components.log) |
| Настоящий SFTP | [6 development checks](test-results/beta5-development-document-sftp.log): байты .sh/.conf/.txt, UTF-8 BOM/UTF-16/смешанные окончания/пустой текст/mode0755, внешний writer с восстановленной mtime, explicit symlink/retarget, 10 МиБ, metadata conflict, отмена до rename. Не полный GUI-маршрут |
| SFTP-only / Linux отказы | [11 PASS](test-results/beta5-development-sftp-only.log): exec отклонён, SFTP доступен; предыдущие общие сценарии повторены и не суммируются как новые; дополнительно real permission denied, FIFO refusal, невозможность сохранить root owner, реальный ENOSPC на собственном tmpfs192KiB. Оригинал и полный local snapshot сохранены. Стенд создан отдельно; управляемый сервер не меняется |
| Установленный Блокнот | [Development proof](test-results/beta5-development-notepad.json): настоящий Notepad11.2607.14.0, видимое открытие `.sh` как текста, изменение и Ctrl+S через разрешённый Computer Use; независимый SFTP readback совпал с local bytes/baseline SHA и mode0755. После закрытия клиента Блокнот остался открыт. Это синтетический ввод инструмента, не физический ввод человека и не обычная auth-проверка; TestSessionHarness использовал отдельные credentials/trust. Final release DLL этим не подменяется |
| Native Shell | [5 PASS](test-results/beta5-development-shell-icons.log): реальные frozen resources 16/32/48/256, dedup1000, отсутствие Shell-запросов для 10 000 нематериализованных моделей, fractional-size bucket и environment invalidation. Нет document file queries/execution и показанных окон |
| Windows CI | Реальные WPF HWND/layout/templates и программные control events; только guarded software SendInput в собственном CI окне для удерживаемой мыши. Документная галерея использует controlled transport и launcher stub, **не Notepad**. Содержимое рабочих документов остаётся в `.tools`, не artifact directory |
| Полный CI | Сохранены core, resources/MVVM, motion clocks, connection/drag/file state, monitor, 113 profile/STA, 13 auth component +26 loopback network, ordinary Add/Trust/Connect/fresh child, native COM ABI, self-contained package и отдельный packaged EXE startup/normal close |

Приостановка/ошибка/UNKNOWN и format decision не выдаются за успешное сохранение. Отчёты не содержат рабочие документы, секреты или тестовые профили. Никакие данные пользователя в Notepad session storage/памяти не читались; собственный тестовый документ наблюдался через обычный UI.

## Проверенный checkpoint

`e6c4bc47abf82bc733ecf86a80062ec4de1f0301`: [полный CI SUCCESS](https://github.com/Jetvac/ssh-explorer/actions/runs/41787035963), [UI preflight SUCCESS](https://github.com/Jetvac/ssh-explorer/actions/runs/41787035972). 64 core assertions включают 15 документных, не считаются как64+15. 48 monitor/shell, 38 Files, 24 Forms/Tabs и17 document/native-file checks. Пять наборов содержат 64 исходных PNG: before9, after16, interactions12, forms4, documents23. SHA256 artifact ZIP `d71b606c017caf9ca2332775a7f84cb6206bdec0ae042df1c8295c64e4a4be8e` и все PNG проверены перед просмотром.

Независимые процессы monitor/Files/Forms/Documents не скрывают сбой соседнего набора. Comparison ZIP создаётся только после всех четырёх PASS. После просмотра предыдущего checkpoint исправлены прозрачная подложка document root и нехватка места для строки при enlarged text. Новый прогон проверяет реальные измерения, восемь видов, длинные имена, активный view glyph, отличие Edit от Image Preview, прогресс байтов сохранённого snapshot и доступность отмены. Первая проверка меню выявила ошибку fixture: disconnected backend закономерно запрещал изменение; fixture теперь предоставляет существующий controlled available backend. Production availability checks сохранены. Финальные числа/PNG/provenance повторно проверяются из нового tag CI, без переноса хешей checkpoint.

## Версия и происхождение финального выпуска

[Релиз beta.5](https://github.com/Jetvac/ssh-explorer/releases/tag/v0.2.0-beta.5) связывает точный tag/commit, успешный Windows CI и portable/source/UI ZIP. [Отчёт независимого повторного скачивания](https://github.com/Jetvac/ssh-explorer/releases/download/v0.2.0-beta.5/SSHExplorer-0.2.0-beta.5-verification.json) проверяет outer GitHub digests, внутренний манифест portable, все Git blobs source ZIP, SHA каждого исходного PNG, совпадение render DLL с portable DLL и startup report точного тега. SHA256 отчёта находится также в release notes; собственный hash не включается рекурсивно в него самого.

Текущие кадры пяти наборов: `ui-beta5-before` (beta.4), `ui-beta5-after`, `ui-beta5-interactions`, `ui-beta5-forms`, `ui-beta5-documents`. Metadata содержит commit/DLL hash, actual client size, DPI, role font, culture, theme, active/focus/sort, software-input level. PNG не ретушируются. Для common пяти пар проверяется совпадение условий. Разные document/native views имеют собственные измерения, не объявляются одинаковой геометрией с исходным Explorer.

## Разрешения и открытая приёмка

Computer Use был разрешён и затем отозван 04.11.2026. После отзыва нет нового управления рабочим столом/host HWND. Уже захваченный reference экспортирован из памяти без нового capture/input; live proof остаётся привязан к прежней development DLL. Историческая beta.4 сохраняет свой запрет и свои факты, не переписывается новым разрешением.

NOT RUN как полная матрица: физическая authentication/file keyboard/mouse, Explorer OLE gestures, native DWM/caption/Snap и multiple monitors, motion video, OS DPI100/125/150/200/text scale/High Contrast, чистая Windows. Программный font22 не объявляется изменением OS DPI. Прежний парольный crash остаётся с неизвестной причиной. [Сценарии ручной приёмки](../ACCEPTANCE.md). Пользовательская визуальная оценка открыта.
