package ru.lkxa.wgswitch.ui

import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.runtime.withFrameNanos
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.scale
import androidx.compose.ui.graphics.drawscope.translate
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.vector.PathParser
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.launch
import ru.lkxa.wgswitch.vpn.State
import ru.lkxa.wgswitch.vpn.Status
import kotlin.math.PI
import kotlin.math.cos
import kotlin.math.max
import kotlin.math.sin

// те же константы, что в MainWindow.xaml.cs
private const val SPIN_PERIOD = 1.4
private const val SPIN_MIN_DEG = 18.0
private const val SPIN_MAX_DEG = 150.0
private const val RING_DRAW_MS = 900
private const val RING_ERASE_MS = 450

private val powerIcon = PathParser().parsePathString("M12 2v10 M18.4 6.6a9 9 0 1 1-12.77.04").toPath()

/**
 * Кнопка: тёмный диск. Подключилось — кольцо дорисовывается от верхней точки по кругу, кнопка «вдыхает»,
 * следом кольцо-пульс. Ожидание — короткая дуга, которая «дышит» на ходу. Отключение — кольцо желтеет,
 * стирается, и из его конца появляется дуга ожидания.
 */
@Composable
fun PowerButton(status: Status, pendingOff: Boolean, heartbeat: SharedFlow<Unit>, haptics: Haptics, onClick: () -> Unit) {
    val disconnecting = status.disconnecting || pendingOff
    val filled = status.up
    val spinning = status.state == State.Connecting || disconnecting
    val ring = filled && !disconnecting
    val accent = when {
        disconnecting -> C.Amber
        status.state == State.Connected -> C.Green
        status.state == State.NoResponse -> C.Red
        status.state == State.Connecting -> C.Amber
        else -> C.Muted
    }

    val iconColor by animateColorAsState(accent, tween(300), label = "icon")
    val ringColor by animateColorAsState(if (disconnecting) C.Amber else if (status.state == State.NoResponse) C.Red else C.Green, tween(300), label = "ring")
    val fillColor by animateColorAsState(if (status.state == State.NoResponse) C.Red else C.Green, tween(300), label = "fill")
    val tint by animateFloatAsState(if (filled) 0.08f else 0f, tween(700, easing = Expo), label = "tint")

    val ringP = remember { Animatable(if (ring) 1f else 0f) }
    val breathe = remember { Animatable(1f) }
    val rippleScale = remember { Animatable(1f) }
    val rippleAlpha = remember { Animatable(0f) }
    val spinAlpha = remember { Animatable(if (spinning) 1f else 0f) }
    var spinT by remember { mutableFloatStateOf(0f) }
    var lastPulse by remember { mutableFloatStateOf(0f) }
    val scope = rememberCoroutineScope()
    val rippleColor = if (status.state == State.NoResponse) C.Red else C.Green
    val currentState by rememberUpdatedState(status.state)

    fun pulse(delayMs: Long) = scope.launch {
        lastPulse = System.currentTimeMillis().toFloat()
        delay(delayMs)
        rippleScale.snapTo(1f)
        rippleAlpha.snapTo(0.35f)
        launch { rippleAlpha.animateTo(0f, tween(1400, easing = LinearEasing)) }
        rippleScale.animateTo(1.12f, tween(1400, easing = Expo))
    }

    LaunchedEffect(ring) {
        if (ring == (ringP.value > 0.5f) && ringP.value in listOf(0f, 1f)) return@LaunchedEffect // первый показ — без анимации
        if (ring) {
            launch { ringP.animateTo(1f, tween(RING_DRAW_MS, easing = Expo)) }
            launch {
                delay(250)
                breathe.animateTo(1.025f, tween(200, easing = SineInOut))
                breathe.animateTo(1f, tween(200, easing = SineInOut))
            }
            pulse(600)
        } else {
            ringP.animateTo(0f, tween(RING_ERASE_MS, easing = EaseInCubic))
        }
    }

    LaunchedEffect(spinning) {
        if (!spinning) {
            spinAlpha.animateTo(0f, tween(250))
            return@LaunchedEffect
        }
        // при отключении дуга появляется там, где кольцо досчиталось до нуля: у верхней точки
        val erasing = ringP.value > 0.5f
        val startDelay = if (erasing) (RING_ERASE_MS - 60).toLong() else 0L
        launch {
            delay(startDelay)
            spinAlpha.animateTo(1f, tween(if (erasing) 150 else 250))
        }
        val start = withFrameNanos { it } + startDelay * 1_000_000
        var lastTurn = 0L
        while (true) {
            withFrameNanos { now ->
                val e = max(0.0, (now - start) / 1e9)
                spinT = e.toFloat()
                val turn = (e / SPIN_PERIOD).toLong()
                if (turn != lastTurn) {
                    lastTurn = turn
                    haptics.tick() // дуга прошла верхнюю точку
                }
            }
        }
    }

    // сервер ответил на пинг — тонкое расходящееся кольцо, не чаще раза в 6 с
    LaunchedEffect(heartbeat) {
        heartbeat.collect {
            if (currentState == State.Connected && System.currentTimeMillis() - lastPulse > 6000) pulse(0)
        }
    }

    val source = remember { MutableInteractionSource() }
    val pressed by source.collectIsPressedAsState()
    val pressScale by animateFloatAsState(if (pressed) 0.96f else 1f, tween(if (pressed) 120 else 400, easing = Expo), label = "press")

    Box(Modifier.size(208.dp), contentAlignment = Alignment.Center) {
        // кольцо-пульс за кнопкой
        Canvas(Modifier.size(184.dp)) {
            val r = size.minDimension / 2 * rippleScale.value
            drawCircle(rippleColor.copy(alpha = rippleAlpha.value), radius = r - 0.5.dp.toPx(), style = Stroke(1.dp.toPx()))
        }
        Canvas(
            Modifier.size(184.dp)
                .graphicsLayer { scaleX = breathe.value * pressScale; scaleY = breathe.value * pressScale }
                .clip(CircleShape)
                .clickable(source, null) {
                    haptics.press()
                    onClick()
                },
        ) {
            val r = size.minDimension / 2
            drawCircle(C.Surface, r)
            drawCircle(C.Line, r - 0.5.dp.toPx(), style = Stroke(1.dp.toPx()))
            if (tint > 0f) drawCircle(fillColor.copy(alpha = tint), r)

            val w = 2.dp.toPx()
            val inset = w / 2 + 0.5.dp.toPx()
            val arcSize = Size(size.width - inset * 2, size.height - inset * 2)
            if (ringP.value > 0f)
                drawArc(ringColor, -90f, 360f * ringP.value, false, Offset(inset, inset), arcSize, style = Stroke(w))
            if (spinAlpha.value > 0f) drawSpinner(spinT.toDouble(), spinAlpha.value, w, inset, arcSize)

            // значок питания 24×24 → 46 dp, обводка не масштабируется
            val icon = 46.dp.toPx()
            val k = icon / 24f
            translate((size.width - icon) / 2, (size.height - icon) / 2) {
                scale(k, k, pivot = Offset.Zero) {
                    drawPath(powerIcon, iconColor, style = Stroke(1.9.dp.toPx() / k, cap = StrokeCap.Round))
                }
            }
        }
    }
}

