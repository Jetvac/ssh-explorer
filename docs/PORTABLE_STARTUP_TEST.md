# Проверка запуска portable-комплекта в CI

`scripts/Test-PortableStartup.ps1` запускается **только** в Windows GitHub Actions после сборки точного чистого комплекта. До любого Process.Start проверяются GITHUB_ACTIONS, CI, Windows/runner, run id, workspace, ожидаемый commit и точная versioned-папка `artifacts/SSHExplorer-<version>-win-x64`. Затем `Verify-Portable.ps1` проверяет происхождение, version resources и контрольные суммы без исполнения PE.

```powershell
.\scripts\Test-PortableStartup.ps1 -Directory artifacts/SSHExplorer-0.2.0-beta.2-win-x64 -ExpectedCommit <40-character-commit> -ExpectedVersion 0.2.0-beta.2
```

Это команда **для workflow**, а не инструкция запуска на пользовательском desktop. В текущей итерации на host выполнена только синтаксическая PowerShell parser-проверка; сам probe локально не запускался.

Probe создаёт новую ignored `.tools/portable-startup/<run-id>-<guid>` и передаёт обычному packaged EXE только стандартный `--settings-dir`. TestSessionHarness, supplied credentials, host trust override, UI automation, SendKeys и ввод пароля не используются. `CreateNoWindow=true` не является доказательством отсутствия GUI WPF: EXE действительно создаёт главное окно в разрешённой CI-среде.

Для PASS требуются собственный Process.MainWindowHandle, `app.started` с правильными PID/build, отсутствие диагностических ошибок, CloseMainWindow именно своего Process, ExitCode 0, `app.close.completed` и `app.exit` с outcome 0. После spawn процесс имеет общий бюджет 60 секунд: 40 на startup, graceful close до 55-й секунды и остаток до 60 на аварийную очистку. Hash-verification перед spawn не входит в этот бюджет. В finally разрешено завершить только созданный Process, без поиска по имени или убийства дерева процессов.

Артефакты workflow:

- `artifacts/portable-startup.json` — версия/commit/run id, наблюдаемые стадии, результат/exit code и наличие принудительной очистки.
- `artifacts/portable-startup-diagnostics.jsonl` — только whitelist utc/build/PID/stage/outcome/hasException из нового изолированного журнала. Настройки, профили, сообщения исключений, server responses, environment strings, stack и исходные файлы журналов не публикуются.

Результат относится к **запуску и обычному закрытию опубликованного EXE в CI**. Он не проверяет пользовательский парольный маршрут, настоящую клавиатуру/мышь, Explorer DnD, визуальное соответствие, WebView2-терминал, отсутствие глобальных runtime на машине или отдельную чистую Windows. Отсутствие CI desktop/главного handle даёт FAIL, а не фиктивный PASS. Текущий повторный пользовательский crash не объявляется исправленным по одному startup probe.
