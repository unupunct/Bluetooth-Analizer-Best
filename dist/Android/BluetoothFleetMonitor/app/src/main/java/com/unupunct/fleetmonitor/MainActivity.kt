package com.unupunct.fleetmonitor

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.ui.Alignment
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.DateRange
import androidx.compose.material.icons.filled.List
import androidx.compose.material.icons.filled.LocationOn
import androidx.compose.material.icons.filled.Menu
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material3.Icon
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.NavigationBarItemDefaults
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.collectAsState
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.ui.Modifier
import androidx.hilt.navigation.compose.hiltViewModel
import androidx.navigation.NavGraph.Companion.findStartDestination
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import com.unupunct.fleetmonitor.ui.*
import dagger.hilt.android.AndroidEntryPoint

@AndroidEntryPoint
class MainActivity : ComponentActivity() {
    @javax.inject.Inject lateinit var sync: com.unupunct.fleetmonitor.sync.SyncManager

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        offerLink(intent)
        setContent { FleetTheme { AppRoot() } }
    }

    override fun onNewIntent(intent: android.content.Intent) {
        super.onNewIntent(intent)
        offerLink(intent)
    }

    /** A bfm://pair link only ever opens the confirmation on the Sync screen; it never pairs by itself. */
    private fun offerLink(i: android.content.Intent?) {
        val data = i?.data ?: return
        if (data.scheme == "bfm") { sync.offer(data.toString()); i.data = null }
    }
}

private data class Tab(val route: String, val label: String, val icon: androidx.compose.ui.graphics.vector.ImageVector)

@Composable
private fun AppRoot() {
    val vm: FleetViewModel = hiltViewModel()
    val nav = rememberNavController()
    val tabs = listOf(
        Tab("scanner", "Scan", Icons.Default.PlayArrow), Tab("nearby", "Nearby", Icons.Default.List),
        Tab("radar", "Radar", Icons.Default.LocationOn), Tab("sessions", "Sessions", Icons.Default.DateRange), Tab("more", "More", Icons.Default.Menu),
    )
    val entry by nav.currentBackStackEntryAsState()
    val current = entry?.destination?.route
    fun go(route: String) = nav.navigate(route) { launchSingleTop = true }
    val pendingPair by vm.sync.pendingPair.collectAsState()
    androidx.compose.runtime.LaunchedEffect(pendingPair) { if (pendingPair != null && nav.currentDestination?.route != "sync") go("sync") }

    Scaffold(
        containerColor = FleetColors.Bg,
        bottomBar = {
            NavigationBar(containerColor = FleetColors.Panel) {
                tabs.forEach { t ->
                    NavigationBarItem(
                        selected = current == t.route, onClick = { nav.navigate(t.route) { popUpTo(nav.graph.findStartDestination().id) { saveState = true }; launchSingleTop = true; restoreState = true } },
                        icon = { Icon(t.icon, t.label) }, label = { Text(t.label) },
                        colors = NavigationBarItemDefaults.colors(indicatorColor = FleetColors.AccentDim, selectedIconColor = FleetColors.Accent, selectedTextColor = FleetColors.Accent,
                            unselectedIconColor = FleetColors.Muted, unselectedTextColor = FleetColors.Muted),
                    )
                }
            }
        },
    ) { pad ->
        val settings by vm.settings.collectAsState()
        Column(Modifier.fillMaxSize().padding(pad).statusBarsPadding()) {
          if (settings.demoMode) Box(Modifier.fillMaxWidth().background(FleetColors.WarnBg).padding(vertical = 5.dp), contentAlignment = Alignment.Center) {
              Text("DEMO DATA: fictional devices, not real measurements", color = FleetColors.Warn, fontSize = 12.sp, fontWeight = FontWeight.SemiBold)
          }
          Box(Modifier.weight(1f).fillMaxWidth()) {
            NavHost(nav, startDestination = "scanner") {
                composable("scanner") {
                    ScannerScreen(vm) { nav.navigate("radar") { popUpTo(nav.graph.findStartDestination().id) { saveState = true }; launchSingleTop = true; restoreState = true } }
                }
                composable("nearby") { NearbyScreen(vm) { go("inspector/$it") } }
                composable("radar") { RadarScreen(vm) { go("inspector/$it") } }
                composable("sessions") { SessionsScreen(vm) { go("inspector/$it") } }
                composable("more") { MoreScreen(vm, { go("history") }, { go("sync") }, { go("settings") }) }
                composable("history") { HistoryScreen(vm, null) }
                composable("history/{address}") { b -> HistoryScreen(vm, b.arguments?.getString("address")) }
                composable("inspector/{address}") { b ->
                    val a = b.arguments?.getString("address").orEmpty()
                    InspectorScreen(vm, a, { go("history/$it") }, { nav.popBackStack() }) { addr ->
                        vm.select(addr)
                        nav.navigate("radar") { popUpTo(nav.graph.findStartDestination().id) { saveState = true }; launchSingleTop = true; restoreState = true }
                    }
                }
                composable("sync") { SyncScreen(vm) }
                composable("settings") { SettingsScreen(vm) }
            }
          }
        }
    }
}