/**
 * Дуга ожидания: середина обходит круг за SPIN_PERIOD с лёгким разгоном и торможением,
 * длина меняется по косинусу — минимум у верхней точки, максимум внизу.
 */
private fun DrawScope.drawSpinner(seconds: Double, alpha: Float, w: Float, inset: Float, arcSize: Size) {
    val t = seconds % SPIN_PERIOD / SPIN_PERIOD
    val eased = t - sin(2 * PI * t) / (2 * PI) * 0.35
    val span = SPIN_MIN_DEG + (SPIN_MAX_DEG - SPIN_MIN_DEG) * (1 - cos(2 * PI * t)) / 2
    val center = -90 + 360 * eased
    drawArc(C.Amber.copy(alpha = alpha), (center - span / 2).toFloat(), span.toFloat(), false, Offset(inset, inset), arcSize, style = Stroke(w, cap = StrokeCap.Round))
}

/** Точка состояния: со свечением; пока идёт ожидание — от неё расходится кольцо. */
@Composable
fun StateDot(color: Color, pinging: Boolean) {
    val c by animateColorAsState(color, tween(300), label = "dot")
    val wave = rememberInfiniteTransition(label = "dotPing")
    val p by wave.animateFloat(0f, 1f, infiniteRepeatable(tween(1000, easing = androidx.compose.animation.core.EaseOutCubic), RepeatMode.Restart), label = "p")
    Canvas(Modifier.size(9.dp)) {
        val r = size.minDimension / 2
        if (c != C.Muted) drawCircle(Brush.radialGradient(listOf(c.copy(alpha = 0.45f), Color.Transparent), center, r * 3.2f), r * 3.2f)
        if (pinging) drawCircle(c.copy(alpha = 0.7f * (1 - p)), r * (1 + 1.4f * p))
        drawCircle(c, r)
    }
}
