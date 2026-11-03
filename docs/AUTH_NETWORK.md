# Авторизация и транспорт: проверка для beta.2

03.11.2026, Windows 11 x64 build 26200, .NET SDK 10.0.403 / runtime 10.0.14, SSH.NET 2026.0.0. Проверялось изменённое рабочее дерево поверх `eda137a734e8fc259eb97fca300b2072d11232a8`; это component/network evidence, а не проверка опубликованного beta.2 EXE. [Метаданные и SHA-256 проверенных Core/Smoke binaries и исходников](test-results/beta2-auth-metadata.json).

**Сообщённый пользователем вылет обычного подключения остаётся открытым до воспроизведения соответствующего маршрута.** Ни одна Core-проверка ниже не заменяет «Главная → Добавить → PasswordBox → Сохранить и подключиться → настоящий Trust → вкладка → папка». Эти Core/console/network процессы не создают WPF App. Отдельная ошибка побочного startup в профильном тестовом runner описана в [PROFILE_FLOW_BETA2](PROFILE_FLOW_BETA2.md); её исправление не является диагнозом пользовательского crash. Старая рекурсия выбора профиля — [PROFILE_CRASH.md](PROFILE_CRASH.md).

## Проверенные границы и изменения

- Пароль для SSH password передаётся без `Trim` или иной нормализации. Без интерактивного callback метод password больше не добавляет keyboard-interactive автоматически.
- Явный callback позволяет password → keyboard-interactive и private key → keyboard-interactive для законного MFA. Каждый PAM/OTP-запрос получает отдельный явный ответ. Сохранённый `Secret` никогда не подставляется по тексту запроса, в том числе в `One-time password:`.
- `SessionCredentials.CancellablePrompt` получает token конкретного дополнительного подключения, объединённый с lifetime сессии и timeout авторизации. Старый `Prompt` сохранён для совместимости; его ожидание отменяемо, но сам устаревший delegate не умеет закрывать своё UI. Новое приложение должно использовать `CancellablePrompt`.
- Отмена/ошибка callback возвращается в задачу подключения. Это проверено реальной SSH keyboard-interactive веткой, а не только прямым вызовом helper. В закреплённом [SSH.NET исходнике](https://github.com/sshnet/SSH.NET/blob/7b2fd3dbf2c86a80a7b06cea020aa5f821c9902e/src/Renci.SshNet/KeyboardInteractiveAuthenticationMethod.cs#L100) callback выполняется vendor worker с перехватом исключения и передачей его ожидающему `Authenticate`.
- Повторный `ConnectAsync` той же сессии во время подключения отклоняется. Отдельные экземпляры `ServerSession` независимы. После неверного пароля допустима новая попытка на том же не подключившемся экземпляре.
- Отмена дополнительной SFTP/PTY-авторизации не меняет состояние независимых рабочих exec/SFTP соединений. Закрытие сессии отменяет её дополнительные callbacks и не позволяет позднему завершению вернуть состояние Connected.
- Исправлено владение ещё подключающимся `BaseClient`. В закреплённом [BaseClient](https://github.com/sshnet/SSH.NET/blob/7b2fd3dbf2c86a80a7b06cea020aa5f821c9902e/src/Renci.SshNet/BaseClient.cs#L278) поле Session присваивается после окончания авторизации. Ранний Dispose мог не увидеть локальную Session, к которой поздний успешный вход ещё прикреплял транспорт. Теперь запрос освобождения запоминается, и завершение vendor Connect обязательно освобождает такой клиент. Реальный сервер с задержанной проверкой пароля подтверждает `active connections → 0` после caller cancel и session close.

**Ограничение физической отмены:** SSH.NET 2026.0.0 после открытия socket выполняет часть authentication waits синхронно, без наблюдения caller token. Задача клиента и UI получают отмену сразу; pending транспорт закрывается после vendor unwind, ограниченного настроенным timeout авторизации (3–120 секунд). Проверка с задержанным сервером подтверждает освобождение при позднем успешном ответе; она не доказывает мгновенный обрыв socket во всех состояниях библиотеки. Unsafe reflection к внутренним transport-полям не вводилась.

## Диагностический контракт Core

`ServerSession.StageChanged` передаёт `ConnectionStageEvent`:

- `SessionId`, `OperationId`, `ParentOperationId` — случайные идентификаторы сессии, транспортной операции и исходной попытки;
- `ConnectionStage`: Connect, Transport, HostTrust, Authentication, InteractivePrompt, ExecProbe, Sftp, Pty;
- `ConnectionStageOutcome`: Started, Succeeded, Failed, Cancelled;
- при ошибке — только тип исключения, HRESULT и стек методов без путей исходников. `Exception.Message`, endpoint, username, текст prompt, ответы сервера, команда и credentials не включаются.

Начальные Connect/probe/SFTP события имеют ID исходной попытки, дополнительные transport/PTY операции — собственный ID и parent исходной попытки. Authentication success устанавливается отдельно от SFTP subsystem failure: уже успешный SSH login не объявляется неверным паролем, если сервер отклонил SFTP. Ошибка диагностического подписчика не меняет результат авторизации.

Новый overload `ConnectAsync(credentials, Func<HostKeyPrompt,CancellationToken,Task<TrustDecision>>, ct, operationId)` передаёт отмену также host-trust callback. Старый overload с одним аргументом callback сохранён. Подписку для журналирования следует устанавливать до начала подключения. UI-стадии принятия credentials, создания содержимого и первой навигации принадлежат App и проверяются отдельно.

## Матрица выполненных проверок

| Уровень | Результат | Доказательство / граница |
|---|---|---|
| Auth component, без сети и окон | PASS, 13 assertions | [beta2-auth-component.log](test-results/beta2-auth-component.log): выбор методов, отсутствие подстановки secret, неизменённый callback response, отмена новых/legacy callbacks, pre-cancel operation/dispatch, redaction, общий Dispose task |
| Generated loopback SSH fixture | PASS, 26 assertions | [beta2-auth-scripted.log](test-results/beta2-auth-scripted.log): password с Unicode/пробелами/кавычками, password/key MFA, exec/SFTP/PTY/export, reject trust, неверный→верный пароль, callback throw/null/cancel/timeout, late auth disposal, denied subsystems |
| Свежий independently verified Alpine A | PASS, 5 assertions | [beta2-auth-linux.log](test-results/beta2-auth-linux.log): настоящий OpenSSH password/encrypted Ed25519 login, probe, SFTP, чтение папки, независимые PTY/export/exec |
| Существующие Core/monitor unit + Alpine integration | PASS, 54 assertions | [beta2-core-linux-regression.log](test-results/beta2-core-linux-regression.log): 36 component + 18 реальных network assertions; CRUD/потоковые передачи/конфликты/cancel/undo/symlink/права/мониторинг и независимый CPU-интервал. Это один интеграционный прогон, не 54 пользовательских входа |
| Обычный MainWindow + настоящие Credentials/Trust/Interactive | NOT RUN на host | Окна запрещены; отдельный STA/CI маршрут App не подменяется этими Core-тестами |
| Перезапуск опубликованного portable EXE, живой ввод, DPI/Explorer | NOT RUN | Требуют отдельной разрешённой среды и точного собранного комплекта |

Fixture `scripts/testing/auth_fixture.py` используется только тестами: слушает IPv4 loopback на случайных свободных портах, генерирует свежие host/client keys и секреты в ignored `.tools`, вычисляет fingerprint непосредственно из сгенерированного публичного ключа до подключения. В production приложение его не включает и Python не требует. SFTP fixture читает только свой ограниченный локальный каталог; реальные Linux-проверки используют отдельный известный fingerprint из разрешённого fixture config. Процесс fixture запускается с `CreateNoWindow` и завершается в `finally` runner.

## CI-driver и Python dependencies

[`requirements-auth.txt`](../scripts/testing/requirements-auth.txt) фиксирует проверенные версии Paramiko и зависимостей с SHA-256 Windows x64 wheels для CPython 3.13. Все восемь wheel hashes сверены с официальным PyPI release JSON; offline `pip download --require-hashes` с target `win_amd64/cp313` прошёл. Выполнение этих конкретных CPython 3.13 binaries на host не заявляется: локальные component/network проверки использовали Python 3.10 с теми же версиями пакетов. Настоящее исполнение CPython 3.13 принадлежит CI.

[`Test-AuthNetwork.ps1`](../scripts/Test-AuthNetwork.ps1) по умолчанию собирает и выполняет только headless auth component/scripted network проверки. На host driver прошёл 13 + 26 assertions; создаваемые им console/fixture процессы завершились. Парсер PowerShell и отказ `-RunOrdinaryUi` на обычном host проверены отдельно. Switch `-RunOrdinaryUi` требует одновременно `GITHUB_ACTIONS=true`, `RUNNER_OS=Windows`, числовой `GITHUB_RUN_ID` и `CI=true` **до запуска дочернего процесса**. Только этот режим строит/запускает ProfileTests ordinary STA route. Он использует apphost `.exe` тестового runner, поскольку сценарий нового процесса использует `Environment.ProcessPath`.

Driver пишет только test logs и scope metadata в `artifacts/auth-network`. Fixture config, host/client private keys, пароли и профильное хранилище остаются в ignored `.tools`; они не входят в каталог evidence. Запросы завершения относятся только к созданным driver процессам. Оконный режим driver на host не запускался и не засчитывается как пройденный.

```powershell
# CI после установки CPython 3.13 x64. Это test-only venv, не часть portable поставки.
python -m venv .tools/auth-python
.tools/auth-python/Scripts/python.exe -m pip install --require-hashes --only-binary=:all: -r scripts/testing/requirements-auth.txt
./scripts/Test-AuthNetwork.ps1 -Python .tools/auth-python/Scripts/python.exe -RunOrdinaryUi
# На host — только без switch окон; Python должен уже иметь закреплённые test dependencies.
./scripts/Test-AuthNetwork.ps1 -Python C:/path/to/python.exe
```

## Повторение разрешённых headless проверок

```powershell
dotnet build tests/SSHExplorer.Core.Smoke/SSHExplorer.Core.Smoke.csproj -c Release
dotnet run --project tests/SSHExplorer.Core.Smoke -c Release --no-build -- --auth-unit
# Только для разработки/CI: Python с установленным Paramiko.
dotnet run --project tests/SSHExplorer.Core.Smoke -c Release --no-build -- --auth-scripted C:/path/to/python.exe
# Только независимо проверенный изолированный loopback config, не пользовательский сервер.
dotnet run --project tests/SSHExplorer.Core.Smoke -c Release --no-build -- --auth-linux .tools/iteration2/linux-a/integration.json
dotnet run --project tests/SSHExplorer.Tests -c Release -- --linux-integration .tools/iteration2/linux-a/integration.json
```

Числа выше — assertions соответствующего уровня. Они не суммируются в число end-to-end пользовательских маршрутов и не доказывают устранение текущего сообщения о вылете.
