# GEMINI.md — Mirage (Кроссплатформенный обход DPI)

Техническая памятка по архитектуре, критическим решениям и сборке проекта **Mirage** для Windows, Android и macOS.

---

## 1. Структура репозитория

```text
DPI/
├── windows/   # WPF + C# (.NET Framework 4.0/4.8 csc) клиент Mirage.exe
├── android/   # Android (Kotlin, VpnService + HevSocks5Tunnel + libtpws/libnfqws)
├── macos/     # macOS (Go Universal arm64/x86_64 + tpws + mkdmg -> .dmg)
├── release/   # Готовые клиентские сборки (не коммитятся в git, публикуются в GitHub Releases)
├── README.md  # Краткое описание проекта для GitHub
└── GEMINI.md  # Этот файл
```

---

## 2. Ключевые инженерные решения (эмпирический опыт обхода блокировок)

### Общие принципы для всех платформ
1. **Чистый DNS поверх нестандартного порта**: провайдеры подменяют/замедляют `UDP:53`. Во всех клиентах DNS-запросы направляются на **Яндекс DNS `77.88.8.8:1253`** (резерв `8.8.8.8:53`).
2. **Избирательный список доменов (`sites.txt`)**:
   - Корневые домены `googleapis.com` и `github.com` **принудительно исключаются** из обхода/PAC во избежание поломки IDE, системных служб и CDN (поддомены вроде `youtubei.googleapis.com` при этом разрешены).
3. **Индикаторы доступности 8 ресурсов** (параллельная проверка в реальном времени):
   `YouTube`, `Instagram`, `Discord`, `Telegram` (реальный TLS-хендшейк MTProxy `156.239.251.46:443` с SNI `www.microsoft.com`), `RuTracker`, `Rutor.info`, `X (Twitter)`, `Medium`.
4. **Telegram без VPN**: встроенный FakeTLS MTProxy (`tg://proxy?server=156.239.251.46&port=443&secret=eeff2a510da3958357083ec111f4de115f7777772e6d6963726f736f66742e636f6d`) и резервные узлы.

### Windows (`windows/`)
- **Сборка**: [`windows/build.bat`](windows/build.bat) (`csc.exe` под `.NET Framework 4.x`, без MSBuild/Visual Studio) → [`windows/Mirage.exe`](windows/Mirage.exe).
- **Интерфейс**: [`windows/src/MainWindow.xaml`](windows/src/MainWindow.xaml) парсится на лету через `XamlReader.Parse`, поэтому рядом с `.exe` обязательно должны лежать `src/MainWindow.xaml`, `config/` и `bin/`.
- **Запуск `winws.exe` (Zapret / Cygwin)**:
  - Обязателен файл `bin/zapret/etc/nsswitch.conf` (`passwd: files`, `group: files`) — без него Cygwin на доменных ПК в офлайне падает/зависает на поиске учётной записи.
  - Нельзя включать `--debug=@winws_debug.log` в продакшене (быстро забивает десятки МБ логов и тормозит трафик).
  - Нельзя выносить `Diagnostics.ProbeSiteDetailed` без проверки на `502`/`504` (иначе локальный прокси ошибочно считает таймаут успешным HTTP-ответом).
- **Проверенный профиль против жёсткой фильтрации YouTube SNI (Билайн)**:
  `z-hostfake-md5`: `--dpi-desync=hostfakesplit --dpi-desync-hostfakesplit-midhost=midsld --dpi-desync-hostfakesplit-mod=host=ya.ru,altorder=1 --dpi-desync-fooling=md5sig`.
- **Локальный PAC-прокси (`SmartProxyServer.cs`, `127.0.0.1:1085`)**: направляет через обход только домены из `config/sites.txt`, не мешая стороннему VPN/IDE.

### Android (`android/`)
- **Сборка и тесты**:
  ```powershell
  cd C:\Users\d4200-01-156\Desktop\DPI\android
  $env:JAVA_HOME = 'C:\Program Files\Microsoft\jdk-17.0.21.21-hotspot'
  $env:ANDROID_HOME = 'C:\Users\d4200-01-156\AppData\Local\Android\Sdk'
  .\gradlew.bat clean testDebugUnitTest assembleDebug --no-daemon
  ```
