# Mirage для macOS

Лёгкий клиент обхода DPI без VPN и без отключения SIP, собранный в виде подписанного (ad-hoc) универсального пакета `Mirage.app` (Apple Silicon `arm64` + Intel `x86_64`) внутри образа `Mirage-macOS-v1.0.0.dmg`.

## Возможности
- **Zapret `tpws` v72.13 (Universal `arm64`/`x86_64`)** с автоподбором рабочей стратегии (`tpws-disorder-sni`, `tpws-tlsrec-sni`, `tpws-oob-sni` и др.).
- **Умный PAC-прокси (`127.0.0.1:1085`) + чистый Яндекс DNS (`77.88.8.8:1253`)**: автоматически настраивает PAC в macOS (`networksetup -setautoproxyurl`) только для доменов из `sites.txt` (исключая корневые `googleapis.com` и `github.com`), и аккуратно отключает при остановке или выходе.
- **Индикаторы доступности 8 ресурсов**: YouTube, Instagram, Discord, Telegram (MTProxy), RuTracker, Rutor.info, X (Twitter), Medium.
- **Telegram в 1 клик**: FakeTLS MTProxy (`tg://proxy?...`) и локальный SOCKS5 (`:1080`).

## Установка на Mac
1. Откройте `Mirage-macOS-v1.0.0.dmg` и перетащите **Mirage.app** в **Applications**.
2. При первом запуске стороннего (без Apple Developer ID) приложения нажмите по **Mirage.app** правой кнопкой -> **Открыть** (или «Системные настройки → Конфиденциальность и безопасность → Всё равно открыть»).

## Сборка из Windows
```cmd
cd C:\Users\d4200-01-156\Desktop\DPI\macos
build_dmg.bat
```
Готовый образ сохраняется в `..\release\macos\Mirage-macOS-v1.0.0.dmg`.

