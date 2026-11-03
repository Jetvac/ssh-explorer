# Motion contract — 0.2.0-beta.2

Дата: 03.11.2026. Это контракт WPF-кода и проверки его механизма, **не измерение анимаций Explorer**. После первоначального ограничения пользователь разрешил короткий [живой прогон](LIVE_BETA2.md); его снимки не доказывают движение. Motion video, DPI/HC и переключение системной анимации на host не выполнялись.

Microsoft задаёт стандартные ориентиры 83/167/250 мс; Windows motion должен быстро отвечать на действие и учитывать контекст. Для этого приложения выбраны значения ниже, а не заявлено совпадение с измеренной длительностью Проводника. Источники: [Motion in Windows](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/motion), [Timing and easing](https://learn.microsoft.com/en-us/windows/apps/design/motion/timing-and-easing).

## Общие правила реализации

- `UiMotion` меняет только presentation-свойства. Запуск команды, смена активной сессии, данные каталога и освобождение ресурсов не ожидают завершения clock.
- Общие TimeSpan-ресурсы App.xaml: `MotionFast=83ms`, `MotionSurface=167ms`, `MotionSlow=250ms`. Последний зарезервирован и сейчас не назначен эффекту: нет декоративного замедления ради использования всех токенов.
- Перед установкой clock базовое значение свойства уже является конечным. Старый clock снимается через SnapshotAndReplace/Stop; версия состояния не позволяет позднему Completed вернуть прежний результат.
- Состояние хранится на конкретном небольшом слое/host; реестр целей использует слабые ссылки. Виртуализированные строки не получают по таймеру/Storyboard на каждый сетевой refresh. Unloaded завершает и очищает оставшиеся clocks.
- `Enabled` учитывает `SystemParameters.ClientAreaAnimation && !HighContrast`. Runtime-изменение параметров и смена темы завершают текущие украшения сразу. Отсутствие анимации не меняет hit-test flags, доступность команды или конечное состояние. [API системного параметра](https://learn.microsoft.com/en-us/dotnet/api/system.windows.systemparameters.clientareaanimation?view=windowsdesktop-10.0).
- High Contrast использует системные цвета, отдельные normal/selected/primary focus strokes и мгновенные selection/focus/drop. Читаемость не зависит только от opacity.
- Motion не анимирует высоты таблиц, сетку тысяч файлов, ширину колонок или сетевые операции. Искусственной паузы перед командой нет.

## Контракт по событиям

| Область / событие | Начало → конец и длительность | Прерывание, фокус и выключение | Проверка / оставшаяся граница |
|---|---|---|---|
| Hover обычной кнопки, невыбранной вкладки и ListBoxItem | Opacity отдельной фоновой HoverLayer 0→1/1→0, linear 83мс; текст и иконка остаются непрозрачными. Подложки имеют постоянный radius и область клика | Opposite hover берёт текущее opacity и заменяет один clock. Disabled/selected/drop переставляют правильное состояние; off/HC сразу ставит конечное значение | Настоящий clock: промежуточное значение и opposite retarget **PASS headless**. Live hover/video **NOT RUN** |
| Pressed, keyboard focus, active/inactive selection, cut, drop | Мгновенный цвет/рамка. Cut использует явную opacity выбранного объекта; keyboard focus — одна рамка строки/элемента | Геометрия не меняется. Focus overlay отделён от cell content; HC получает контрастный stroke. Primary-кнопка использует контрастный focus stroke | Ресурсы/шаблоны **PASS**. Настоящее получение keyboard focus, inactive окно и HC **NOT RUN на host** |
| Открытие меню/flyout | После штатного открытия/placement opacity FlyoutSurface .96→1, linear 83мс. Перевода/масштаба popup нет | Доступность и захват ввода выполняются WPF сразу. Команда/Esc/outside dismiss не ждут fade-out. Closed/Unloaded очищает clock; native submenu Esc обрабатывается отдельно. Возврат фокуса не перехватывает новый диалог/поле | Запуск и cleanup реального clock без открытия Popup **PASS headless**. Нативный захват, work-area, keyboard и outside-click **NOT RUN** |
| Выбор/закрытие серверной вкладки | Сессия и содержимое меняются сразу. Допускается только 83мс hover-фон заголовка; active surface мгновенная | Закрытие и отмена lifetime не ждут эффекта. Native caption hit testing и Snap не переделываются motion-кодом | Код; live быстрые смены/закрытия и Snap новой сборки **NOT RUN** |
| Файлы ↔ графический монитор | Dedicated WPF screen host opacity .96→1 и TranslateY 4→0, 167 мс CubicEase EaseOut. LayoutTransform и размеры не меняются | Повторный переход заменяет максимум два clock. Выбор раздела, сессия и команда уже актуальны. Off/HC и disposal: opacity1/Y0, clocks0 | Реальные промежуточные values,64 быстрых повторения, disable, Unloaded **PASS headless**. Видимый путь и реальная позиция hit testing **NOT RUN** |
| Терминал | Мгновенный переход; WebView2/HWND не анимируется | Stop(screen), opacity1, Transform.Identity до запуска terminal. PTY/ввод не ожидают motion | Код call-site; запуск/живой terminal новой сборки **NOT RUN этим отчётом** |
| Каталог, refresh, смена восьми режимов | Мгновенное применение актуального результата без искусственной паузы/смещения строк | Путь/история меняются только после CommitNavigation актуального поколения. Опоздавшие ответы не заменяют новый каталог. Режимы наследуют общие item states | Stale/path/selection **PASS MVVM**. Реальное воспроизведение всех 8 layouts и сохранение scroll — отдельный CI/UI набор |
| Дерево | Раскрытие штатное, chevron 0↔90° мгновенно. Нет массовой анимации высот | Текущая папка отделена от keyboard selection; полный фон строки не меняет ширину при вложенности. Нативная клавиатура не ждёт перехода | Code/resource; full-width/layout/keyboard **NOT RUN на host** |
| Loading, ошибка, empty и preview | Операционный текст в фиксированной status-строке 20 DIP, empty collapsed; нет верхней полосы, сдвигающей таблицу. Preview применяет только актуальное изображение | Delay 180 мс не вводился. Быстрый запрос может кратко обновить текст статуса, но не запускает fade/blank всей таблицы. Cancellation/generation не позволяет чужому старому кадру появиться у нового объекта | Empty visibility/stale model **PASS**; реальное preview и восприятие busy/flicker **NOT RUN** |
| Drag/drop | Cursor Copy/Move/None и фон/рамка цели мгновенны. Hover вкладки/папки — отдельное семантическое ожидание, не задержка передачи | Leave/Esc/drop/закрытие вызывают cleanup; source identity не определяется текущей визуальной вкладкой. Нет fade, создающего ложную готовность цели | Код UI cleanup; native-протокол сохранён. Живые Explorer жесты новой сборки **NOT RUN** |
| Монитор и очередь | Значения/прогресс обновляются сразу. Отдельные строки сохраняются по identity, график отображает реальные замеры | Не анимируются refresh всех строк/завершение операций; старый selected PID не наследуется повторно использованным PID | Stable/changed process identity без Reset **PASS MVVM**; scroll и отсутствие видимого мерцания **NOT RUN** |

## Воспроизводимые проверки без окон

```powershell
SSHExplorer.UiTests.exe --resources-only
SSHExplorer.UiTests.exe --motion-only
```

03.11.2026, Windows 11 x64 build 26200, SDK 10.0.403, рабочее дерево поверх beta.1 `eda137a734e8fc259eb97fca300b2072d11232a8`, Release 0 warnings/errors:

- **PASS:** 8 resource/contract и 18 MVVM assertions — [isolated repeat](test-results/beta2-resources-isolated.log).
- **PASS:** 17 clock/contract assertions — [isolated repeat](test-results/beta2-motion-isolated.log).
- SHA-256 новой промежуточной test/app DLL — [build metadata](test-results/beta2-isolated-test-build.json). В повторе deliberately consumed WPF startup callback не запускает desktop, не открывает обычный Store и не создаёт Window. Исправление общей ошибки изоляции профильного runner — [PROFILE_FLOW_BETA2](PROFILE_FLOW_BETA2.md). Clean release идентифицируется отдельно при упаковке.

`MotionTests` использует настоящие DoubleAnimation/AnimationClock и внутренний test-policy hook. SeekAlignedToLastTick переводит реальные clocks в середину диапазона, не требует tick/render Window и не имитирует движение скриншотом. Проверены replacement, обратное направление, конечные base values, отключение во время эффекта, Unloaded, смена темы и callback Closed. Проверка объекта/Focusable/IsHitTestVisible/LayoutTransform не выдаётся за реальный ввод клавиатурой. Ни Window, ни Popup не открывался; системные настройки анимации не менялись.

Два уровня разделены: resource test доказывает наличие/валидность шаблона; clock test — поведение механизма. Они не доказывают визуальное сходство, фактические миллисекунды отображения кадра, OS focus/capture или пользовательскую оценку.

## Отложенная живая матрица

Частичный позднее разрешённый прогон строки/меню/навигации — [LIVE_BETA2](LIVE_BETA2.md). **NOT RUN:** видео hover/pressed/flyout и быстрых переходов/Esc, все 100/125/150/200% DPI, Dark/High Contrast, системное отключение анимаций во время эффекта, перенос между мониторами и чистая Windows. Скриншоты beta.1 и beta.2 не доказывают движение.
