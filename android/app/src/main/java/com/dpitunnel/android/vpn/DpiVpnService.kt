package com.dpitunnel.android.vpn

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.VpnService
import android.os.Build
import android.os.ParcelFileDescriptor
import androidx.core.app.NotificationCompat
import com.dpitunnel.android.MainActivity
import com.dpitunnel.android.R
import com.dpitunnel.android.core.AppConfig
import com.dpitunnel.android.core.EngineMode
import com.dpitunnel.android.core.RuntimeLogger
import com.dpitunnel.android.diagnostics.Diagnostics
import com.dpitunnel.android.engine.StrategyProfile
import com.dpitunnel.android.engine.ZapretRunner
import com.dpitunnel.android.proxy.LocalSocksBridge
import org.amnezia.awg.hevtunnel.TProxyService
import java.io.File
import java.util.concurrent.CopyOnWriteArrayList

object DpiState {
    @Volatile var running: Boolean = false
    @Volatile var probing: Boolean = false
    @Volatile var statusText: String = "Обход остановлен"
    @Volatile var activeStrategy: String = "—"

    private val listeners = CopyOnWriteArrayList<() -> Unit>()
    fun addListener(l: () -> Unit) { listeners.addIfAbsent(l) }
    fun removeListener(l: () -> Unit) { listeners.remove(l) }
    fun notifyChanged() { listeners.forEach { it.invoke() } }
}

class DpiVpnService : VpnService() {

    companion object {
        const val ACTION_START = "com.dpitunnel.android.ACTION_START"
        const val ACTION_STOP = "com.dpitunnel.android.ACTION_STOP"
        private const val CHANNEL_ID = "dpi_tunnel_channel"
        private const val NOTIF_ID = 1001
        private const val TPWS_PORT = 1080
        private const val BRIDGE_PORT = 1081
    }

    private var tunPfd: ParcelFileDescriptor? = null
    private var runner: ZapretRunner? = null
    private var bridge: LocalSocksBridge? = null
    private var hevRunning = false

    @Volatile
    private var generation = 0

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        RuntimeLogger.init(applicationContext)
        if (intent?.action == ACTION_STOP) {
            stopAll()
            stopSelf()
            return START_NOT_STICKY
        }

        ensureNotificationChannel()
        val notif = buildNotification("Запуск обхода DPI...")
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            startForeground(NOTIF_ID, notif, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        } else {
            startForeground(NOTIF_ID, notif)
        }

