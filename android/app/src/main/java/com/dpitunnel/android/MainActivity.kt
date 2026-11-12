package com.dpitunnel.android

import android.Manifest
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import android.content.pm.PackageManager
import android.content.res.ColorStateList
import android.net.Uri
import android.net.VpnService
import android.os.Build
import android.os.Bundle
import android.view.View
import android.widget.AdapterView
import android.widget.ArrayAdapter
import android.widget.TextView
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.core.view.ViewCompat
import com.dpitunnel.android.core.AppConfig
import com.dpitunnel.android.core.EngineMode
import com.dpitunnel.android.core.RuntimeLogger
import com.dpitunnel.android.databinding.ActivityMainBinding
import com.dpitunnel.android.diagnostics.Diagnostics
import com.dpitunnel.android.vpn.DpiState
import com.dpitunnel.android.vpn.DpiVpnService
import com.google.android.material.tabs.TabLayout
import java.util.concurrent.atomic.AtomicInteger

class MainActivity : AppCompatActivity() {

    private lateinit var binding: ActivityMainBinding
    private val checkSeq = AtomicInteger(0)
    private lateinit var resourceViews: Map<String, Pair<View, TextView>>
    private var wasRunning = false

    private val vpnPermissionLauncher =
        registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { res ->
            if (res.resultCode == RESULT_OK) {
                launchVpnService()
            } else {
                Toast.makeText(this, "Разрешение на VPN не выдано", Toast.LENGTH_SHORT).show()
            }
        }

