# SSH/SFTP: реализация, ограничения и проверки

## Выполнено

- SSH.NET **2026.0.0**, .NET 10; зависимости закреплены в csproj.
- Собственное хранилище host keys: DNS/IPv4/IPv6 endpoint, порт, алгоритм, публичные байты, SHA-256 и время доверия. Все соединения проходят один verifier до user authentication. Изменение ключа или ранее доверенного алгоритма блокирует подключение. Разовое доверие ограничено логической сессией; одновременно создаваемые каналы объединяют запросы.
- Пароль, приватный ключ, зашифрованный приватный ключ с passphrase и keyboard-interactive с отдельным UI callback для каждого запроса (включая OTP). SSH-agent forwarding не включён.
- Секреты только по настройке профиля: DPAPI CurrentUser, отдельный файл с ciphertext; JSON содержит настройки без secret. Данные лежат в `%LOCALAPPDATA%\SSHExplorer`; записи JSON атомарны, с `.bak`.
- Отдельные соединения для exec, навигации, передачи и каждой PTY. Передачи используют ограничение параллелизма 2; завершённые клиенты удаляются из набора ресурсов. Закрытие session отменяет lifetime и освобождает clients/PTY/keys. Закрытие дочернего PTY освобождает его client.
- SFTP file operations: create, rename, stat, chmod, chown, recursive delete (без следования symlink), read preview with size/race checks, explicit folder size calculation, explicit follow-link. Перемещение/удаление корня запрещено.
- Общая очередь upload/download/remote copy/move. Буфер потока 128 КиБ, `long` sizes, временный sibling `.sshexplorer-<id>.part`, сохранение источника при неуспешной передаче. Имя назначения не очищается перед загрузкой.
- Существующий файл заменяется только при финализации. Используется `posix-rename@openssh.com` при поддержке; иначе existing → backup, temporary → destination, затем удаление собственного backup с попыткой rollback при сбое. Каталоги объединяются; чужие вложенные объекты не удаляются.
- Generic SFTP failure при записи дополняется проверкой существующего `statvfs@openssh.com`, если сервер поддерживает расширение. Подтверждённые нулевые доступные blocks/inodes получают точное русское объяснение нехватки места; при отсутствии расширения или потерянной связи сохраняется исходная серверная ошибка.
- При конфликте можно заменить, пропустить, сохранить оба либо отменить. Resolver получает JobId и Attempt, чтобы UI применял «ко всем» только к текущей попытке. Retry не запускается автоматически.
- При move сначала копируется и проверяется обычный файл, затем заново проверяется источник и удаляется. Непустой каталог с новыми или пропущенными объектами не удаляется. Same-endpoint move без конфликта сначала пробует SFTP rename с undo.
- Windows names: предлагается безопасное имя с сохранением `.sshexplorer-name-map.jsonl`; обрабатываются reserved names, forbidden characters, case collisions, trailing spaces/dots и длина. Lexical containment и reparse checks запрещают выход через локальные ссылки.
- Undo хранит до 20 переименований/rename-move без перезаписи. Проверяются метаданные, содержимое обычных файлов (SHA-256), цели symlink и дерево каталогов. Изменение содержимого той же длины с восстановленной mtime обнаруживается. Бюджет безопасного undo — 256 МБ содержимого, 100 000 объектов и глубина 128; при недоступном чтении или превышении бюджета само переименование выполняется, undo очищается и `LastUndoUnavailableReason` объясняет причину.

## Адаптер exact SFTP paths

У SSH.NET 2026.0.0 высокоуровневые `Get`, `GetAttributes`, `DeleteFile`, `RenameFile`, `SymbolicLink` выполняют REALPATH над аргументами. Это может разыменовывать последний symlink; `SymbolicLink` также меняет смысл относительной цели. Публичного READLINK нет.

`SftpProtocolAdapter` изолированно вызывает уже реализованные в закреплённой библиотеке SFTP-v3 запросы LSTAT, READLINK, SYMLINK и POSIX rename, проверяя имена и сигнатуры во время выполнения. Используется reflection к internal API. При несовпадении версии/сигнатур операции прекращаются; небезопасного fallback нет. SFTP chroot не переводится в shell paths. Приложение **нельзя публиковать с trimming** без замены этого адаптера проверенным публичным API или vendored fork.

## Подтверждённые границы

- SFTP v3 не даёт универсальных atomic compare-and-rename, openat/no-follow и stable inode identity. Проверки изменения выполняются перед действием; между проверкой и действием остаётся гонка при одновременных внешних изменениях. Атомарная гарантия не заявляется.
- Без POSIX rename fallback имеет промежуток между backup и destination. При обрыве в нём собственный `.backup` может остаться рядом; не удаляется автоматически при следующем подключении.
- Flush SFTP stream подтверждает завершение запросов записи, но не обещает физический fsync на удалённом диске.
- При замене обычного файла сохраняются POSIX mode bits. Владелец/группа нового temporary определяется сервером и правами учётной записи; их совпадение с прежним файлом не обещается. Доступно отдельное изменение owner/group в свойствах.
- Локальные reparse points и symlinks не обходятся автоматически. Скачивание самой Unix symlink в Windows не подменяется скачиванием цели. Для работы с целью есть отдельное явное FollowLinkAsync; remote-to-remote copy переносит саму ссылку с исходной относительной/абсолютной целью.
- Символические ссылки не становятся рекурсивными узлами удаления/копирования. Права самой symlink через SFTP SETSTAT не меняются, поскольку сервер может изменить цель. Сокеты, FIFO и устройства не скачиваются как обычные файлы.
- Возобновление `.part` не реализовано: Retry начинает с нового собственного temporary и заново разрешает конфликты. Автоматического удаления чужих partial/backup нет.
- `ConnectionInfo.Encoding` использует strict UTF-8: некорректно кодированные имена приводят к явному отказу вместо destructive операции по искажённому имени.