        val myGen = ++generation
        Thread({ startPipeline(myGen) }, "DpiVpnStart").start()
        return START_STICKY
    }

    private fun startPipeline(myGen: Int) {
        stopEnginesOnly()
        DpiState.probing = true
        DpiState.running = false
        DpiState.statusText = "Подготовка конфига и DNS..."
        DpiState.notifyChanged()

        AppConfig.ensureAssetsPrepared(applicationContext)
        val blacklist = AppConfig.writeZapretBlacklist(applicationContext)
        val dnsHost = AppConfig.getDnsHost(applicationContext)
        val dnsPort = AppConfig.getDnsPort(applicationContext)
        val blockQuic = AppConfig.isBlockQuic(applicationContext)
        val autoProbe = AppConfig.isAutoProbe(applicationContext)
        val mode = AppConfig.getEngineMode(applicationContext)

        val zap = ZapretRunner(applicationContext)
        runner = zap
        RuntimeLogger.write("ENGINE", "Start mode=${mode.name}, dns=$dnsHost:$dnsPort, blockQuic=$blockQuic, autoProbe=$autoProbe")

        if (mode == EngineMode.ROOT_NFQWS) {
            if (!zap.isRootAvailable()) {
                RuntimeLogger.write("ROOT-ERR", "Root (su) недоступен, переключаемся на VPN + tpws")
                AppConfig.setEngineMode(applicationContext, EngineMode.VPN_TPWS)
            } else {
                val winner = selectRootStrategy(zap, blacklist, dnsHost, dnsPort, autoProbe, myGen)
                if (generation != myGen) return
                if (winner != null) {
                    DpiState.probing = false
                    DpiState.running = true
                    DpiState.activeStrategy = "Root nfqws (${winner.id})"
                    DpiState.statusText = "Обход активен • ${DpiState.activeStrategy}"
                    DpiState.notifyChanged()
                    updateNotification(DpiState.statusText)
                    return
                }
            }
        }

        // Non-root VPN + HevSocks5Tunnel + LocalSocksBridge + Zapret tpws
        val winner = selectTpwsStrategy(zap, blacklist, autoProbe, myGen)
        if (generation != myGen) return
        if (winner == null) {
            DpiState.probing = false
            DpiState.running = false
            DpiState.statusText = "Ошибка запуска libtpws.so"
            DpiState.notifyChanged()
            stopAll()
            stopSelf()
            return
        }

        val socksBridge = LocalSocksBridge(BRIDGE_PORT, TPWS_PORT, dnsHost, dnsPort, blockQuic)
        if (!socksBridge.start()) {
            DpiState.probing = false
            DpiState.running = false
            DpiState.statusText = "Ошибка локального SOCKS5-моста"
            DpiState.notifyChanged()
            stopAll()
            stopSelf()
            return
        }
        bridge = socksBridge

        val pfd = Builder().apply {
            setSession("Mirage")
            addAddress("10.0.0.2", 30)
            addRoute("0.0.0.0", 0)
            addAddress("fd00:10::2", 126)
            addRoute("::", 0)
            addDnsServer(dnsHost)
            setMtu(1500)
            runCatching { addDisallowedApplication(packageName) }
        }.establish()

        if (pfd == null) {
            RuntimeLogger.write("VPN-ERR", "VpnService.Builder.establish() returned null")
            DpiState.probing = false
            DpiState.running = false
            DpiState.statusText = "Нет разрешения на VPN"
            DpiState.notifyChanged()
            stopAll()
            stopSelf()
            return
        }
        tunPfd = pfd

        val ymlFile = writeHevConfig(BRIDGE_PORT)
        TProxyService.TProxyStartService(ymlFile.absolutePath, pfd.fd)
        hevRunning = true

        DpiState.probing = false
        DpiState.running = true
        DpiState.activeStrategy = "VPN + tpws (${winner.id})"
        DpiState.statusText = "Обход активен • ${DpiState.activeStrategy}"
        DpiState.notifyChanged()
        updateNotification(DpiState.statusText)
        RuntimeLogger.write("VPN", "TUN established fd=${pfd.fd}, hev-socks5-tunnel active")
    }

    private fun selectTpwsStrategy(
        zap: ZapretRunner,
        blacklist: File,
        autoProbe: Boolean,
        myGen: Int
    ): StrategyProfile? {
        if (!autoProbe) {
            val first = zap.tpwsCandidates.first()
            return if (zap.startTpws(TPWS_PORT, first, blacklist)) first else null
        }

        var best: StrategyProfile? = null
        var bestScore = -1
        for (cand in zap.tpwsCandidates) {
            if (generation != myGen) return null
            DpiState.statusText = "Тест tpws [${cand.id}]..."
            DpiState.notifyChanged()
            if (!zap.startTpws(TPWS_PORT, cand, blacklist)) continue

            val res = Diagnostics.probeTriple(TPWS_PORT, 2700)
            RuntimeLogger.write("PROBE", "tpws [${cand.id}] -> YT=${res.ytDetail} | Rutor=${res.rutorDetail} | RuTracker=${res.rutrackerDetail} (score=${res.score})")
            if (res.score > bestScore) {
                bestScore = res.score
                best = cand
            }
            if (res.score == 7) return cand
        }
        if (best != null) {
            zap.startTpws(TPWS_PORT, best, blacklist)
        }
        return best
    }

    private fun selectRootStrategy(
        zap: ZapretRunner,
        blacklist: File,
        dnsHost: String,
        dnsPort: Int,
        autoProbe: Boolean,
        myGen: Int
    ): StrategyProfile? {
        if (!autoProbe) {
            val first = zap.nfqwsCandidates.first()
            return if (zap.startNfqwsRoot(first, blacklist, dnsHost, dnsPort)) first else null
        }
        var best: StrategyProfile? = null
        var bestScore = -1
        for (cand in zap.nfqwsCandidates) {
            if (generation != myGen) return null
            DpiState.statusText = "Тест Root nfqws [${cand.id}]..."
            DpiState.notifyChanged()
            if (!zap.startNfqwsRoot(cand, blacklist, dnsHost, dnsPort)) continue

            val res = Diagnostics.probeTriple(0, 2700)
            RuntimeLogger.write("PROBE", "nfqws [${cand.id}] -> YT=${res.ytDetail} | Rutor=${res.rutorDetail} | RuTracker=${res.rutrackerDetail} (score=${res.score})")
            if (res.score > bestScore) {
                bestScore = res.score
                best = cand
            }
            if (res.score == 7) return cand
        }
        if (best != null) {
            zap.startNfqwsRoot(best, blacklist, dnsHost, dnsPort)
        }
        return best
    }

    private fun writeHevConfig(bridgePort: Int): File {
        val yml = """
            tunnel:
              name: tun0
              mtu: 1500
              ipv4: 10.0.0.2
              ipv6: 'fd00:10::2'
            socks5:
              port: $bridgePort
              address: 127.0.0.1
              udp: 'udp'
            misc:
              task-stack-size: 86016
              log-level: warn
        """.trimIndent()
        val f = File(filesDir, "hev_tunnel.yml")
        f.writeText(yml, Charsets.UTF_8)
        return f
    }

    private fun stopEnginesOnly() {
        if (hevRunning) {
            runCatching { TProxyService.TProxyStopService() }
            hevRunning = false
        }
        runCatching { tunPfd?.close() }
        tunPfd = null
        bridge?.stop()
        bridge = null
        runner?.stop()
        runner = null
    }

    private fun stopAll() {
        generation++
        stopEnginesOnly()
        DpiState.probing = false
        DpiState.running = false
        DpiState.activeStrategy = "—"
        DpiState.statusText = "Обход остановлен"
        DpiState.notifyChanged()
        RuntimeLogger.write("VPN", "Service stopped cleanly")
    }

    override fun onRevoke() {
        stopAll()
        stopSelf()
        super.onRevoke()
    }

    override fun onDestroy() {
        stopAll()
        super.onDestroy()
    }

    private fun ensureNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val nm = getSystemService(NotificationManager::class.java)
            if (nm.getNotificationChannel(CHANNEL_ID) == null) {
                val ch = NotificationChannel(CHANNEL_ID, "Mirage", NotificationManager.IMPORTANCE_LOW)
                nm.createNotificationChannel(ch)
            }
        }
    }

    private fun buildNotification(text: String): Notification {
        val openIntent = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE
        )
        val stopIntent = PendingIntent.getService(
            this, 1, Intent(this, DpiVpnService::class.java).apply { action = ACTION_STOP },
            PendingIntent.FLAG_IMMUTABLE
        )
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_mirage_logo)
            .setContentTitle("Mirage")
            .setContentText(text)
            .setOngoing(true)
            .setContentIntent(openIntent)
            .addAction(0, "Остановить", stopIntent)
            .build()
    }

    private fun updateNotification(text: String) {
        val nm = getSystemService(NotificationManager::class.java)
        nm.notify(NOTIF_ID, buildNotification(text))
    }
}

