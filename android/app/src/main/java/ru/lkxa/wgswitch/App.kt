package ru.lkxa.wgswitch

import android.app.Application
import android.content.Context
import ru.lkxa.wgswitch.data.Store
import ru.lkxa.wgswitch.data.Updates
import ru.lkxa.wgswitch.ui.Haptics
import ru.lkxa.wgswitch.vpn.StatusUi
import ru.lkxa.wgswitch.vpn.Tunnels

/** Одно на процесс: настройки, туннель и вибрация общие для окна, плитки в шторке и «постоянного VPN». */
class App : Application() {
    lateinit var store: Store
    lateinit var tunnels: Tunnels
    lateinit var haptics: Haptics
    lateinit var statusUi: StatusUi
    lateinit var updates: Updates

    override fun onCreate() {
        super.onCreate()
        store = Store(filesDir)
        tunnels = Tunnels(this, store)
        haptics = Haptics(this)
        Tunnels.registerAlwaysOn(tunnels, store)
        statusUi = StatusUi(this).also { it.start() }
        updates = Updates(this, store)
    }

    companion object {
        fun of(context: Context) = context.applicationContext as App
    }
}
