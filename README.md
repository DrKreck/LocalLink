# LocalLink

Графический интерфейс для **zapret2** на Windows. Настройка подключения, диагностика и баг-репорты — в одном окне.

Бесплатно, с открытым исходным кодом. Без регистрации и ключей доступа.

**[Скачать для Windows](https://raw.githubusercontent.com/DrKreck/LocalLink/main/downloads/LocalLink-0.8.1-windows-x64.zip)** · [Исходники ZIP](https://raw.githubusercontent.com/DrKreck/LocalLink/main/downloads/LocalLink-0.8.1-source.zip) · [Telegram](https://t.me/AresC404) · [Поддержать на Boosty](https://boosty.to/aresc404)

![Интерфейс LocalLink](docs/images/interface.png)

## Быстрый старт

Нужны **Windows 10/11 x64** и **.NET Framework 4.8**.

1. Скачайте `LocalLink-0.8.1-windows-x64.zip` по ссылке выше.
2. Распакуйте архив целиком в отдельную папку.
3. Откройте PowerShell в этой папке и выполните:

   ```powershell
   .\tools\Setup-Engine.ps1
   ```

4. Запустите `LocalLink.exe` и нажмите **Включить**. Для фильтра потребуются права администратора.
5. Нажмите **Проверить**. Если метод не подходит — выберите другой или запустите подбор в настройках.

**Движок не входит в Windows-архив:** скрипт получает закреплённые компоненты с GitHub и проверяет их хеши. Без шага 3 фильтр не запустится. Необязательный мост Telegram требует отдельного [runtime](telegram_bridge/README.md).

## Возможности

- Профили сервисов, свои домены и дополнительные протоколы.
- Проверка доступности, подбор метода и отдельная настройка Discord.
- Светлая, тёмная и системная темы, работа в трее и автозапуск.
- ZIP-отчёт с ограниченной историей работы между запусками.

Шестерёнка открывает настройки. **×** скрывает окно в трей; **Выключить** останавливает фильтр; **Выход** в трее завершает приложение.

## Если не работает

Воспроизведите проблему → нажмите **Баг-репорт** → сохраните ZIP → отправьте его приватно в [@AresC404](https://t.me/AresC404). Кнопка контакта находится рядом с отчётом; архив прикрепляется вручную.

Для [Issues](https://github.com/DrKreck/LocalLink/issues) достаточно описания, версии и шагов воспроизведения. Не публикуйте там ZIP, ключи или личные сведения. [Данные и приватность](docs/PRIVACY.md) · [Сообщить об уязвимости](SECURITY.md).

## Ограничения

LocalLink — **не VPN**: внешний IP не меняется, туннель не создаётся. Результат зависит от провайдера и сети; универсального обхода нет. Ответ сайта не подтверждает работу голоса, видео или игрового сервера.

Не запускайте одновременно несколько программ на winws/WinDivert и не отключайте защиту Windows ради запуска.

## Сборка из исходников

Используется штатный компилятор .NET Framework x64; Visual Studio и .NET SDK не нужны.

```powershell
.\tools\Setup-Engine.ps1
.\Build.ps1 -OutputDirectory .\artifacts\build
.\artifacts\build\LocalLink.exe
```

Сборка выполняет офлайн-тесты и не включает фильтр. [Участие в разработке](CONTRIBUTING.md) · [Изменения](CHANGELOG.md) · [Проверка выпуска](docs/RELEASE-0.8.1.md).

## Поддержать проект

[Boosty — aresc404](https://boosty.to/aresc404). Поддержка добровольная: функции программы бесплатны и не зависят от донатов.

## Лицензии и контакты

Код LocalLink — [MIT](LICENSE). В основе — [zapret2](https://github.com/bol-van/zapret2), компоненты Windows — из [zapret-win-bundle](https://github.com/bol-van/zapret-win-bundle). Лицензии зависимостей сохраняются отдельно: [THIRD-PARTY.md](THIRD-PARTY.md).

[GitHub · DrKreck](https://github.com/DrKreck) · [Telegram · @AresC404](https://t.me/AresC404)
