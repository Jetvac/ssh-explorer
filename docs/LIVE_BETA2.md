# Живые проверки beta.2 — 03.11.2026

**Актуальная сводка после публикации beta.2:** релиз `v0.2.0-beta.2` собран из `e8424e548e89b8406444d3122157542597c3c33f`. Финальный [CI 41651021287](https://github.com/Jetvac/ssh-explorer/actions/runs/41651021287) прошёл; опубликованные ZIP повторно скачаны, проверены все **816 файлов portable и 252 git blobs исходников**. [Публичный отчёт проверки](https://github.com/Jetvac/ssh-explorer/releases/download/v0.2.0-beta.2/SSHExplorer-0.2.0-beta.2-verification.json). Формулировки ниже о 785 файлах и будущем скачивании относятся к ранним checkpoint ab01976/4895d45, а не к состоянию завершённой поставки. Исторические результаты и хеши не изменены.

Пользователь разрешил Computer Use примерно на 40 минут с 13:41:53 UTC. Root управлял только испытательными окнами; Windows security, сеть, DPI и системная тема не менялись. Среда: Windows 11 x64 build 26200, Light, снимки главного окна инструмента 1320×850. Физический DPI не измерен, миллисекунды Explorer не измерялись. Изображения сохранены без ретуши; синяя подсветка указателя принадлежит инструменту.

Использованы скачанные чистые CI portable-комплекты `ab019761db7cb2d0ae401dcebb1c3ca2ec9cb34d` и затем `4895d45687e662fa4586f5eb7642127b299ac199`. EXE/DLL, commit и все 785 файлов проверены SHA-манифестом. [Метаданные и хеши снимков](test-results/beta2-live-screenshots.json).

**Эти файловые/терминальные проверки используют явный TestSessionHarness с isolated store, supplied credentials и независимо известным fingerprint локального стенда. Они не являются доказательством обычного парольного входа.** Пользовательское хранилище не использовалось; на Linux ничего не устанавливалось.

| Сценарий | Результат и доказательство |
|---|---|
| Таблица и выбор | PASS: одна общая строка выделения, без синих рамок каждой ячейки; действия выбора активируются. [Без выбора](screenshots/beta2/files-details-empty-selection-ci-ab01976.png), [выбор](screenshots/beta2/files-selected-full-row-ci-ab01976.png) |
| Меню и виды | PASS указанного объёма: View flyout появился у вызвавшей кнопки; README остаётся выбран при Details→Huge→List→Content. [Меню](screenshots/beta2/view-menu-anchored-ci-ab01976.png), [Huge](screenshots/beta2/files-huge-ci-ab01976.png), [List](screenshots/beta2/files-list-ci-ab01976.png), [Content](screenshots/beta2/files-content-ci-ab01976.png). Все восемь layouts отдельно прошли CI components |
| Пустая папка и история | PASS: double-click empty-folder, Back и Alt+Right меняют путь/содержимое; current marker дерева соответствует папке. [Снимок](screenshots/beta2/empty-folder-tree-current-ci-ab01976.png) |
| Ошибка первой папки после SSH login | PASS: явное сообщение и недоступные файловые команды; Home восстанавливает реальный каталог и очищает ошибку. [До](screenshots/beta2/initial-folder-error-ci-ab01976.png), [после](screenshots/beta2/initial-folder-recovered-ci-ab01976.png) |
| Найденный дефект keyboard focus | FAIL в ab01976: Back становится disabled, затем Alt+Right рисует рамку всего address host. Исправлен в 4895d45: декоративные hosts не получают Focus/Tab. Тот же живой путь повторён — PASS. [До](screenshots/beta2/nav-focus-defect-before-ci-ab01976.png), [после](screenshots/beta2/nav-focus-fixed-ci-4895d45.png), [точные binaries/результат](test-results/beta2-live-focus-4895d45.json) |
| Реальный OpenSSH PTY / WebView2 | PASS: настоящий вывод, UTF-8/emoji, ANSI red, комплектный engine 156.0.4314.58. Команда printf отправлена известной acceptance-пробой, не ручным вводом в терминал. [Снимок](screenshots/beta2/real-linux-terminal-utf8-ansi-ci-ab01976.png), [JSON](test-results/beta2-live-terminal-ab01976.json) |
| Linux monitor | PASS указанного объёма: реальные CPU/память/процессы; недоступное I/O не подменено нулями. Выбор PID 1 сохранялся между несколькими замерами; никаких сигналов процессам не отправлялось. [Данные](screenshots/beta2/linux-monitor-live-ci-ab01976.png), [сохранённый выбор](screenshots/beta2/linux-monitor-selection-stable-ci-ab01976.png) |

## Что не закрыто

Обычный путь опубликованной beta.1 был начат в пустом отдельном store: показана настоящая форма, затем отменена без создания профиля. Инструмент терял ввод при активации родителя owned modal: поле оставалось пустым, а другие индексы возвращали `element ... is not available in cached app state`. Попытка через второй доступный интерфейс управления дала тот же результат. Полный живой парольный ввод, Trust и перезапуск опубликованного EXE поэтому **BLOCKED инструментом**, а не принят как PASS. В CI обычные команды/контролы с реальным SSH/SFTP прошли отдельно: [CI_BETA2](CI_BETA2.md).

Не воспроизведён исходный пользовательский crash; **P0 открыт**. Отсутствие crash в явно внедрённом fixture не устанавливает его причину. Не выполнены motion video, полный Explorer drag/drop, все восемь видов живой клавиатурой, 100/125/150/200% DPI, HC, переключение системной анимации, multi-monitor и чистая Windows. Снимки не подтверждают длительность/качество движения или полное визуальное совпадение с Explorer.

Финальный опубликованный комплект должен пройти собственный clean CI и скачивание/проверку; точные release commit и hashes указываются в GitHub Release. Показанные здесь ранние CI-комплекты не переименовываются в финальную поставку.
