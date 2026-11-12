package com.dpitunnel.android.core

import android.content.Context
import java.io.File

enum class EngineMode(val displayName: String) {
    VPN_TPWS("Без Root: VPN + Zapret tpws (универсальный)"),
    ROOT_NFQWS("Root: Zapret nfqws (hostfake-md5 + YouTube)")
}

object AppConfig {
    private const val PREFS = "dpi_tunnel_prefs"
    private const val KEY_ENGINE_MODE = "engine_mode"
    private const val KEY_DNS_HOST = "dns_host"
    private const val KEY_DNS_PORT = "dns_port"
    private const val KEY_BLOCK_QUIC = "block_quic"
    private const val KEY_AUTO_PROBE = "auto_probe"
    private const val KEY_SITES_VERSION = "sites_version"
    private const val CURRENT_SITES_VERSION = 3

    val YOUTUBE_APP_DOMAINS = listOf(
        "youtubei.googleapis.com",
        "jnn-pa.googleapis.com",
        "youtube.googleapis.com",
        "youtubeembeddedplayer.googleapis.com",
        "www.googleapis.com",
        "oauth2.googleapis.com",
        "play.googleapis.com",
        "android.clients.google.com",
        "clientservices.googleapis.com",
        "firebaseremoteconfig.googleapis.com",
        "firebaseinstallations.googleapis.com",
        "s.youtube.com",
        "video-stats.l.google.com",
        "yt3.googleusercontent.com",
        "lh3.googleusercontent.com",
        "suggestqueries-clients6.google.com",
        "suggestqueries.google.com",
        "gvt1.com",
        "gvt2.com"
    )

    const val TG_PRIMARY_URL =
        "tg://proxy?server=156.239.251.46&port=443&secret=eeff2a510da3958357083ec111f4de115f7777772e6d6963726f736f66742e636f6d"

    val EXCLUDED_ROOT_DOMAINS = setOf("googleapis.com", "github.com")

    fun getEngineMode(context: Context): EngineMode {
        val name = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .getString(KEY_ENGINE_MODE, EngineMode.VPN_TPWS.name)
        return runCatching { EngineMode.valueOf(name ?: EngineMode.VPN_TPWS.name) }
            .getOrDefault(EngineMode.VPN_TPWS)
    }

    fun setEngineMode(context: Context, mode: EngineMode) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .edit().putString(KEY_ENGINE_MODE, mode.name).apply()
    }

    fun getDnsHost(context: Context): String =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .getString(KEY_DNS_HOST, "77.88.8.8") ?: "77.88.8.8"

    fun getDnsPort(context: Context): Int =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .getInt(KEY_DNS_PORT, 1253).coerceIn(1, 65535)

    fun isBlockQuic(context: Context): Boolean =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .getBoolean(KEY_BLOCK_QUIC, true)

    fun isAutoProbe(context: Context): Boolean =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .getBoolean(KEY_AUTO_PROBE, true)

    fun saveSettings(context: Context, dnsHost: String, dnsPort: Int, blockQuic: Boolean, autoProbe: Boolean) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
            .putString(KEY_DNS_HOST, dnsHost.trim().ifEmpty { "77.88.8.8" })
            .putInt(KEY_DNS_PORT, dnsPort.coerceIn(1, 65535))
            .putBoolean(KEY_BLOCK_QUIC, blockQuic)
            .putBoolean(KEY_AUTO_PROBE, autoProbe)
            .apply()
    }

    fun ensureAssetsPrepared(context: Context): File {
        val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val sitesFile = File(context.filesDir, "sites.txt")
        if (!sitesFile.exists()) {
            val content = context.assets.open("sites.txt").bufferedReader().use { it.readText() }
            sitesFile.writeText(content, Charsets.UTF_8)
            prefs.edit().putInt(KEY_SITES_VERSION, CURRENT_SITES_VERSION).apply()
        } else if (prefs.getInt(KEY_SITES_VERSION, 1) < CURRENT_SITES_VERSION) {
            val existing = sitesFile.readText(Charsets.UTF_8)
            val updated = mergeDomains(existing, YOUTUBE_APP_DOMAINS)
            if (updated != existing) {
                sitesFile.writeText(updated, Charsets.UTF_8)
            }
            prefs.edit().putInt(KEY_SITES_VERSION, CURRENT_SITES_VERSION).apply()
        }
        val filesDir = File(context.filesDir, "files")
        if (!filesDir.exists()) filesDir.mkdirs()
        for (binName in listOf("tls_clienthello_www_google_com.bin", "tls_clienthello_vk_com.bin")) {
            val dst = File(filesDir, binName)
            if (!dst.exists()) {
                context.assets.open("files/$binName").use { input ->
                    dst.outputStream().use { out -> input.copyTo(out) }
                }
            }
        }
        return sitesFile
    }

    fun readSitesRaw(context: Context): String {
        val file = ensureAssetsPrepared(context)
        return file.readText(Charsets.UTF_8)
    }

    fun saveSitesRaw(context: Context, raw: String) {
        val file = File(context.filesDir, "sites.txt")
        file.writeText(raw, Charsets.UTF_8)
    }

    fun resetSites(context: Context): String {
        val content = context.assets.open("sites.txt").bufferedReader().use { it.readText() }
        saveSitesRaw(context, content)
        return content
    }

    fun mergeDomains(existingRaw: String, domainsToAdd: List<String>): String {
        val present = filterHostlistLines(existingRaw).toHashSet()
        val missing = domainsToAdd
            .map { it.trim().lowercase() }
            .filter { it.isNotEmpty() && it !in EXCLUDED_ROOT_DOMAINS && !present.contains(it) }
        if (missing.isEmpty()) return existingRaw
        val prefix = existingRaw.trimEnd()
        return if (prefix.isEmpty()) {
            missing.joinToString("\n") + "\n"
        } else {
            prefix + "\n" + missing.joinToString("\n") + "\n"
        }
    }

    fun filterHostlistLines(raw: String): List<String> {
        val out = LinkedHashSet<String>()
        raw.lineSequence().forEach { line ->
            val d = line.trim().lowercase()
            if (d.isEmpty() || d.startsWith("#")) return@forEach
            if (d in EXCLUDED_ROOT_DOMAINS) return@forEach
            out.add(d)
        }
        return out.toList()
    }

    fun writeZapretBlacklist(context: Context): File {
        val domains = filterHostlistLines(readSitesRaw(context))
        val target = File(context.filesDir, "gdpi_blacklist.txt")
        // UTF-8 without BOM + LF line endings for C/Zapret hostlist parser
        target.writeText(domains.joinToString("\n") + "\n", Charsets.UTF_8)
        return target
    }
}

