# SSH Explorer — архитектура

## Решения на этапе исследования

- Пустой исходный каталог, Windows x64. Установлен runtime .NET 8, SDK отсутствует. SDK .NET 10.0.403 загружается локально в `.tools`; пользователю SDK не нужен.
- SSH.NET 2026.0.0: прямые SSH/SFTP, отдельные клиенты для независимых подсистем. Логическая вкладка владеет всеми соединениями, отменой и временным доверием.
- Нативный интерфейс: выбран WPF .NET 10 с подключённым MVVM. Штатные DataGrid и OLE FileDrop устраняют риски отсутствующей штатной WinUI DataGrid и обмена файловыми объектами. Обоснование и токены — docs/UI_DESIGN.md.
- Терминал: упакованные xterm.js, addon-fit и addon-search; изолированный WebView2, никакого CDN/HTTP-сервера; фиксированный runtime в поставке.

## Границы модулей

`SSHExplorer.Core`: профили, DPAPI, trust store, SSH session, SFTP, очередь передач, POSIX-пути. Владелец — специалист SSH/SFTP.

`SSHExplorer.Monitoring`: ограниченный пакетный сбор /proc, парсер метрик, история, безопасная проверка PID. Владелец — специалист интеграции.

`SSHExplorer.App`: WPF-представления и отдельные ObservableObject ViewModels Home, Session, FileBrowser/FileItem и Monitor/Process. Реальные DataContext/bindings связывают таблицы, фильтры, выбор, статусы и графики. Generation/history/selection принадлежат FileBrowserViewModel. Диалоги, OLE и визуальные взаимодействия остаются во views. Терминальный WebView2-компонент и интеграция — основной агент.

`tests`, `scripts`, `artifacts`: реальные проверки, воспроизводимая сборка и самостоятельная поставка. Тестовый SSH-стенд изолирован от управляемых серверов.

## Инварианты

1. Host key проверяется до аутентификации каждым клиентом. Изменённый ключ никогда не подтверждается обычным диалогом первого подключения.
2. Нет серверных установок, демонов, изменений конфигурации, автоматического sudo или воспроизведения изменяющих команд.
3. SFTP и exec независимы: отсутствие одного не отключает другой.
4. Файлы передаются потоково во временный sibling; оригинал не удаляется до успешной передачи. Корень и symlink-цели защищены от рекурсивного удаления.
5. Отмена и номер поколения защищают навигацию от поздних ответов. UI не блокируется сетевыми операциями.
6. Метрики получают реальные значения либо причину недоступности. CPU процесса использует явно подписанную шкалу.
7. В журнал не попадают пароли, содержимое ключей, терминала или полные command lines.

## Зафиксированные версии и границы

.NET SDK 10.0.403 / runtime 10.0.14; SSH.NET 2026.0.0; WebView2 SDK 1.0.4314.35 / системный Evergreen Runtime ≥ 120 (дополнительный комплект — Fixed Runtime 156.0.4314.58 x64); xterm 6.0.0, fit 0.11.0, search 0.16.0. NuGet восстанавливается в locked mode. Все терминальные ресурсы локальны, HTTP-сервера и CDN нет. PublishTrimmed=false и PublishSingleFile=false закреплены явно; Windows App SDK не используется.

Серверный вывод передаётся base64→Uint8Array, не HTML/JS. ACK после xterm parser callback ограничивает output одним 16-КиБ блоком in-flight. Resize coalesced, ввод сериализован, OSC52 перехвачен. Внешние навигации/новые окна/permissions/downloads WebView2 запрещены; IPC и ресурсы ограничены локальным terminal.local.

Clipboard sequence guard не даёт ожиданию загрузки перезаписать новый буфер пользователя. Внутренний remote-file payload — непрозрачный 32-байтный token в bounded registry со слабыми ссылками; CLR-объекты не сериализуются в OLE. Токены закрытой сессии удаляются.

SftpProtocolAdapter закреплён на SSH.NET 2026.0.0, проверяет internal signatures и fail-closed предоставляет exact LSTAT/READLINK/SYMLINK/POSIX rename. Причина и реальные проверки — docs/CORE_LIMITATIONS.md.


## Документы внешнего редактора (beta.5)

`RemoteEditWorkspace/RemoteEditSession` в Core хранят exact server+host-key+path identity, working copy, immutable snapshot и atomic operation journal. Watcher лишь планирует стабильное чтение, per-document gate сериализует версии. `IRemoteDocumentTransport` изолирует проверяемое сохранение; SFTP adapter использует bounded auxiliary leases и exact paths, content hash до/после, metadata и POSIX rename. App слой запускает установленный editor EXE с отдельным path argument, показывает `RemoteDocumentsWindow`, обрабатывает lifecycle и выбор конфликтов; editor process никогда не принадлежит client lifetime. После закрытия main server session скрытого подключения нет. [Контракт и ограничения](docs/REMOTE_EDIT.md).
