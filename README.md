<h1 align="center">✦ WgSwitch</h1>

<p align="center">
  Лёгкий пульт для личного WireGuard-туннеля под Windows.<br/>
  Одна кнопка, честный индикатор «сервер отвечает» и иконка в трее — без лишнего.
</p>

<p align="center">
  <a href="https://github.com/lolkekxa/vpn-client/releases/latest"><b>Скачать</b></a> ·
  <a href="#-что-умеет">Что умеет</a> ·
  <a href="#-быстрый-старт">Быстрый старт</a> ·
  <a href="#-несколько-серверов">Серверы</a> ·
  <a href="#-резервная-копия">Резервная копия</a> ·
  <a href="SERVER.md">Настройка сервера</a> ·
  <a href="TROUBLESHOOTING.md">Ошибки</a> ·
  <a href="#-сборка">Сборка</a>
</p>

<p align="center">
  <a href="https://github.com/lolkekxa/vpn-client/actions/workflows/build.yml"><img alt="build" src="https://img.shields.io/github/actions/workflow/status/lolkekxa/vpn-client/build.yml?branch=main&style=flat-square&labelColor=0a0a0a&label=build" /></a>
  <img alt="Windows 10/11" src="https://img.shields.io/badge/Windows-10_%2F_11-a5e8f5?style=flat-square&labelColor=0a0a0a" />
  <img alt=".NET 8" src="https://img.shields.io/badge/.NET-8_·_WPF-c4b5fd?style=flat-square&labelColor=0a0a0a" />
  <img alt="WireGuard" src="https://img.shields.io/badge/WireGuard-official_client-b8ecc8?style=flat-square&labelColor=0a0a0a" />
  <img alt="0 NuGet" src="https://img.shields.io/badge/NuGet-0_пакетов-fbe3a0?style=flat-square&labelColor=0a0a0a" />
  <a href="LICENSE"><img alt="MIT" src="https://img.shields.io/badge/license-MIT-f9c6dd?style=flat-square&labelColor=0a0a0a" /></a>
</p>

<br/>

<img src="docs/readme/hero.png" alt="WgSwitch: подключено, сервер не отвечает, настройки" width="100%" />

<br/>

WgSwitch не реализует VPN сам: туннелем занимается официальный клиент WireGuard, а WgSwitch
включает и выключает его службу одной кнопкой и показывает, что на самом деле происходит.
Exe весит около **0.6 МБ**, сторонних библиотек нет, а в трее программа почти ничего не делает:
когда окно закрыто, память отдаётся системе, а опрос становится редким.

## ✦ Что умеет

<table>
  <tr>
    <td width="33%"><img src="docs/readme/connected.png" alt="Подключено" /></td>
    <td width="33%"><img src="docs/readme/no-response.png" alt="Нет ответа от сервера" /></td>
    <td width="33%"><img src="docs/readme/settings.png" alt="Настройки" /></td>
  </tr>
  <tr>
    <td valign="top"><b>Одна кнопка.</b> Цвет состояния — только в тонком кольце и иконке. Ниже пинг, время в сети, последний handshake и трафик; любое значение копируется кликом.</td>
    <td valign="top"><b>Честный статус.</b> WireGuard показывает «Active», даже когда сервер лёг. WgSwitch пингует сервер внутри туннеля и краснеет, если тот молчит.</td>
    <td valign="top"><b>Настройки.</b> Имя, IP для проверки, путь к конфигу, режим «весь интернет через VPN», автозапуск, запуск свёрнутым в трей, автоподключение и резервная копия.</td>
  </tr>
  <tr>
    <td><img src="docs/readme/setup-2.png" alt="Мастер: данные сервера" /></td>
    <td><img src="docs/readme/setup-3.png" alt="Мастер: ключ на сервер" /></td>
    <td><img src="docs/readme/backup.png" alt="Резервная копия" /></td>
  </tr>
  <tr>
    <td valign="top"><b>Мастер первого запуска.</b> Адрес и ключ сервера. Пару ключей WgSwitch генерирует сам через <code>wg.exe</code>, а свободную подсеть подбирает под каждый новый сервер.</td>
    <td valign="top"><b>Ключ на сервер.</b> Готовый блок <code>[Peer]</code> для серверного конфига в одно копирование и команда, которая применит его без разрыва соединений.</td>
    <td valign="top"><b>Резервная копия.</b> Все серверы и конфиги в один файл, зашифрованный паролем. Новый компьютер — импорт, и всё снова работает.</td>
  </tr>
</table>

И ещё:

