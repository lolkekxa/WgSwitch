package ru.lkxa.wgswitch.ui

import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.delay
import ru.lkxa.wgswitch.App
import ru.lkxa.wgswitch.data.PeerInfo

/** Строка, значение которой копируется касанием: на секунду показывает «скопировано ✓». */
@Composable
fun CopyRow(app: App, label: String, shown: String, copied: String, spoken: String? = null) {
    val context = currentContext()
    var done by remember { mutableStateOf(false) }
    LaunchedEffect(done) { if (done) { delay(1200); done = false } }
    val color by animateColorAsState(if (done) C.Green else C.Fg, tween(250), label = "copy")
    Row(
        Modifier.fillMaxWidth().height(48.dp)
            .then(if (spoken != null) Modifier.semantics(mergeDescendants = true) { contentDescription = spoken } else Modifier)
            .press({
            copy(context, copied)
            app.haptics.light()
            done = true
        }),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Txt(label, T.label, Modifier.weight(1f))
        Txt(if (done) "скопировано ✓" else shown, T.value, Modifier.widthIn(max = 200.dp), color = color, maxLines = 1)
    }
}

/**
 * Что сделать на сервере, чтобы он пустил телефон. Два копирования и одно предупреждение —
 * ровно те грабли, на которые легко наступить: syncconf оставляет только пиров из файла.
 */
@Composable
fun ServerSteps(app: App, name: String, peer: PeerInfo, index: String) {
    Section(index, "На сервере", Modifier.padding(top = 16.dp))
    Card {
        CopyRow(app, if (peer.amnezia) "1. Блок в awg0.conf" else "1. Блок в wg0.conf", "скопировать", peer.serverBlock(name))
        CopyRow(app, "2. Применить", "скопировать", peer.syncCommand)
        CopyRow(app, "Ключ телефона", shortKey(peer.publicKey), peer.publicKey, spoken = "Ключ телефона " + peer.publicKey)
    }
    Column(Modifier.padding(horizontal = 4.dp, vertical = 8.dp)) {
        Txt("Блок дописывается в конец ${peer.serverFile} на сервере, затем команда применяет его без перезапуска." +
            if (peer.amnezia) " Это AmneziaWG — WireGuard с маскировкой от блокировок." else "", T.hint)
        Txt(
            "Все устройства — ПК, телефон — должны быть в этом файле отдельными [Peer]. Кого там нет, команда отключит, " +
                "в том числе SSH, если вы зашли через VPN.",
            T.hint, Modifier.padding(top = 6.dp), color = C.Amber,
        )
    }
}

fun shortKey(key: String) = if (key.length > 14) key.take(6) + "…" + key.takeLast(6) else key
