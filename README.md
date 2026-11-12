# Mirage

Минималистичный кроссплатформенный клиент для обхода DPI-блокировок и ускорения веб-ресурсов без классического VPN и замедления общего трафика.

Поддерживаемые платформы: **Windows**, **Android**, **macOS (Apple Silicon & Intel)**.

## Возможности
- **Избирательный обход по списку доменов**: работает через умный локальный PAC-прокси (`127.0.0.1:1085` на ПК/Mac) или локальный `VpnService` (на Android без root) — системный и рабочий трафик идёт напрямую без лишних задержек.
- **Чистый DNS (`77.88.8.8:1253`)**: обход отравления и замедления стандартного DNS-порта `UDP:53`.
- **Подавление QUIC (`UDP:443`)**: перевод соединений браузера и мобильных приложений на TCP TLS, где работает фрагментация `ClientHello`.
- **Индикаторы доступности ресурсов**: параллельная проверка `YouTube`, `Instagram`, `Discord`, `Telegram (MTProxy)`, `RuTracker`, `Rutor.info`, `X (Twitter)`, `Medium` с отображением задержки.
- **Telegram в 1 клик**: подключение встроенного FakeTLS MTProxy (`tg://proxy?...`).

## Структура проекта
- [`windows/`](windows) — клиент для Windows (WPF / C#, Zapret `winws.exe`, GoodbyeDPI, ByeDPI `ciadpi.exe`, умный PAC-прокси).
- [`android/`](android) — клиент для Android (Kotlin, `VpnService` + `HevSocks5Tunnel` + `libtpws.so` без root, либо `libnfqws.so` + `iptables` с root).
- [`macos/`](macos) — клиент для macOS (Go Universal `arm64`/`x86_64` + Zapret `tpws` + системный PAC-прокси, упаковка `.app` и `.dmg`).

## Скачать готовые сборки
Готовые бинарные файлы (`Mirage-Windows-v1.0.0.zip`, `Mirage-Android-v1.0.0.apk`, `Mirage-macOS-v1.0.0.dmg`) публикуются на странице **[Releases](../../releases)**.

### Первый запуск
- **Windows**: распакуйте архив и запустите `Mirage.exe` (требуются права администратора для драйвера WinDivert).
- **Android**: установите `Mirage-Android-v1.0.0.apk` и нажмите «Включить обход».
- **macOS**: откройте `Mirage-macOS-v1.0.0.dmg`, перенесите `Mirage.app` в `Applications`. При первом запуске (ad-hoc подпись) откройте через правый клик → **Открыть**.