- **Трей.** Серая иконка — выключено, зелёная — подключено, красная — туннель поднят, но сервер не отвечает. Правый клик — включить/выключить каждый сервер, двойной клик — открыть окно.
- **Окно не мешает.** Открывается в правом нижнем углу, как всплывающие окна трея. Кнопка «—» убирает его в трей: программа продолжает работать, а выйти можно из меню иконки.
- **Уведомления.** Если включённый сервер перестал отвечать, приходит уведомление Windows. Когда связь вернётся — ещё одно.
- **Автопереподключение.** Сервер молчит дольше 2 минут (сон, смена Wi-Fi) — туннель перезапускается сам, не чаще раза в 5 минут.
- **Автоподключение по серверу.** У каждого сервера свой тумблер «Подключать при запуске».
- **Выход.** По желанию «Выход» из трея выключает все туннели; по умолчанию они остаются работать.
- **Экономно.** Опрос раз в 2–3 секунды, только пока окно видно или туннель включён. В простое — раз в 15 секунд и без запуска процессов.
- **Одна копия.** Повторный запуск exe просто показывает уже открытое окно.

## ✦ Быстрый старт

1. Поставьте **[WireGuard для Windows](https://www.wireguard.com/install/)** и **[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)** (x64).
2. Скачайте `WgSwitch.exe` из **[релизов](https://github.com/lolkekxa/vpn-client/releases/latest)** и запустите. Программа попросит права администратора: без них Windows не даёт ставить и снимать службу туннеля.
3. Пройдите мастер: адрес сервера, его публичный ключ, ваш IP в VPN.
4. Скопируйте блок `[Peer]` с шага 3 в конфиг сервера. Что сделать на сервере целиком, расписано в **[SERVER.md](SERVER.md)**: WireGuard, файрвол и доступ к SSH и панелям только через VPN.
5. Нажмите кнопку. Зелёный — готово.

Что-то не работает — разбор ошибок в **[TROUBLESHOOTING.md](TROUBLESHOOTING.md)**.

Уже есть свой `.conf`? На первом шаге мастера нажмите «У меня есть .conf». Есть резервная копия — «Восстановить из копии».

## ✦ Несколько серверов

Настройки → «+ Добавить» открывает тот же мастер. Над кнопкой появляются капсулы серверов с точкой
состояния, а кнопка управляет выбранным.

- **Одновременно.** Держать включёнными можно сразу несколько туннелей, если у серверов разные
  подсети. Мастер сам предлагает свободную: `10.8.0.x`, затем `10.9.0.x` и так далее.
- **Переименовать.** Поле «Имя» в настройках. WgSwitch сам переименует `.conf` и поправит путь (в Windows
  туннель называется по имени файла), так что трогать файл руками не нужно.
- **Весь интернет через VPN.** Галочка в настройках сервера переключает `AllowedIPs` в его `.conf`:
  `0.0.0.0/0, ::/0` или только подсеть сервера. Применяется по «Сохранить», включённый туннель
  сам переподключится. Для полного режима на сервере нужен NAT ([SERVER.md](SERVER.md#-весь-трафик-через-vpn)).
- **Удалить.** Убирает сервер из списка, но не удаляет его `.conf`.

Переименовать и удалить можно только выключенный туннель.

## ✦ Резервная копия

Настройки → «Экспорт» сохраняет в один файл `.wgsb` все серверы, их IP и сами конфиги **вместе
с приватными ключами**. Без ключей после потери компьютера в туннель не войти, поэтому файл
зашифрован паролем, и его можно спокойно хранить в облаке:

| | |
|---|---|
| Шифр | AES-256-GCM: неверный пароль и подмену файла видно сразу |
| Ключ из пароля | PBKDF2-SHA256, 600 000 итераций, случайная соль |
| Пароль | не короче 8 символов; восстановить его нельзя |

Восстановить: «Импорт» в настройках или «Восстановить» на первом шаге мастера. Конфиги ложатся
в `C:\ProgramData\WgSwitch`, серверы с теми же именами заменяются, остальные остаются. Отличающийся
конфиг на диске не затирается, а переименовывается в `.bak`.

## ✦ Безопасность

- **Конфиги.** Мастер кладёт их в `C:\ProgramData\WgSwitch`, а доступ к папке есть только у Администраторов и SYSTEM.
- **Приватный ключ.** Существует только в `.conf`. Приложение видит его, когда создаёт ключ (`wg genkey`), шифрует резервную копию и переписывает `AllowedIPs` при смене режима. Ключ держится только в памяти, в логи и настройки не попадает, а при смене режима остаётся в том же файле.
- **Настройки.** В `%APPDATA%\WgSwitch\settings.json` лежат только пути, IP и флаги.
- **Handshake и трафик.** Читаются через `wg show <имя> latest-handshakes` и `transfer`, а не через `dump`: `dump` выводит приватный ключ.
- **Автозапуск.** Это задача планировщика «при входе» с наивысшими правами. Ключ реестра `Run` не подходит: программы с `requireAdministrator` Windows через него молча не запускает.

<details>
<summary><b>Свой <code>.conf</code> вручную</b></summary>

<br/>

Укажите путь в настройках. В конфиге лежит приватный ключ, поэтому закройте папку от обычных
пользователей (PowerShell от администратора):

```powershell
New-Item -ItemType Directory -Force C:\ProgramData\WgSwitch | Out-Null
Copy-Item .\wg0.conf C:\ProgramData\WgSwitch\
icacls C:\ProgramData\WgSwitch /inheritance:r /grant:r "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-18:(OI)(CI)F"
```

Имя файла становится именем туннеля: `wg0.conf` → `wg0` → служба `WireGuardTunnel$wg0`.
Допустимы латиница, цифры и `_=+.-`, до 32 символов.

</details>

## ✦ Сборка

Каждый пуш в `main` собирается на GitHub Actions и обновляет релиз
[`latest`](https://github.com/lolkekxa/vpn-client/releases/tag/latest). Тег вида `v1.2.0` создаёт отдельный
постоянный релиз.

Локально нужен .NET SDK 8 или новее:

```powershell
# один файл (~0.6 МБ), нужен .NET 8 Desktop Runtime
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish

# один файл без зависимостей (~65 МБ), runtime внутри
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish-sc
```

Урезать self-contained сборку через `PublishTrimmed` нельзя: .NET запрещает trimming для WPF и WinForms
(ошибки NETSDK1168 и NETSDK1175).

<details>
<summary><b>Как это устроено</b></summary>

<br/>

| Действие | Как |
|---|---|
| Включить | `wireguard.exe /installtunnelservice "<путь к .conf>"` |
| Выключить | `wireguard.exe /uninstalltunnelservice <имя>` |
| Состояние | служба `WireGuardTunnel$<имя>` через SCM, затем пинг до IP сервера внутри туннеля |
| Handshake и трафик | `wg.exe show <имя> latest-handshakes` / `transfer` |
| Новые ключи | `wg.exe genkey` / `pubkey` |

```
App.xaml(.cs)               запуск, одна копия (Mutex), трей, показ и уничтожение окна
MainWindow.xaml(.cs)        окно: кнопка, статус, серверы, настройки, мастер, резервная копия
Models/TunnelStatus.cs      состояние туннеля
Services/WireGuardService   install/uninstall, опрос служб, пинг, wg show — для всех серверов
Services/TrayService        NotifyIcon: иконки рисуются в рантайме, тёмное меню
Services/SettingsService    settings.json, список серверов, автозапуск
Services/ConfigFactory      мастер: проверка ввода, ключи, конфиг в закрытой папке
Services/BackupService      зашифрованная резервная копия (.wgsb)
Services/Native             DWM, SCM, сброс рабочего набора
Services/Proc               запуск внешних утилит без окна консоли
Assets/                     шрифты Geist (OFL), иконка приложения
```

Сторонних NuGet-пакетов нет: WPF для окна, `System.Windows.Forms.NotifyIcon` для трея, остальное —
стандартная библиотека и несколько вызовов Win32.

</details>

## ✦ Автор

Сделано в **[LKXA](https://lkxa.ru)**: Telegram-боты, Mini Apps и веб-сервисы под ключ.
Дизайн повторяет сайт студии: тёмная тема, шрифт Geist, капсулы и мягкие анимации.

- Портфолио — **[lkxa.ru](https://lkxa.ru)**
- Telegram — **[@lkxa_dev](https://t.me/lkxa_dev)**
- Канал — **[@lkxa_ru](https://t.me/lkxa_ru)**
- Почта — **[lkxa.dev@gmail.com](mailto:lkxa.dev@gmail.com)**

## ✦ Лицензия

[MIT](LICENSE). Шрифты Geist и Geist Mono — [SIL Open Font License 1.1](Assets/Fonts/OFL.txt).

<br/>

<p align="center">
  <a href="https://lkxa.ru/?ref=badge"><img src="https://lkxa.ru/identity/badge/ru-dark.svg" alt="Сделано в LKXA" height="32" /></a>
</p>
