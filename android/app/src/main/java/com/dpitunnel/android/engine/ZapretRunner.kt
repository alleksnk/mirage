package com.dpitunnel.android.engine

import android.content.Context
import com.dpitunnel.android.core.AppConfig
import com.dpitunnel.android.core.RuntimeLogger
import java.io.BufferedReader
import java.io.File
import java.io.InputStreamReader
import java.net.InetSocketAddress
import java.net.Socket

data class StrategyProfile(
    val id: String,
    val args: List<String>
)

class ZapretRunner(private val context: Context) {

    @Volatile
    private var activeProcess: Process? = null

    @Volatile
    private var rootRulesActive = false

    @Volatile
    var activeLabel: String = "Zapret"
        private set

    val tpwsCandidates = listOf(
        StrategyProfile("tpws-disorder-sni", listOf("--disorder", "--split-pos=1,sniext+1,host+1,midsld,endhost-1")),
        StrategyProfile("tpws-disorder-midsld", listOf("--disorder", "--split-pos=1,midsld")),
        StrategyProfile("tpws-disorder-1", listOf("--disorder", "--split-pos=1")),
        StrategyProfile("tpws-tlsrec-sni", listOf("--tlsrec=sni", "--split-pos=1,midsld")),
        StrategyProfile("tpws-tlsrec-disorder", listOf("--tlsrec=sni", "--disorder", "--split-pos=1")),
        StrategyProfile("tpws-oob-sni", listOf("--oob", "--split-pos=1,sniext+1")),
        StrategyProfile("tpws-split-any", listOf("--disorder", "--split-any-protocol", "--split-pos=1,midsld"))
    )

    val nfqwsCandidates = listOf(
        StrategyProfile(
            "z-hostfake-md5",
            listOf(
                "--dpi-desync=hostfakesplit",
                "--dpi-desync-hostfakesplit-midhost=midsld",
                "--dpi-desync-hostfakesplit-mod=host=ya.ru,altorder=1",
                "--dpi-desync-fooling=md5sig"
            )
        ),
        StrategyProfile(
            "z-multidisorder",
            listOf("--dpi-desync=multidisorder", "--dpi-desync-split-pos=1,sniext+1,host+1,midsld,endhost-1")
        ),
        StrategyProfile(
            "z-seqovl-split",
            listOf(
                "--dpi-desync=multisplit",
                "--dpi-desync-split-pos=2,midsld",
                "--dpi-desync-split-seqovl=681",
                "--dpi-desync-split-seqovl-pattern=@files/tls_clienthello_www_google_com.bin"
            )
        ),
        StrategyProfile(
            "z-hostfake-badseq",
            listOf(
                "--dpi-desync=hostfakesplit",
                "--dpi-desync-hostfakesplit-midhost=midsld",
                "--dpi-desync-hostfakesplit-mod=host=ya.ru,altorder=1",
                "--dpi-desync-fooling=badseq",
                "--dpi-desync-badseq-increment=0x80000000"
            )
        ),
        StrategyProfile("z-dis-1", listOf("--dpi-desync=multidisorder", "--dpi-desync-split-pos=1"))
    )

    fun startTpws(port: Int, profile: StrategyProfile, hostlistFile: File): Boolean {
        stop()
        val bin = File(context.applicationInfo.nativeLibraryDir, "libtpws.so")
        if (!bin.exists()) {
            RuntimeLogger.write("TPWS-ERR", "Binary not found: ${bin.absolutePath}")
            return false
        }
        val cmd = mutableListOf(
            bin.absolutePath,
            "--socks",
            "--bind-addr=127.0.0.1",
            "--port=$port",
            "--hostlist=${hostlistFile.absolutePath}"
        )
        cmd.addAll(profile.args)
        RuntimeLogger.write("CMD", cmd.joinToString(" "))
        return try {
            val proc = ProcessBuilder(cmd)
                .directory(context.filesDir)
                .redirectErrorStream(true)
                .start()
            activeProcess = proc
            activeLabel = "tpws (${profile.id})"
            pumpOutput(proc, "TPWS")
            waitForTcpPort("127.0.0.1", port, 1800)
        } catch (e: Exception) {
            RuntimeLogger.write("TPWS-EX", e.message ?: "error")
            false
        }
    }

    fun isRootAvailable(): Boolean {
        return try {
            val p = ProcessBuilder("su", "-c", "id").redirectErrorStream(true).start()
            val out = p.inputStream.bufferedReader().readText()
            p.waitFor() == 0 && out.contains("uid=0")
        } catch (_: Exception) {
            false
        }
    }

