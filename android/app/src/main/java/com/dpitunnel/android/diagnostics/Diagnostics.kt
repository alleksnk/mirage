package com.dpitunnel.android.diagnostics

import com.dpitunnel.android.core.RuntimeLogger
import java.net.InetSocketAddress
import java.net.Proxy
import java.net.Socket
import java.net.SocketTimeoutException
import java.net.URL
import java.net.UnknownHostException
import java.security.cert.X509Certificate
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocket
import javax.net.ssl.TrustManager
import javax.net.ssl.X509TrustManager

data class ProbeTripleResult(
    val ytOk: Boolean,
    val rutorOk: Boolean,
    val rutrackerOk: Boolean,
    val ytDetail: String,
    val rutorDetail: String,
    val rutrackerDetail: String
) {
    val score: Int get() = (if (ytOk) 4 else 0) + (if (rutorOk) 2 else 0) + (if (rutrackerOk) 1 else 0)
}

data class ResourceTarget(
    val id: String,
    val displayName: String,
    val url: String,
    val isTelegramProxy: Boolean = false
)

data class ResourceProbeResult(
    val id: String,
    val displayName: String,
    val ok: Boolean,
    val elapsedMs: Long,
    val shortStatus: String
)

object Diagnostics {

    val POPULAR_RESOURCES: List<ResourceTarget> = listOf(
        ResourceTarget("youtube", "YouTube", "https://www.youtube.com"),
        ResourceTarget("instagram", "Instagram", "https://www.instagram.com"),
        ResourceTarget("discord", "Discord", "https://discord.com"),
        ResourceTarget("telegram", "Telegram", "156.239.251.46:443", isTelegramProxy = true),
        ResourceTarget("rutracker", "RuTracker", "https://rutracker.org"),
        ResourceTarget("rutor", "Rutor.info", "https://rutor.info"),
        ResourceTarget("x", "X (Twitter)", "https://x.com"),
        ResourceTarget("medium", "Medium", "https://medium.com")
    )

    fun probeTriple(socksPort: Int, timeoutMs: Int = 2700): ProbeTripleResult {
        var ytOk = false
        var rutOk = false
        var cfOk = false
        var ytDet = "timeout"
        var rutDet = "timeout"
        var cfDet = "timeout"
        val latch = CountDownLatch(3)

        Thread {
            val (ok, det) = probeHttps("https://www.youtube.com", socksPort, timeoutMs)
            ytOk = ok; ytDet = det; latch.countDown()
        }.start()

        Thread {
            val (ok, det) = probeHttps("https://rutor.info", socksPort, timeoutMs)
            rutOk = ok; rutDet = det; latch.countDown()
        }.start()

        Thread {
            val (ok, det) = probeHttps("https://rutracker.org", socksPort, timeoutMs)
            cfOk = ok; cfDet = det; latch.countDown()
        }.start()

        latch.await((timeoutMs + 800).toLong(), TimeUnit.MILLISECONDS)
        return ProbeTripleResult(ytOk, rutOk, cfOk, ytDet, rutDet, cfDet)
    }

    fun probePopularResources(
        socksPort: Int,
        timeoutMs: Int = 3500,
        onSingleResult: (ResourceProbeResult) -> Unit,
        onFinished: () -> Unit
    ) {
        val remaining = AtomicInteger(POPULAR_RESOURCES.size)
        POPULAR_RESOURCES.forEach { target ->
            Thread {
                val res = if (target.isTelegramProxy) {
                    probeTelegramResource(target)
                } else {
                    probeWebResource(target, socksPort, timeoutMs)
                }
                onSingleResult(res)
                if (remaining.decrementAndGet() == 0) {
                    onFinished()
                }
            }.start()
        }
    }

