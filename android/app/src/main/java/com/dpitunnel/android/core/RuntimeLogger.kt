package com.dpitunnel.android.core

import android.content.Context
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.concurrent.CopyOnWriteArrayList

object RuntimeLogger {
    private const val MAX_LOG_BYTES = 512 * 1024L
    private const val MAX_MEMORY_LINES = 300

    private val lock = Any()
    private val memoryLines = ArrayDeque<String>(MAX_MEMORY_LINES)
    private val listeners = CopyOnWriteArrayList<() -> Unit>()
    private val sdf = SimpleDateFormat("HH:mm:ss.SSS", Locale.US)

    @Volatile
    private var logFile: File? = null

    fun init(context: Context) {
        val f = File(context.filesDir, "dpi_android.log")
        if (f.exists() && f.length() > MAX_LOG_BYTES) {
            val bak = File(context.filesDir, "dpi_android.log.bak")
            if (bak.exists()) bak.delete()
            f.renameTo(bak)
        }
        logFile = f
    }

    fun write(category: String, message: String) {
        val ts = synchronized(sdf) { sdf.format(Date()) }
        val line = "[$ts] [$category] $message"
        synchronized(lock) {
            if (memoryLines.size >= MAX_MEMORY_LINES) {
                memoryLines.removeFirst()
            }
            memoryLines.addLast(line)
            try {
                logFile?.appendText(line + "\n", Charsets.UTF_8)
            } catch (_: Exception) {
            }
        }
        listeners.forEach { it.invoke() }
    }

    fun snapshot(): String {
        synchronized(lock) {
            return memoryLines.joinToString("\n")
        }
    }

    fun clear() {
        synchronized(lock) {
            memoryLines.clear()
            try {
                logFile?.writeText("", Charsets.UTF_8)
            } catch (_: Exception) {
            }
        }
        listeners.forEach { it.invoke() }
    }

    fun addListener(listener: () -> Unit) {
        listeners.addIfAbsent(listener)
    }

    fun removeListener(listener: () -> Unit) {
        listeners.remove(listener)
    }
}

