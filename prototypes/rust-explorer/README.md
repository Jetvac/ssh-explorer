# SSH Explorer — Rust-прототип (изолированный эксперимент)

Это **не** замена приложения и не часть выпуска. Прототип проверяет трудные границы нативного стека без браузерного runtime, чтобы решение о миграции опиралось на измерения. Основная версия — WPF в `src/`. Сравнение вариантов: [PACKAGING_AND_PERFORMANCE.md](../../docs/PACKAGING_AND_PERFORMANCE.md).

## Состав

| Крейт | Содержимое |
|---|---|
| `core` (`sshx-core`) | VT-терминал на `vte` (SGR 16/256/RGB, широкие символы, альтернативный экран, scroll regions, DSR/DA, bracketed paste, поиск, выделение); типизированная сортировка (естественные имена, папки первыми, неизвестное в конце); хранилище host keys (Trusted/Unknown/Changed); SSH/SFTP/PTY на `ssh2` (libssh2, на Windows — WinCNG, без OpenSSL) |
| `app` (`sshx-explorer`, бинарник `sshx-rust-prototype.exe`) | Win32 через `windows-sys`: окно с системным заголовком и Snap, per-monitor DPI v2, Common Controls v6 и иконка приложения из встроенного ресурса; вкладки «Файлы/Терминал»; редактируемый путь (Ctrl+L/Alt+D, Enter, Escape, устаревшие ответы отбрасываются); ленивое дерево; owner-data ListView с сортировкой заголовками и иконками Shell по расширению; терминал на GDI с двойной буферизацией; Ctrl+Shift+C/V, Shift+Insert, правый клик; поиск; OLE `IDropTarget` для загрузки файлов из Проводника |

Host key проверяется **до** отправки пароля. Неизвестный ключ требует явного согласия с показом SHA-256. Изменённый ключ блокирует подключение и никогда не перезаписывается. Upload открывает файл с `CREATE|EXCLUSIVE` и не заменяет существующий. Прототип хранит доверие в `%APPDATA%\SSHExplorerRustPrototype\known_hosts` и не читает профили, секреты и `hostkeys.json` основной версии.

## Сборка и проверки

```powershell
cd prototypes/rust-explorer
cargo test --workspace --release           # Windows: ядро + текстовые помощники окна
cargo build --release -p sshx-explorer     # target/release/sshx-rust-prototype.exe, статический CRT
target/release/sshx-rust-prototype.exe --selftest report.json
```

`--selftest` работает в настоящем окне. Он записывает размер EXE, время до первой отрисовки от создания процесса, память (working set и private), заполнение списка из 100 000 строк, клик заголовка через SendInput, разбор терминального потока, время кадра 160×48 и сортировку. Рядом сохраняются два BMP. Данные синтетические, SSH не используется. Это CI-рендер: он не доказывает физический ввод, OS DPI или High Contrast.

Live-тесты ядра (`core/tests/live_ssh.rs`) выполняются только при заданном `SSHX_LIVE_HOST` и только против одноразового тестового сервера на той же машине. Переменные описаны в заголовке файла. Запуск: `cargo test -p sshx-core --release -- --test-threads=1`.

Хост-независимый замер: `cargo run --release -p sshx-core --example bench`.

CI: [.github/workflows/rust-prototype.yml](../../.github/workflows/rust-prototype.yml).

- Windows-задание: тесты, clippy, release-сборка, self-test с проверками (Common Controls v6, иконка в EXE, RegisterDragDrop, отсутствие re-entrancy, сортировка после клика), PNG-снимки, иконка из EXE, размер EXE/ZIP.
- Linux-задание: clippy и live-тесты против одноразового OpenSSH `sshd` на runner (127.0.0.1:2222, собственный host key и конфиг).

## Что проверено, а что нет

Проверено в Linux dev-контейнере:

- 21 тест ядра; 8 из них — против настоящего SSH-сервера (libssh2 + OpenSSL ↔ локальный paramiko);
- type-check и `clippy -D warnings` всего workspace, включая Win32-код;
- `.res` с иконкой и манифестом побайтно совпадает с выводом `llvm-rc`.

Числа: [rust-prototype-dev-container.json](../../docs/perf/rust-prototype-dev-container.json).

**Не проверено:**

- сборка и запуск EXE на Windows; окно, DPI, отрисовка, ввод;
- drop из Проводника, буфер обмена, IME;
- TUI-приложения (mc, htop, vim) в этом renderer;
- live-тесты против OpenSSH.

Workflow подготовлен, но не запускался, потому что push из рабочей сессии заблокирован.

Сознательно не реализовано: редактирование файлов, скачивание и DnD сервер → Windows (virtual files), очередь передач, мониторинг, профили и DPAPI, ключи с passphrase-диалогом, keyboard-interactive, тёмная тема.
