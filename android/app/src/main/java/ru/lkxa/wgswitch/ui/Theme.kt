package ru.lkxa.wgswitch.ui

import androidx.compose.animation.core.CubicBezierEasing
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.Font
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp
import ru.lkxa.wgswitch.R

/** Те же токены, что в App.xaml у Windows-версии. */
object C {
    val Bg = Color(0xFF0A0A0A)
    val Surface = Color(0xFF111111)
    val Hover = Color(0xFF1A1A1A)
    val Fg = Color(0xFFEDEDED)
    val Muted = Color(0xFF7A7A7A)
    val Faint = Color(0xFF3A3A3A)
    val Line = Color(0xFF1F1F1F)
    val Hint = Color(0xFF5A5A5A)
    val Green = Color(0xFF22C55E)
    val Red = Color(0xFFEF4444)
    val Amber = Color(0xFFF59E0B)
    val Violet = Color(0xFF8B5CF6)
}

val Sans = FontFamily(Font(R.font.geist_regular, FontWeight.Normal), Font(R.font.geist_medium, FontWeight.Medium))
val Mono = FontFamily(Font(R.font.geist_mono, FontWeight.Normal))

/** ease-out-expo, как на сайте и в WPF-версии: cubic-bezier(0.22, 1, 0.36, 1). */
val Expo = CubicBezierEasing(0.22f, 1f, 0.36f, 1f)
val EaseInCubic = CubicBezierEasing(0.32f, 0f, 0.67f, 0f)
val SineInOut = CubicBezierEasing(0.37f, 0f, 0.63f, 1f)

/** Размеры текста — как на ПК, но на шаг крупнее: телефон держат дальше от глаз, чем смотрят в окно 320 px. */
object T {
    val label = TextStyle(fontFamily = Sans, fontSize = 14.sp, color = C.Muted)
    val action = TextStyle(fontFamily = Sans, fontSize = 14.sp, color = C.Fg)
    val value = TextStyle(fontFamily = Mono, fontSize = 13.sp, color = C.Fg)
    val index = TextStyle(fontFamily = Mono, fontSize = 12.sp, color = C.Faint)
    val heading = TextStyle(fontFamily = Sans, fontSize = 22.sp, fontWeight = FontWeight.Medium, color = C.Fg)
    val state = TextStyle(fontFamily = Sans, fontSize = 24.sp, fontWeight = FontWeight.Medium, color = C.Fg)
    val body = TextStyle(fontFamily = Sans, fontSize = 14.sp, lineHeight = 20.sp, color = C.Muted)
    val hint = TextStyle(fontFamily = Sans, fontSize = 12.sp, lineHeight = 17.sp, color = C.Hint)
    val title = TextStyle(fontFamily = Sans, fontSize = 15.sp, color = C.Muted)
    val chip = TextStyle(fontFamily = Mono, fontSize = 12.sp, color = C.Muted)
}