    private val notifPermissionLauncher =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) { _ -> }

    private val stateListener: () -> Unit = { runOnUiThread { renderState() } }
    private val logListener: () -> Unit = {
        runOnUiThread { binding.txtLogs.text = RuntimeLogger.snapshot() }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        RuntimeLogger.init(applicationContext)
        AppConfig.ensureAssetsPrepared(applicationContext)

        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)

        resourceViews = mapOf(
            "youtube" to (binding.dotYoutube to binding.txtStatusYoutube),
            "instagram" to (binding.dotInstagram to binding.txtStatusInstagram),
            "discord" to (binding.dotDiscord to binding.txtStatusDiscord),
            "telegram" to (binding.dotTelegram to binding.txtStatusTelegram),
            "rutracker" to (binding.dotRutracker to binding.txtStatusRutracker),
            "rutor" to (binding.dotRutor to binding.txtStatusRutor),
            "x" to (binding.dotX to binding.txtStatusX),
            "medium" to (binding.dotMedium to binding.txtStatusMedium)
        )

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            notifPermissionLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
        }

        setupTabs()
        setupEngineSpinner()
        setupActions()
        loadInitialData()
        renderState()
    }

    override fun onStart() {
        super.onStart()
        DpiState.addListener(stateListener)
        RuntimeLogger.addListener(logListener)
        binding.txtLogs.text = RuntimeLogger.snapshot()
        renderState()
    }

    override fun onStop() {
        DpiState.removeListener(stateListener)
        RuntimeLogger.removeListener(logListener)
        super.onStop()
    }

    private fun setupTabs() {
        val titles = listOf("Обзор", "Сайты", "Telegram", "Журнал", "Настройки")
        titles.forEach { binding.tabLayout.addTab(binding.tabLayout.newTab().setText(it)) }
        binding.tabLayout.addOnTabSelectedListener(object : TabLayout.OnTabSelectedListener {
            override fun onTabSelected(tab: TabLayout.Tab) {
                val idx = tab.position
                binding.panelHome.visibility = if (idx == 0) View.VISIBLE else View.GONE
                binding.panelSites.visibility = if (idx == 1) View.VISIBLE else View.GONE
                binding.panelTelegram.visibility = if (idx == 2) View.VISIBLE else View.GONE
                binding.panelLogs.visibility = if (idx == 3) View.VISIBLE else View.GONE
                binding.panelSettings.visibility = if (idx == 4) View.VISIBLE else View.GONE
            }

            override fun onTabUnselected(tab: TabLayout.Tab) {}
            override fun onTabReselected(tab: TabLayout.Tab) {}
        })
    }

    private fun setupEngineSpinner() {
        val items = EngineMode.values().map { it.displayName }
        val adapter = ArrayAdapter(this, android.R.layout.simple_spinner_dropdown_item, items)
        binding.spinnerEngine.adapter = adapter
        val cur = AppConfig.getEngineMode(this)
        binding.spinnerEngine.setSelection(cur.ordinal)
        binding.spinnerEngine.onItemSelectedListener = object : AdapterView.OnItemSelectedListener {
            override fun onItemSelected(parent: AdapterView<*>?, view: View?, pos: Int, id: Long) {
                val selected = EngineMode.values()[pos]
                AppConfig.setEngineMode(this@MainActivity, selected)
                binding.txtEngineBadge.text = if (selected == EngineMode.ROOT_NFQWS) "Root nfqws" else "VPN + tpws"
            }

            override fun onNothingSelected(parent: AdapterView<*>?) {}
        }
    }

    private fun setupActions() {
        binding.btnToggle.setOnClickListener {
            if (DpiState.running || DpiState.probing) {
                val stopIntent = Intent(this, DpiVpnService::class.java).apply {
                    action = DpiVpnService.ACTION_STOP
                }
                startService(stopIntent)
            } else {
                startWithPermissionCheck()
            }
        }

        binding.btnRunCheck.setOnClickListener { runDiagnostics() }

        binding.cardTelegram.setOnClickListener {
            binding.tabLayout.getTabAt(2)?.select()
        }

        binding.btnQuickAddSite.setOnClickListener {
            val clean = binding.editQuickAddSite.text.toString()
                .trim()
                .lowercase()
                .removePrefix("https://")
                .removePrefix("http://")
                .trimEnd('/')
            if (clean.isEmpty()) return@setOnClickListener
            val existing = binding.editSites.text.toString()
            if (!existing.lines().any { it.trim().equals(clean, ignoreCase = true) }) {
                val updated = if (existing.isBlank()) clean else existing.trimEnd() + "\n" + clean
                binding.editSites.setText(updated)
                AppConfig.saveSitesRaw(this, updated)
                AppConfig.writeZapretBlacklist(this)
            }
            binding.editQuickAddSite.text?.clear()
            Toast.makeText(this, "Добавлен: $clean", Toast.LENGTH_SHORT).show()
        }

        binding.btnSaveSites.setOnClickListener {
            AppConfig.saveSitesRaw(this, binding.editSites.text.toString())
            AppConfig.writeZapretBlacklist(this)
            Toast.makeText(this, "Список сайтов сохранён", Toast.LENGTH_SHORT).show()
        }

        binding.btnResetSites.setOnClickListener {
            binding.editSites.setText(AppConfig.resetSites(this))
            AppConfig.writeZapretBlacklist(this)
            Toast.makeText(this, "Список сброшен", Toast.LENGTH_SHORT).show()
        }

        binding.btnOpenTgProxy.setOnClickListener {
            RuntimeLogger.write("TG", "Opening MTProxy: ${AppConfig.TG_PRIMARY_URL}")
            runCatching {
                startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(AppConfig.TG_PRIMARY_URL)))
            }.onFailure {
                Toast.makeText(this, "Telegram не установлен на устройстве", Toast.LENGTH_SHORT).show()
            }
        }

        binding.btnCopyTgProxy.setOnClickListener {
            val cm = getSystemService(CLIPBOARD_SERVICE) as ClipboardManager
            cm.setPrimaryClip(ClipData.newPlainText("tg_proxy", AppConfig.TG_PRIMARY_URL))
            Toast.makeText(this, "Ссылка скопирована", Toast.LENGTH_SHORT).show()
        }

        binding.btnClearLogs.setOnClickListener { RuntimeLogger.clear() }

        binding.btnSaveSettings.setOnClickListener {
            val host = binding.editDnsHost.text.toString()
            val port = binding.editDnsPort.text.toString().toIntOrNull() ?: 1253
            AppConfig.saveSettings(
                this,
                host,
                port,
                binding.chkBlockQuic.isChecked,
                binding.chkAutoProbe.isChecked
            )
            Toast.makeText(this, "Настройки сохранены", Toast.LENGTH_SHORT).show()
        }
    }

    private fun loadInitialData() {
        binding.editSites.setText(AppConfig.readSitesRaw(this))
        binding.editDnsHost.setText(AppConfig.getDnsHost(this))
        binding.editDnsPort.setText(AppConfig.getDnsPort(this).toString())
        binding.chkBlockQuic.isChecked = AppConfig.isBlockQuic(this)
        binding.chkAutoProbe.isChecked = AppConfig.isAutoProbe(this)
    }

    private fun startWithPermissionCheck() {
        if (AppConfig.getEngineMode(this) == EngineMode.ROOT_NFQWS) {
            launchVpnService()
            return
        }
        val prep = VpnService.prepare(this)
        if (prep != null) {
            vpnPermissionLauncher.launch(prep)
        } else {
            launchVpnService()
        }
    }

    private fun launchVpnService() {
        val intent = Intent(this, DpiVpnService::class.java).apply {
            action = DpiVpnService.ACTION_START
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            startForegroundService(intent)
        } else {
            startService(intent)
        }
    }

    private fun runDiagnostics() {
        val runId = checkSeq.incrementAndGet()
        binding.btnRunCheck.isEnabled = false
        val amber = ContextCompat.getColor(this, R.color.accent_yellow)
        val green = ContextCompat.getColor(this, R.color.accent_green)
        val red = ContextCompat.getColor(this, R.color.accent_red)
        val cyan = ContextCompat.getColor(this, R.color.accent_cyan)
        val muted = ContextCompat.getColor(this, R.color.text_muted)

        resourceViews.values.forEach { (dot, txt) ->
            ViewCompat.setBackgroundTintList(dot, ColorStateList.valueOf(amber))
            txt.text = "..."
            txt.setTextColor(muted)
        }
        val total = Diagnostics.POPULAR_RESOURCES.size
        val okCount = AtomicInteger(0)
        binding.txtResourcesSummary.text = "Проверка..."
        binding.txtResourcesSummary.setTextColor(amber)

        val socksPort = if (DpiState.running && AppConfig.getEngineMode(this) == EngineMode.VPN_TPWS) 1080 else 0
        Diagnostics.probePopularResources(
            socksPort = socksPort,
            timeoutMs = 3500,
            onSingleResult = { res ->
                runOnUiThread {
                    if (checkSeq.get() != runId) return@runOnUiThread
                    if (res.ok) okCount.incrementAndGet()
                    resourceViews[res.id]?.let { (dot, txt) ->
                        val color = when {
                            res.ok -> green
                            res.id == "telegram" -> cyan
                            else -> red
                        }
                        ViewCompat.setBackgroundTintList(dot, ColorStateList.valueOf(color))
                        txt.text = res.shortStatus
                        txt.setTextColor(color)
                    }
                    val count = okCount.get()
                    binding.txtResourcesSummary.text = "$count / $total"
                    binding.txtResourcesSummary.setTextColor(if (count > 0) muted else red)
                }
            },
            onFinished = {
                runOnUiThread {
                    if (checkSeq.get() != runId) return@runOnUiThread
                    binding.btnRunCheck.isEnabled = true
                    val count = okCount.get()
                    binding.txtResourcesSummary.text = "$count / $total онлайн"
                    binding.txtResourcesSummary.setTextColor(
                        when {
                            count >= total - 1 -> green
                            count > 0 -> amber
                            else -> red
                        }
                    )
                }
            }
        )
    }

    private fun renderState() {
        val nowRunning = DpiState.running
        binding.txtMainStatus.text = DpiState.statusText
        val active = DpiState.running || DpiState.probing
        binding.btnToggle.text = getString(if (active) R.string.btn_stop else R.string.btn_start)
        val colorRes = when {
            DpiState.probing -> R.color.accent_yellow
            DpiState.running -> R.color.accent_red
            else -> R.color.accent_blue
        }
        val tintColor = ContextCompat.getColor(this, colorRes)
        binding.btnToggle.backgroundTintList = ColorStateList.valueOf(tintColor)

        val headerDotColorRes = when {
            DpiState.probing -> R.color.accent_yellow
            DpiState.running -> R.color.accent_green
            else -> R.color.accent_red
        }
        ViewCompat.setBackgroundTintList(
            binding.dotHeaderStatus,
            ColorStateList.valueOf(ContextCompat.getColor(this, headerDotColorRes))
        )
        binding.txtMainStatus.setTextColor(
            ContextCompat.getColor(
                this,
                when {
                    DpiState.probing -> R.color.accent_yellow
                    DpiState.running -> R.color.accent_green
                    else -> R.color.text_primary
                }
            )
        )
        binding.txtSubStatus.text = "DNS ${AppConfig.getDnsHost(this)}:${AppConfig.getDnsPort(this)} • ${DpiState.activeStrategy}"

        if (nowRunning && !wasRunning) {
            runDiagnostics()
        }
        wasRunning = nowRunning
    }
}
