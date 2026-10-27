# Сторонние компоненты

Приложение загружает закреплённые версии компонентов после подтверждения пользователя и проверяет их SHA256. Их лицензии сохраняются вместе с установленными файлами. MIT в `LICENSE` относится к коду LocalLink.

| Компонент | Лицензия и источник |
|---|---|
| zapret2 / winws2 / Lua helpers | MIT, copyright 2016–2026 bol-van; [лицензия](https://github.com/bol-van/zapret2/blob/00f5aaa36de500971faf6db440dd7d8b25672281/docs/LICENSE.txt). |
| WinDivert | LGPLv3 или GPLv2; [лицензия](https://github.com/basil00/WinDivert/blob/1789526ecfb9ff5397c94f9f54c1a3dc2fb60440/LICENSE). |
| Cygwin API library | LGPLv3-or-later с Cygwin Linking Exception; [условия](https://cygwin.com/licensing.html), [исходники](https://github.com/cygwin/cygwin/tree/89a1e4a573d40b233011402f42630b970ff71291). Уведомления newlib сохраняются. |
| Telegram runtime | CPython — PSF; cryptography — Apache-2.0/BSD; cffi — MIT; pycparser — BSD. Тексты LICENSE/NOTICE сохраняются в runtime. |
| Списки доменов | Источники: community.antifilter.download и antifilter.download. Загружаются по действию пользователя. |

`engine.lock.json` фиксирует версии и файлы движка из [zapret-win-bundle](https://github.com/bol-van/zapret-win-bundle/tree/6eb463a6758fb48cd101bc55dfd057e6e9d98af1). Сторонние бинарники не включены в Windows-архив LocalLink.
