# SSH Explorer — фактическая приёмка

03.11.2026. Клиент: Windows 11 x64 build 26200. Изолированный стенд: Alpine 3.24.3 / Linux 6.18 / OpenSSH 10.3, обычный пользователь, ext4 и маленький tmpfs. Источник: [docs/TASK.ru.txt](docs/TASK.ru.txt).

**Рабочая самостоятельная поставка и исходники готовы. Полная обязательная приёмка ещё не завершена:** оставшиеся ручные и внешние проверки перечислены явно. Наличие кода не засчитывается как пройденный ручной тест.

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

WPF .NET 10 вместо WinUI обоснован штатной DataGrid и OLE FileDrop; MVVM фактически подключён. Исходящий FileDrop использует подготовленные локальные файлы, а не deferred virtual files: нужны место и предварительное скачивание. При раннем отпускании мыши подготовленные файлы доступны через буфер/повторное перетаскивание. Кэш очищается явно.

SFTP v3 не обещает atomic compare-and-rename/no-follow/fsync; fallback без POSIX rename может оставить собственный backup после обрыва. Resume с середины не реализован (задание допускает начало заново): retry создаёт новый temporary и снова спрашивает о конфликтах. Unix symlink при скачивании не подменяется целью. Точные границы — CORE_LIMITATIONS.

**Перечисленные непроверенные обязательные сценарии приёмки остаются незавершёнными**, особенно живой Explorer/OLE, полная DPI/High Contrast матрица и отдельная чистая Windows. Реальный Explorer-прогон не засчитан: окно изменялось/закрывалось во время параллельного использования рабочего стола. Сборка не подписана.
