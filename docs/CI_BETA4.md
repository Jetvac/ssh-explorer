# Проверки beta.4

Текущая итерация: baseline `v0.2.0-beta.3` / `e8ef6815dd3417dcbda632077ed8408c77576d04`. Исторический [проверенный выпуск beta.3](https://github.com/Jetvac/ssh-explorer/releases/tag/v0.2.0-beta.3) и его [отчёт](https://github.com/Jetvac/ssh-explorer/releases/download/v0.2.0-beta.3/SSHExplorer-0.2.0-beta.3-verification.json) сохраняются.

## Уровни доказательств

- Headless: core/auth/state/resource/clock tests. Непоказанные WPF controls не являются физическим вводом. Host HWND/Computer Use запрещены и не применяются.
- Реальный изолированный Linux/SFTP: [пять новых файловых проверок PASS](test-results/beta4-files-sftp.log) — occupied-name sentinel, два независимых клиента для exclusive CREATE и MKDIR, конфликт/исправление Unicode имени/Undo, отмена до создания. Только GUID-каталог этого теста, удалён после прогона. Нет установки серверных компонентов, сигналов или изменений SSH-сервиса. [49 core PASS](test-results/beta4-core.log), [происхождение dirty development snapshot](test-results/beta4-local-checks.json); это не проверка точной финальной DLL релиза.
- Windows Actions: настоящие WPF HWND/layout/render, программные key/control events и controlled list/create/rename executor. Для удерживаемой мыши выделен guarded SendInput helper: только GHA, только показанный активный собственный HWND, проверяются Win32/WPF button state и обязательный release. Это синтетический OS-ввод, физическая мышь не проверяется. Проверяются конкретные границы pending/error/cancel/identity/virtualization, а не полные физические пользовательские маршруты.
- Полный CI: прежние auth network/ordinary WPF routes, fresh child, native ABI, self-contained portable и отдельный packaged EXE startup/normal close.
- Отдельно NOT RUN: физический ввод, настоящий Explorer OLE gesture, DWM/caption/Snap, motion video, настоящая полная OS DPI/text scale/HC матрица и чистая Windows. Приёмка за пользователем.

Три оконных набора запускаются отдельными процессами и дают независимые журналы: monitor/shell, Files, Forms/Tabs. Сбой одного не скрывает результаты остальных; comparison/evidence ZIP выпускается только после успеха всех трёх. Forms/Tabs используют отдельный fresh fixture, никогда не выдают начальный `SessionState.Closed` неподключённых объектов за их уничтожение: проверяется отмена lifetime только закрываемой сессии.

Отдельная auth-граница: [детерминированный development probe](test-results/beta4-auth-timeout-probe.json) с test-only vendor budget250ms раньше обычного Core4s показал active pending callbacks1→0. В production budget не менялся, retry не добавлялся, исходный SshOperationTimeoutException сохраняется. [13 component](test-results/beta4-auth-components.log) + [26 network](test-results/beta4-auth-network.log) PASS. Это не диагноз неизвестного прежнего user crash.

## Галерея и provenance

`Test-UiComposition.ps1` разрешён только в GitHub Actions. Before — immutable опубликованный ZIP beta.3 с pinned SHA256, а не пересобранный старый код. Он выполняется отдельным baseline harness. After — текущая App DLL. Обе стороны используют общую обезличенную fixture, production parser/formatters, одинаковые фактические client DIP/DPI/font/theme/culture. Native caption/DWM исключены из RenderTargetBitmap; чёрная/прозрачная зона сверху не диагностируется как дефект окна.

Metadata записывает commit/DLL hash, клиентский размер, DPI, шрифт, culture, active state, focus, данные и sort. Программное задание состояния не считается физическим получением focus/hover. Восьмибитная буква fixture не подменяет production перевод состояний: R/S/D проходят штатный parser. ru-RU/en-US проверяются отдельно; десятичные знаки не меняются вручную.

Новые сцены: inline имя Details/tiles, сохранённая ошибка имени, подтверждённая новая папка, ошибочный адрес, lasso, узкие Files/Home/overflow, выбранная группа/context menu, заполненная форма/ComboBox popup, длинные вкладки, мониторинг/сортировка/preferences. Прежние34 beta.3 assertions сохраняются как component/layout assertions.

## Итоговый выпуск

Проверенный checkpoint `df859649f0b25ad97520795fed4f1c94d22c3c5b`: [полный CI — SUCCESS](https://github.com/Jetvac/ssh-explorer/actions/runs/41688865770), [отдельный UI preflight — SUCCESS](https://github.com/Jetvac/ssh-explorer/actions/runs/41688865620). [Машиночитаемая сводка checkpoint](test-results/beta4-ci-df85964-evidence.json).

| Проверка checkpoint | Результат и граница |
|---|---|
| Monitor/shell actual WPF | 48 PASS, включая сохранённые 34 beta.3; реальные HWND/layout, управляемые данные |
| Files actual WPF | 38 PASS: pending/error/retry/identity, адрес, create, 10 181 модель, bounded containers, контекст/overflow, программная удерживаемая мышь и cleanup |
| Forms/Tabs actual WPF | 24 PASS: применённые templates, одинаковая естественная высота полей, popup, error/disabled/reveal, overflow и независимое закрытие сессий |
| Core/state/resources | core49; resources8 + MVVM18; motion17; connection13; graph6; monitor states16/poll8/refinement23; drag7; file state7 |
| Обычные UI/auth/native регрессии | 6 WPF layout + 18 MVVM + 16 UI component; 113 profile/STA; 13 auth component + 26 loopback network; ordinary Add/Trust/Connect + fresh child; native COM ABI |
| Поставка | Self-contained package hashes, exact version/commit, packaged EXE startup и normal exit0 в CI; физический парольный вход этим не доказан |

У checkpoint проверены SHA256 всех 41 исходных PNG: 9 baseline, 16 current, 12 Files, 4 Forms. Сравнимые пары — пять сцен с одинаковой фактической геометрией, 96 DPI, font14 и ru-RU. У взаимодействий отдельно зафиксирован размер окна, помещённого в рабочую область CI. Проверка изображения обнаружила, что RenderTargetBitmap содержимого не включает соседний AdornerLayer; финальный harness захватывает общий реальный AdornerDecorator для кадров рамки. Это исправление границы доказательства, а не дорисовка PNG или специальное изменение продукта.

Финальный `v0.2.0-beta.4` собирается повторно после последнего изменения harness и документации. [Выпуск](https://github.com/Jetvac/ssh-explorer/releases/tag/v0.2.0-beta.4) содержит portable/source/UI ZIP, SHA256, отдельные неизменённые PNG и [отчёт повторного скачивания](https://github.com/Jetvac/ssh-explorer/releases/download/v0.2.0-beta.4/SSHExplorer-0.2.0-beta.4-verification.json). Последний связывает точный tag commit/CI, Git blobs исходников, DLL всех текущих render sets и portable, хеши опубликованных assets и startup. Исторический checkpoint не подменяет проверку финальной сборки.
