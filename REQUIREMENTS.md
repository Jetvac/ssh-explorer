# SSH Explorer — требования и реализация

Полный источник: [docs/TASK.ru.txt](docs/TASK.ru.txt). Обязательный объём не сокращён. Фактический статус каждого из 17 сценариев — в [ACCEPTANCE.md](ACCEPTANCE.md).

| Раздел | Реализация | Проверка / фактический статус |
|---|---|---|
| 1. Локальность, SSH/SFTP, без серверной установки | Прямой SSH.NET, самостоятельный комплект | Реальный Linux и инспекция команд; чистая Windows открыта |
| 2. Организация | Три специалиста, контракты, документы, интеграция | Выполнено, открытая приёмка обозначена |
| 3. Стек, MVVM, lifecycle | C# .NET 10 LTS, WPF MVVM, SSH.NET, local xterm/WebView2 | Release clean; unit/WPF/state/real network проверки |
| 4. Windows 11 | Токены, темы, штатная рамка, таблицы и меню | Реальные light/dark и два размера; DPI/High Contrast открыты |
| 5. Профили, auth/trust/secrets | Password/key/passphrase/interactive, endpoint host keys, DPAPI | Unit и реальные handshake/key; ручные auth-диалоги не автоматизировались |
| 6. Вкладки | Изолированные ресурсы, номера, контекст, reconnect | Изоляция/close пройдены; полная ручная матрица открыта |
| 7. Терминал | PTY, ANSI/UTF-8, resize/search/font/paste/alt screen | Реальные PTY и опубликованный xterm; полный набор программ открыт |
| 8. Файловый UI | Lazy tree, POSIX paths/history, 8 virtualized видов, колонки/выбор/поиск | Виды и 22 WPF/state/binding проверки; вся ручная матрица открыта |
| 9. Операции и Windows | Queue, temp/commit/conflicts/undo, native FileDrop/clipboard | Реальные transfers/failures и системный CF_HDROP; живой Explorer/OLE открыт |
| 10. Linux различия | Exact paths, links, permissions, owner/group | Реальные hostile names/links/permissions пройдены |
| 11. PNG/JPEG | Клиентский decode, viewer, limits/cancel/cache | Основные UI-превью пройдены; все граничные случаи открыты |
| 12. Мониторинг | Bounded /proc, counter deltas, graphs, identity-safe signals | Реальный Linux без Python/htop, независимые метрики и сигналы пройдены |
| 13. Эксплуатация | Versioned atomic JSON, настройки, ограниченный журнал | Restart/DPAPI пройдены; весь ручной error/settings набор открыт |
| 14. Приёмка | Матрица 17 сценариев и доказательства | Значительный объём пройден, полная приёмка не объявлена завершённой |
| 15. Код и поставка | Solution, locked versions, build script, portable/source ZIP, licenses | Release и runtime-origin на текущем Windows пройдены |

Дополнительные jump hosts, forwarding, архивы, systemd, редактор и синхронизация не расширяли объём вместо обязательных функций.
