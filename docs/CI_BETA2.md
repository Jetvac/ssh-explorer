# CI beta.2: фактические проверки

**Актуальная сводка после публикации beta.2:** релиз `v0.2.0-beta.2` собран из `e8424e548e89b8406444d3122157542597c3c33f`. Финальный [CI 41651021287](https://github.com/Jetvac/ssh-explorer/actions/runs/41651021287) прошёл; опубликованные ZIP повторно скачаны, проверены все **816 файлов portable и 252 git blobs исходников**. [Публичный отчёт проверки](https://github.com/Jetvac/ssh-explorer/releases/download/v0.2.0-beta.2/SSHExplorer-0.2.0-beta.2-verification.json). Формулировки ниже о 785 файлах и будущем скачивании относятся к ранним checkpoint ab01976/4895d45, а не к состоянию завершённой поставки. Исторические результаты и хеши не изменены.

Последующий кодовый checkpoint `4895d45687e662fa4586f5eb7642127b299ac199` также полностью прошёл [CI 41649603241](https://github.com/Jetvac/ssh-explorer/actions/runs/41649603241). После найденного живого дефекта добавлены 4 проверки фокуса: теперь **16 UI component checks**; [выдержка](test-results/beta2-ci-focus-4895d45.log). Живой повтор исправленного пакета — [LIVE_BETA2](LIVE_BETA2.md). Ниже сохранены точные числа/бинарные хеши первого успешного checkpoint ab01976; это исторические результаты своего commit, не финальные hashes будущего релиза.

[GitHub Actions 41646603882](https://github.com/Jetvac/ssh-explorer/actions/runs/41646603882) завершился **SUCCESS** 03.11.2026 в 13:44:24 UTC. Проверенный исходный commit — [`ab019761db7cb2d0ae401dcebb1c3ca2ec9cb34d`](https://github.com/Jetvac/ssh-explorer/commit/ab019761db7cb2d0ae401dcebb1c3ca2ec9cb34d), версия `0.2.0-beta.2`. Все этапы workflow, включая ordinary authentication route и packaged startup, прошли. Это checkpoint конкретного commit; поздние изменения требуют собственного CI.

Публичные выдержки и JSON взяты из `Windows-regression-report` artifact этого run. [Манифест доказательств и SHA256](test-results/beta2-ci-ab01976-evidence.json). У component-журнала сохранены только PASS/RESULT/SAFETY, безопасные снимки окон и descriptor count. Заголовок PowerShell с runner account/machine и прочее окружение исключены. Секреты fixture остались в ignored `.tools` и не загружались.

| Уровень | Фактический результат | Граница доказательства |
|---|---|---|
| Сборка и регрессии ядра | Release 0 warnings/errors; 36/36 core assertions | Без утверждения о полном GUI-маршруте |
| WPF layout/state/components | 6 layout/virtualization + 18 MVVM + 12 UI component assertions | Реальные WPF контроли и layout; не снимки, физическая клавиатура или все DPI |
| Resources/MVVM | 8 resource/contract + 18 MVVM assertions | Повторные MVVM assertions не суммируются в новые маршруты; load resources не доказывает живой HC |
| Motion | 17 реальных clock/contract assertions | Прерывание/отключение/unload/theme; без окна/Popup и без визуальной оценки движения |
| Operations/diagnostics/transfers | 13 assertions | Dedup/cancel, безопасные durable diagnostics, устойчивые строки очереди |
| Профили и модальные окна | 110 STA assertions | Реальные Profile/Credentials/Trust/Interactive, owner, worker cancellation, owner close; это события контролов тестового runner |
| Native COM | ABI-only PASS, 8 protocol/scope assertions | Отдельный native consumer, QI/FILEDESCRIPTORW/indexed IStream/EndOperation, synthetic zero-byte entry; в этом CI не было SSH, >2 ГБ или Explorer gesture |
| SSH authentication components | 13 assertions | Без окон и сети; callback contract, точность ответа, cancellation и diagnostics |
| SSH authentication по сети | 26 assertions | Изолированный loopback Paramiko fixture: password Unicode/пробелы/кавычки, wrong→correct retry, MFA/OTP, timeout/cancel, auxiliary PTY/export, subsystem refusal |
| Обычные команды Add→PasswordBox→Trust→SFTP | 8 parent assertions PASS и успешный новый тестовый процесс | Настоящие команды MainWindow и диалоги, независимо заданный fingerprint, фактическая SFTP README, deferred DPAPI; без suppliedCredentials/trustOverride |
| Проверка собранного portable каталога | PASS, 785 файлов | Версия/commit/hashes, self-contained runtime и WebView2; каталог CI, не повторно скачанный GitHub Release ZIP |
| Собранный portable EXE | PASS, exit 0, normal close | Новый процесс, isolated fresh settings, own main HWND, app.started/close.completed/exit; без пароля/физического ввода/чистой Windows |

Числа обозначают assertions разных наборов. Они не складываются в количество пользовательских маршрутов. [Component/STA/ABI журнал](test-results/beta2-ci-ab01976-components.log), [auth component](test-results/beta2-ci-ab01976-auth-component.log), [auth network](test-results/beta2-ci-ab01976-auth-network.log).

## Обычный парольный маршрут

[Журнал](test-results/beta2-ci-ab01976-ordinary-sta.log) подтверждает Profile PasswordBox → «Сохранить и подключиться» → реальный Trust dialog с независимым fingerprint → Connected/SFTP → README. Оптированный секрет отсутствует до аутентификации и появляется в DPAPI после успешного подключения. Первый MainWindow действительно закрыт до запуска нового тестового процесса. Новый процесс использовал обычную кнопку Connect и сохранённый секрет/host trust без повторного запроса. Отдельный child stdout в artifact не сохранён; его успех подтверждён parent assertion с проверкой exit/summary, без дополнительного счётчика.

[Scope JSON](test-results/beta2-ci-ab01976-auth-scope.json) прямо отмечает: `CI STA component + real network + fresh test process`; физический ввод и published EXE password route в этом наборе не выполнялись. `App(startDesktop:false)` подавляет только автоматический startup из очереди WPF. Пользовательские Add/Connect и настоящие диалоги не заменяются тестовыми callback.

## Portable startup

[Статическая проверка](test-results/beta2-ci-ab01976-portable-verification.json): commit совпал, dirty=false, 785 файлов проверены; .NET runtime `10.0.14`, Fixed WebView2 `156.0.4314.58`, подпись отсутствует. EXE SHA256: `1e461040d6c26e384666b702787e4a0ad61938f3c4dd2fd18cad886a5ca769bc`; App DLL SHA256: `50f5cf2c81ada2bb2d7f7ff9d33faaf4354e6b5cd8602fa1d4eda46fc18a9aef`.

[Process probe](test-results/beta2-ci-ab01976-portable-startup.json) наблюдал собственный HWND, запросил штатное закрытие и получил exit 0 за 787 мс без forced termination. [Безопасные lifecycle-события](test-results/beta2-ci-ab01976-portable-startup-diagnostics.jsonl) относятся к одному PID и той же сборке. Probe запускал EXE из собранного CI portable каталога. Он не вводил пароль, не проверял повторный вход после перезапуска опубликованной поставки и не подтверждает чистую Windows.

## Открытые границы

P0 пользовательского сообщения о парольном crash остаётся **открытым**: именно это падение актуальной поставки не воспроизведено. CI подтверждает перечисленные устранённые дефекты и рабочие границы, но не устанавливает неизвестную причину падения.

Скачанный опубликованный beta.2 ZIP ещё не проверен этим отчётом. Физическая клавиатура, live Explorer/OLE, видео анимаций, полная DPI/High Contrast матрица и отдельная чистая Windows имеют самостоятельные статусы в [ACCEPTANCE](../ACCEPTANCE.md). Этот документ не меняет их на PASS.
