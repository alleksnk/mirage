# DPI Tunnel for Android

Android-версия проекта **DPI Tunnel**, построенная на практическом опыте обхода DPI-блокировок из Windows-версии (`../windows`).

## Архитектура и ключевые решения из Windows-опыта

1. **Исполняемые бинарники Zapret v72.13 в `jniLibs` (`arm64-v8a`, `armeabi-v7a`, `x86`, `x86_64`)**:
   - `libtpws.so` (user-space SOCKS/прозрачный прокси из `zapret v72.13`) и `libnfqws.so` (NFQUEUE-движок для root-режима).
   - Используется `jniLibs { useLegacyPackaging = true }`, чтобы Android распаковывал их в `nativeLibraryDir` с правом исполнения (`execve`), так как SELinux на Android 10+ запрещает запуск бинарников из `filesDir`.
2. **Без Root (VpnService + HevSocks5Tunnel + `LocalSocksBridge` + `libtpws.so`)**:
   - `VpnService` (`DpiVpnService`) перенаправляет IPv4-трафик через `com.zaneschepke:hevtunnel:1.0.1` на локальный `LocalSocksBridge` (`127.0.0.1:1081`).
   - Само приложение исключается через `addDisallowedApplication(packageName)`, поэтому исходящие сокеты `LocalSocksBridge`, `libtpws.so` и DNS уходят напрямую в сеть без петли TUN.
   - `LocalSocksBridge` принимает `SOCKS5 TCP CONNECT` и проксирует через `libtpws.so` (`127.0.0.1:1080`, `--socks --hostlist=...`), а также реализует `SOCKS5 UDP ASSOCIATE` (чего нет в самом `tpws`):
     - **Чистый DNS без подмены и замедления UDP:53**: все DNS-запросы (порт `53`) пересылаются на `77.88.8.8:1253` (резерв `8.8.8.8:53`).
     - **Блокировка QUIC (UDP:443)**: отбрасывает UDP:443 по настройке, заставляя браузер и YouTube переходить на TCP TLS, где работает фрагментация ClientHello.
3. **Root-режим (`libnfqws.so` + `iptables`)**:
   - Прямой перенос рабочего профиля Windows `z-hostfake-md5` (`--dpi-desync=hostfakesplit --dpi-desync-hostfakesplit-midhost=midsld --dpi-desync-hostfakesplit-mod=host=ya.ru,altorder=1 --dpi-desync-fooling=md5sig`), правила `iptables -t mangle -j NFQUEUE --queue-num 200 --queue-bypass` и NAT-перенаправление `UDP:53 -> 77.88.8.8:1253`.
   - *Примечание*: на сетях с жёсткой фильтрацией SNI YouTube (как на Билайн) пользовательский `tpws` (без root) не может отправлять фейковые пакеты с `md5sig`, поэтому YouTube надёжнее всего работает в Root-режиме (`libnfqws.so`), тогда как без root-прав гарантируются чистый DNS `77.88.8.8:1253`, блокировка QUIC и разблокировка сайтов через TCP `--disorder`/`--oob`/`--tlsrec`.
4. **Выборочный список доменов (`assets/sites.txt`)**:
   - Исключены корневые `googleapis.com` и `github.com`, чтобы не ломать CDN/сервисы без необходимости, но сохранены `youtube.com`, `googlevideo.com`, `ytimg.com`, `rutor.info`, `rutracker.org`, `medium.com`, `x.com` и др.
5. **Диагностика и Telegram MTProxy**:
   - Реальная проверка сайтов и TLS-хендшейк MTProxy `156.239.251.46:443`.
   - Кнопка подключения Telegram в 1 клик (`tg://proxy?server=156.239.251.46&port=443&secret=eeff2a510da3958357083ec111f4de115f7777772e6d6963726f736f66742e636f6d`).
   - Постоянный лог в `dpi_android.log`.

## Сборка

```powershell
cd C:\Users\d4200-01-156\Desktop\DPI\Android
$env:JAVA_HOME = 'C:\Program Files\Microsoft\jdk-17.0.21.21-hotspot'
$env:ANDROID_HOME = 'C:\Users\d4200-01-156\AppData\Local\Android\Sdk'
.\gradlew.bat testDebugUnitTest assembleDebug --no-daemon
```

Готовый APK: `app/build/outputs/apk/debug/app-debug.apk`.