- **Исполняемые бинарники Zapret v72.13**: `libtpws.so` и `libnfqws.so` лежат в `app/src/main/jniLibs/<abi>/` с `jniLibs { useLegacyPackaging = true }` (`android:extractNativeLibs="true"` в итоговом манифесте), иначе SELinux Android 10+ блокирует `execve()` из `filesDir`.
- **Режим без Root (`DpiVpnService` + `com.zaneschepke:hevtunnel:1.0.1` + `LocalSocksBridge` + `libtpws.so`)**:
  - `addDisallowedApplication(packageName)` исключает исходящие сокеты самого моста и `libtpws.so` из TUN.
  - `LocalSocksBridge` (`127.0.0.1:1081` → `tpws:1080`) реализует `SOCKS5 UDP ASSOCIATE` (отсутствует в `tpws`): DNS `:53` пересылает на `77.88.8.8:1253`, отбрасывает QUIC (`UDP:443`) при включённой опции и **немедленно отвечает пустым `NOERROR` на `AAAA` DNS-запросы** + отклоняет IPv6 TCP кодом `0x04` (маршрут `::/0` в `tun0` блокирует утечку IPv6 у мобильных операторов).
  - **Критично для нативного клиента YouTube (`Cronet`)**:
    1. В `tpws` (`protocol.c`) `IsTLSClientHello(..., bPartialIsOK=false)` требует, чтобы весь TLS ClientHello пришёл одним `recv()`. Крупный постквантовый ClientHello из приложения YouTube (>1460 байт при MTU 1500) рвался на 2 сегмента, и `tpws` пропускал его как `UNKNOWN`. `LocalSocksBridge.forwardInitialTlsClientHello` собирает весь TLS-фрейм перед отправкой в `tpws` (`RD_BLOCK_SIZE = 65536`).
    2. `AppConfig.YOUTUBE_APP_DOMAINS` (`youtubei.googleapis.com`, `jnn-pa.googleapis.com`, `www.googleapis.com`, `oauth2.googleapis.com`, `play.googleapis.com`, `s.youtube.com`, `gvt1.com` и др.) автоматически мигрирует в `sites.txt` через `KEY_SITES_VERSION`.
- **USB/Wi-Fi модем**: при раздаче интернета на ПК **Mirage на телефоне следует выключать**, оставляя включённым только Mirage на ПК (иначе двойная терминация TCP в `hev-socks5-tunnel` стирает `md5sig`/сегментацию `winws.exe`).

### macOS (`macos/`)
- **Сборка `.dmg` прямо из Windows**:
  ```cmd
  cd C:\Users\d4200-01-156\Desktop\DPI\macos
  build_dmg.bat
  ```
- **Архитектура**:
  - `build_dmg.bat` прогоняет `go test . -v`, кросс-компилирует `GOOS=darwin GOARCH=amd64` и `arm64`, а утилита `cmd/mkdmg` (на базе `go.digitalxero.dev/osx`) собирает FAT Mach-O (`0xCAFEBABE`, выравнивание 16 КБ под Apple Silicon), подписывает ad-hoc (`sign.NewCodeSigner().WithAdHoc()`), вкладывает универсальный `tpws` (`zapret v72.13`), иконку `Mirage.icns`, `sites.txt` и упаковывает в сжатый UDIF `.dmg` с проверкой `dmg.Verify()` и прав `0755`.
  - Без root на Mac: настраивает PAC `http://127.0.0.1:1085/proxy.pac` через `networksetup` и сбрасывает при выходе.

---

## 3. Текущие проверки (последние результаты)
- **Windows**: `build.bat` → `Mirage.exe`, XAML валидирован через `XamlReader.Parse`.
- **Android**: `.\gradlew.bat clean testDebugUnitTest assembleDebug --no-daemon` → `CoreLogicTest` (`7/7` тестов пройдены).
- **macOS**: `go test . -count=1 -v` → `4/4` тестов пройдены; `build_dmg.bat` → `DMG_BUILT_AND_VERIFIED`.