    fun startNfqwsRoot(profile: StrategyProfile, hostlistFile: File, dnsHost: String, dnsPort: Int): Boolean {
        stop()
        val bin = File(context.applicationInfo.nativeLibraryDir, "libnfqws.so")
        if (!bin.exists()) {
            RuntimeLogger.write("NFQWS-ERR", "Binary not found: ${bin.absolutePath}")
            return false
        }
        val nfqArgs = mutableListOf(
            bin.absolutePath,
            "--qnum=200",
            "--dpi-desync-fwmark=0x40000000",
            "--hostlist=${hostlistFile.absolutePath}"
        )
        nfqArgs.addAll(profile.args)
        val cmdStr = nfqArgs.joinToString(" ")
        RuntimeLogger.write("CMD-ROOT", cmdStr)

        if (!installIptables(dnsHost, dnsPort)) {
            RuntimeLogger.write("NFQWS-ERR", "Failed to apply root iptables NFQUEUE rules")
            return false
        }
        rootRulesActive = true

        return try {
            val proc = ProcessBuilder("su", "-c", cmdStr)
                .directory(context.filesDir)
                .redirectErrorStream(true)
                .start()
            activeProcess = proc
            activeLabel = "Root nfqws (${profile.id})"
            pumpOutput(proc, "NFQWS")
            Thread.sleep(300)
            procIsAlive(proc)
        } catch (e: Exception) {
            RuntimeLogger.write("NFQWS-EX", e.message ?: "error")
            cleanupIptables()
            false
        }
    }

    fun stop() {
        try {
            activeProcess?.destroy()
        } catch (_: Exception) {}
        activeProcess = null
        if (rootRulesActive) {
            cleanupIptables()
            rootRulesActive = false
        }
    }

    fun isRunning(): Boolean {
        val p = activeProcess ?: return false
        return procIsAlive(p)
    }

    private fun procIsAlive(p: Process): Boolean {
        return try {
            p.exitValue()
            false
        } catch (_: IllegalThreadStateException) {
            true
        }
    }

    private fun installIptables(dnsHost: String, dnsPort: Int): Boolean {
        cleanupIptables()
        val script = """
            iptables -t mangle -I OUTPUT -p tcp --dport 443 -m connbytes 1:15 --connbytes-dir original --connbytes-mode packets -m mark ! --mark 0x40000000/0x40000000 -m comment --comment "dpi_tunnel_nfq" -j NFQUEUE --queue-num 200 --queue-bypass || \
            iptables -t mangle -I OUTPUT -p tcp --dport 443 -m mark ! --mark 0x40000000/0x40000000 -m comment --comment "dpi_tunnel_nfq" -j NFQUEUE --queue-num 200 --queue-bypass
            iptables -t nat -I OUTPUT -p udp --dport 53 -m comment --comment "dpi_tunnel_dns" -j DNAT --to-destination $dnsHost:$dnsPort
        """.trimIndent()
        return runSuScript(script)
    }

    private fun cleanupIptables() {
        val script = """
            while iptables -t mangle -D OUTPUT -p tcp --dport 443 -m connbytes 1:15 --connbytes-dir original --connbytes-mode packets -m mark ! --mark 0x40000000/0x40000000 -m comment --comment "dpi_tunnel_nfq" -j NFQUEUE --queue-num 200 --queue-bypass 2>/dev/null; do :; done
            while iptables -t mangle -D OUTPUT -p tcp --dport 443 -m mark ! --mark 0x40000000/0x40000000 -m comment --comment "dpi_tunnel_nfq" -j NFQUEUE --queue-num 200 --queue-bypass 2>/dev/null; do :; done
            iptables -t nat -S OUTPUT 2>/dev/null | grep "dpi_tunnel_dns" | sed 's/^-A /-D /' | while read r; do iptables -t nat ${'$'}r 2>/dev/null; done
        """.trimIndent()
        runSuScript(script)
    }

    private fun runSuScript(script: String): Boolean {
        return try {
            val p = ProcessBuilder("su", "-c", script).redirectErrorStream(true).start()
            val out = p.inputStream.bufferedReader().readText().trim()
            if (out.isNotEmpty()) RuntimeLogger.write("SU", out)
            p.waitFor() == 0
        } catch (e: Exception) {
            RuntimeLogger.write("SU-ERR", e.message ?: "error")
            false
        }
    }

    private fun pumpOutput(proc: Process, tag: String) {
        Thread({
            try {
                BufferedReader(InputStreamReader(proc.inputStream)).use { br ->
                    while (true) {
                        val line = br.readLine() ?: break
                        if (line.isNotBlank()) RuntimeLogger.write(tag, line)
                    }
                }
            } catch (_: Exception) {
            }
        }, "$tag-Logger").apply { isDaemon = true; start() }
    }

    private fun waitForTcpPort(host: String, port: Int, timeoutMs: Int): Boolean {
        val deadline = System.currentTimeMillis() + timeoutMs
        while (System.currentTimeMillis() < deadline) {
            try {
                Socket().use { s ->
                    s.connect(InetSocketAddress(host, port), 250)
                    return true
                }
            } catch (_: Exception) {
                Thread.sleep(60)
            }
        }
        return false
    }
}

