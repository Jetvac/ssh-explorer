# Проверки beta.4

Текущая итерация: baseline `v0.2.0-beta.3` / `e8ef6815dd3417dcbda632077ed8408c77576d04`. Исторический [проверенный выпуск beta.3](https://github.com/Jetvac/ssh-explorer/releases/tag/v0.2.0-beta.3) и его [отчёт](https://github.com/Jetvac/ssh-explorer/releases/download/v0.2.0-beta.3/SSHExplorer-0.2.0-beta.3-verification.json) сохраняются.

## Уровни доказательств

- Headless: core/auth/state/resource/clock tests. Непоказанные WPF controls не являются физическим вводом. Host HWND/Computer Use запрещены и не применяются.
- Реальный изолированный Linux/SFTP: [пять новых файловых проверок PASS](test-results/beta4-files-sftp.log) — occupied-name sentinel, два независимых клиента для exclusive CREATE и MKDIR, конфликт/исправление Unicode имени/Undo, отмена до создания. Только GUID-каталог этого теста, удалён после прогона. Нет установки серверных компонентов, сигналов или изменений SSH-сервиса. [49 core PASS](test-results/beta4-core.log), [происхождение dirty development snapshot](test-results/beta4-local-checks.json); это не проверка точной финальной DLL релиза.
- Windows Actions: настоящие WPF HWND/layout/render, программные key/control events и controlled list/create/rename executor. Проверяются конкретные границы pending/error/cancel/identity/virtualization, а не полные физические пользовательские маршруты.
- Полный CI: прежние auth network/ordinary WPF routes, fresh child, native ABI, self-contained portable и отдельный packaged EXE startup/normal close.
- Отдельно NOT RUN: физический ввод, настоящий Explorer OLE gesture, DWM/caption/Snap, motion video, настоящая полная OS DPI/text scale/HC матрица и чистая Windows. Приёмка за пользователем.

## Галерея и provenance

`Test-UiComposition.ps1` разрешён только в GitHub Actions. Before — immutable опубликованный ZIP beta.3 с pinned SHA256, а не пересобранный старый код. Он выполняется отдельным baseline harness. After — текущая App DLL. Обе стороны используют общую обезличенную fixture, production parser/formatters, одинаковые фактические client DIP/DPI/font/theme/culture. Native caption/DWM исключены из RenderTargetBitmap; чёрная/прозрачная зона сверху не диагностируется как дефект окна.

Metadata записывает commit/DLL hash, клиентский размер, DPI, шрифт, culture, active state, focus, данные и sort. Программное задание состояния не считается физическим получением focus/hover. Восьмибитная буква fixture не подменяет production перевод состояний: R/S/D проходят штатный parser. ru-RU/en-US проверяются отдельно; десятичные знаки не меняются вручную.

Новые сцены: inline имя Details/tiles, сохранённая ошибка имени, подтверждённая новая папка, ошибочный адрес, lasso, узкие Files/Home/overflow, выбранная группа/context menu, заполненная форма/ComboBox popup, длинные вкладки, мониторинг/сортировка/preferences. Прежние34 beta.3 assertions сохраняются как component/layout assertions.

## Итоговый выпуск

Итоговые commit/CI/tag/portable/source/evidence/SHA256 должны относиться к одному результату. До успешного полного прогона и read-back GitHub assets выпуск не считается проверенным. Конкретные ссылки, хеши и счётчики будут добавлены после финального CI.