    private fun probeWebResource(target: ResourceTarget, socksPort: Int, timeoutMs: Int): ResourceProbeResult {
        val start = System.currentTimeMillis()
        var conn: HttpsURLConnection? = null
        return try {
            val proxy = if (socksPort > 0) {
                Proxy(Proxy.Type.SOCKS, InetSocketAddress("127.0.0.1", socksPort))
            } else {
                Proxy.NO_PROXY
            }
            conn = (URL(target.url).openConnection(proxy) as HttpsURLConnection).apply {
                connectTimeout = timeoutMs
                readTimeout = timeoutMs
                instanceFollowRedirects = false
                setRequestProperty("User-Agent", "Mozilla/5.0 (Linux; Android 14) Chrome/128.0 Mobile")
            }
            val code = conn.responseCode
            val ms = System.currentTimeMillis() - start
            val ok = code in 100..499
            val short = if (ok) "$ms мс" else "Недоступен"
            RuntimeLogger.write("DIAG", "${target.displayName} (${target.url}) -> ${if (ok) "OK" else "FAIL"}($code, ${ms}ms)")
            ResourceProbeResult(target.id, target.displayName, ok, ms, short)
        } catch (e: Exception) {
            val ms = System.currentTimeMillis() - start
            val short = when (e) {
                is UnknownHostException -> "Нет DNS"
                is SocketTimeoutException -> "Таймаут"
                else -> "Недоступен"
            }
            RuntimeLogger.write("DIAG", "${target.displayName} (${target.url}) -> FAIL(${e.javaClass.simpleName}, ${ms}ms)")
            ResourceProbeResult(target.id, target.displayName, false, ms, short)
        } finally {
            try { conn?.disconnect() } catch (_: Exception) {}
        }
    }

    private fun probeTelegramResource(target: ResourceTarget): ResourceProbeResult {
        val start = System.currentTimeMillis()
        val (ok, label) = probeTelegramMtProxy()
        val ms = System.currentTimeMillis() - start
        val short = if (ok) label else "Нужен прокси"
        return ResourceProbeResult(target.id, target.displayName, ok, ms, short)
    }

    fun probeHttps(urlStr: String, socksPort: Int = 0, timeoutMs: Int = 4000): Pair<Boolean, String> {
        val start = System.currentTimeMillis()
        var conn: HttpsURLConnection? = null
        return try {
            val url = URL(urlStr)
            val proxy = if (socksPort > 0) {
                Proxy(Proxy.Type.SOCKS, InetSocketAddress("127.0.0.1", socksPort))
            } else {
                Proxy.NO_PROXY
            }
            conn = (url.openConnection(proxy) as HttpsURLConnection).apply {
                connectTimeout = timeoutMs
                readTimeout = timeoutMs
                instanceFollowRedirects = false
                setRequestProperty("User-Agent", "Mozilla/5.0 (Linux; Android 14) Chrome/128.0 Mobile")
            }
            val code = conn.responseCode
            val ms = System.currentTimeMillis() - start
            val ok = code in 100..499
            ok to "${if (ok) "OK" else "FAIL"}($code, ${ms}ms)"
        } catch (e: Exception) {
            val ms = System.currentTimeMillis() - start
            false to "FAIL(${e.javaClass.simpleName}: ${e.message}, ${ms}ms)"
        } finally {
            try { conn?.disconnect() } catch (_: Exception) {}
        }
    }

    fun probeTelegramMtProxy(host: String = "156.239.251.46", port: Int = 443, sni: String = "www.microsoft.com"): Pair<Boolean, String> {
        val start = System.currentTimeMillis()
        return try {
            val trustAll = arrayOf<TrustManager>(object : X509TrustManager {
                override fun checkClientTrusted(chain: Array<X509Certificate>, authType: String) {}
                override fun checkServerTrusted(chain: Array<X509Certificate>, authType: String) {}
                override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
            })
            val sslCtx = SSLContext.getInstance("TLS")
            sslCtx.init(null, trustAll, null)
            Socket().use { raw ->
                raw.connect(InetSocketAddress(host, port), 2500)
                raw.soTimeout = 2500
                (sslCtx.socketFactory.createSocket(raw, sni, port, true) as SSLSocket).use { ssl ->
                    ssl.startHandshake()
                }
            }
            val ms = System.currentTimeMillis() - start
            RuntimeLogger.write("DIAG", "Telegram MTProxy ($host:$port) -> OK (${ms}ms)")
            true to "${ms} мс"
        } catch (e: Exception) {
            val ms = System.currentTimeMillis() - start
            RuntimeLogger.write("DIAG", "Telegram MTProxy ($host:$port) -> FAIL (${e.message}, ${ms}ms)")
            false to "Нужен прокси"
        }
    }
}