## Фактические проверки

- `dotnet build src/SSHExplorer.Core/SSHExplorer.Core.csproj -c Release --no-restore` — пройден, 0 warnings/errors.
- Runtime signatures SftpProtocolAdapter, host trust, POSIX quoting/paths и Windows-name guard — включены в `SSHExplorer.Tests`; фактический результат общего прогона записывается главным агентом.
- `dotnet run --project tests/SSHExplorer.Core.Smoke -c Release -- .tools/vm/integration.json` — **17/17 реальных проверок пройдено** на изолированном Alpine 3.24.3/OpenSSH, Windows 11 x64, 03.11.2026. Проверены отказ доверия, неверный пароль, changed-key блокировка handshake; relative/broken/cycle links, explicit follow, rename/undo ссылки, remote copy с точной целью, overwrite symlink без изменения target, sticky mode 1640, безопасное рекурсивное удаление и очистка собственных temporaries. Каждый прогон создаёт отдельный `sshx-test-core-<UUID>` и удаляет только его.
- Дополнительно **15/15 проверок** зашифрованного Ed25519-ключа, реального PTY, UTF-8/кириллицы/emoji, разрыва UTF-8 между блоками, resize по `stty size`, Ctrl+C/Ctrl+D, сохранения shell state при отдельном exec и независимости SFTP-only/shell-only подсистем. Для ограниченных режимов использованы отдельные свежие аккаунты и два дополнительных OpenSSH listener в disposable VM; основной sshd и его действующие каналы не перезапускались.
- Дополнительно **12/12 проверок** реальных ENOSPC и cross-filesystem move: ext4 → отдельный tmpfs 2 МБ. Запись 8 МБ поверх правильного файла завершилась ошибкой с подтверждённым через statvfs объяснением нехватки места; SHA-256 назначения и локальный исходник сохранены. Неуспешный move сохранил remote source и его SHA-256; успешный малый move прошёл через потоковый fallback и удалил source только после завершения. Удалены только собственные `sshx-test-failure-<UUID>`.
- Окончательный полный прогон **44/44 PASS**, exit code 0; автоматический лог: [test-results/core-smoke.log](test-results/core-smoke.log). В этом прогоне также подтверждена исправленная очистка SSH-client при отказе открытия PTY. Release компиляция тестового набора — без предупреждений/ошибок.
- Общие SFTP/PTY/cancel/>2GiB/monitoring сценарии относятся к общему интеграционному набору; их окончательные результаты записывает главный агент.

### Команды воспроизведения

Все конфиги и секреты стенда находятся только в ignored `.tools/vm`; их нет в исходниках или поставке приложения. Стенд — отдельная тестовая VM, не управляемый приложением сервер.

```powershell
# Полный Core smoke: 17 + 15 + 12 проверок
dotnet run --project tests/SSHExplorer.Core.Smoke -c Release -- .tools/vm/integration.json
# Только encrypted key, PTY и ограниченные режимы
dotnet run --project tests/SSHExplorer.Core.Smoke -c Release -- .tools/vm/integration.json --connections-only
# Только ENOSPC и move между ext4/tmpfs
dotnet run --project tests/SSHExplorer.Core.Smoke -c Release -- .tools/vm/integration.json --failures-only
```

`tests/fixtures/provision-modes.py` является только инструментом разработки. Он подготавливает disposable VM через root serial; режим `--relay` создаёт временный локальный TCP-мост к двум дополнительным тестовым SSH-портам. В клиенте SSH Explorer ни этот скрипт, ни Python, ни relay не используются. После проверок дополнительные listeners и relay helpers остановлены; основная VM и SSH-порт 22222 оставлены для других испытаний.


## Документное сохранение beta.5

Новый `SftpDocumentTransport` отделён от обычного Transfer CommitRemote: дважды сверяет server content hash, пишет own exclusive sibling, проверяет mode/uid/gid и перечитывает destination после POSIX rename. Для unsupported rename нет скрытого delete/backup fallback. Это всё ещё не SFTP hash CAS: остаётся окно внешнего writer, Flush не доказывает fsync, ACL/xattr/hardlinks не сохраняются. Перед открытием показаны условия; при конфликте/неизвестном результате локальный snapshot остаётся. Обрыв может помешать cleanup собственного temporary; клиент не удаляет неизвестное назначение и не делает слепой rollback. [Полные границы и восстановление](REMOTE_EDIT.md).
