# Распространяемые компоненты

| Компонент | Версия | Лицензия / условия |
|---|---|---|
| .NET / WPF runtime | 10.0.14 x64 (SDK 10.0.403) | MIT и third-party notices из runtime |
| SSH.NET | 2026.0.0 | MIT |
| BouncyCastle.Cryptography | Версия зафиксирована packages.lock.json | MIT |
| System.Security.Cryptography.ProtectedData | 10.0.0 | MIT |
| Microsoft.Extensions.Logging.Abstractions и зависимости | packages.lock.json | MIT |
| Microsoft.Web.WebView2 SDK | 1.0.4314.35 | Microsoft BSD-style license, включена в licenses |
| Microsoft Edge WebView2 Fixed Runtime | 156.0.4314.58 x64 | Microsoft Edge WebView2 Runtime Fixed Version, официальный текст в licenses/WebView2-Fixed-Runtime-LICENSE.html |
| @xterm/xterm | 6.0.0 | MIT |
| @xterm/addon-fit | 0.11.0 | MIT |
| @xterm/addon-search | 0.16.0 | MIT |

Тексты лицензий включены в `licenses`, а также рядом с runtime. WebView2 используется только для локального терминала. Ни один компонент не загружается с CDN при работе приложения.

QEMU, Alpine, Python и SDK являются исключительно инструментами разработки/испытаний в `.tools`; они не входят в portable-поставку и не требуются пользователю или управляемому серверу.

Системные объектные иконки и Segoe Fluent Icons запрашиваются у установленной Windows во время работы; извлечённые DLL, шрифты и набор Windows-иконок не включаются в комплект. Блокнот или выбранный пользователем редактор — отдельное установленное приложение, не компонент поставки SSH Explorer.
