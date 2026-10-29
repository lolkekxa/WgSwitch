package ru.lkxa.wgswitch.ui

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.unit.dp
import ru.lkxa.wgswitch.App
import ru.lkxa.wgswitch.data.UpdateState

/** Под карточкой на главной, как у Windows-версии: «Вышла версия X · Обновить», прогресс скачивания или ошибка. */
@Composable
fun UpdateLine(app: App) {
    val state by app.updates.state.collectAsState()
    AnimatedVisibility(state !is UpdateState.None, enter = fadeIn(tween(400)), exit = fadeOut(tween(250))) {
        val (text, dot, clickable) = when (val s = state) {
            is UpdateState.Available -> Triple("Вышла версия ${s.release.version} · Обновить", C.Green, true)
            is UpdateState.Downloading -> Triple("Скачиваю ${s.release.version}… ${(s.progress * 100).toInt()}%", C.Amber, false)
            is UpdateState.Failed -> Triple("Обновление: ${s.message} · Ещё раз", C.Red, true)
            UpdateState.None -> Triple("", C.Muted, false)
        }
        Row(
            Modifier.padding(top = 12.dp).height(40.dp).press({ app.haptics.light(); app.updates.install() }, enabled = clickable),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Box(Modifier.size(6.dp).clip(CircleShape).background(dot))
            Spacer(Modifier.width(8.dp))
            Txt(text, T.label, color = if (clickable) C.Fg else C.Muted)
        }
    }
}
