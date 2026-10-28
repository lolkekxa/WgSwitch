package ru.lkxa.wgswitch.ui

import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.animateDpAsState
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicText
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.draw.scale
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.scale
import androidx.compose.ui.graphics.drawscope.translate
import androidx.compose.ui.graphics.vector.PathParser
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.launch

/** Нажатие без «волны» Material: строка просто светлеет, как наведение в WPF. */
@Composable
fun Modifier.press(onClick: () -> Unit, enabled: Boolean = true, pressedAlpha: Float = 0.6f): Modifier {
    val source = remember { MutableInteractionSource() }
    val pressed by source.collectIsPressedAsState()
    val a by animateFloatAsState(if (pressed) pressedAlpha else 1f, tween(if (pressed) 60 else 250), label = "press")
    return this
        .alpha(a)
        .clickable(interactionSource = source, indication = null, enabled = enabled, onClick = onClick)
}

@Composable
fun Txt(text: String, style: TextStyle, modifier: Modifier = Modifier, color: Color = Color.Unspecified, maxLines: Int = Int.MAX_VALUE, align: TextAlign? = null) =
    BasicText(
        text, modifier,
        style = style.let { s -> if (color != Color.Unspecified) s.copy(color = color) else s }.let { s -> if (align != null) s.copy(textAlign = align) else s },
        maxLines = maxLines, overflow = TextOverflow.Ellipsis,
    )

/** Подпись секции: «(01) Сервер», справа — необязательные действия. */
@Composable
fun Section(index: String, label: String, modifier: Modifier = Modifier, trailing: @Composable RowScope.() -> Unit = {}) {
    Row(modifier.fillMaxWidth().padding(horizontal = 4.dp), verticalAlignment = Alignment.CenterVertically) {
        Txt(index, T.index)
        Spacer(Modifier.width(10.dp))
        Txt(label, T.label, Modifier.weight(1f))
        trailing()
    }
}

/** Карточка: тонкая рамка, скругление 16 — та же, что карточка статуса на главной. */
@Composable
fun Card(modifier: Modifier = Modifier, content: @Composable ColumnScope.() -> Unit) =
    Column(
        modifier.padding(top = 8.dp).fillMaxWidth()
            .border(1.dp, C.Line, RoundedCornerShape(20.dp))
            .padding(horizontal = 18.dp, vertical = 4.dp),
        content = content,
    )

/** Строка «подпись слева — значение справа». */
@Composable
fun InfoRow(label: String, value: String, valueColor: Color = C.Fg, onClick: (() -> Unit)? = null) {
    Row(
        Modifier.fillMaxWidth().height(40.dp).then(if (onClick != null) Modifier.press(onClick) else Modifier),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Txt(label, T.label, Modifier.weight(1f))
        Txt(value, T.value, color = valueColor, maxLines = 1)
    }
}

/** Строка-действие: подпись, справа значение и ›. */
@Composable
fun ActionRow(label: String, value: String? = null, labelColor: Color = C.Fg, leading: @Composable (() -> Unit)? = null, onClick: () -> Unit) {
    Row(Modifier.fillMaxWidth().height(48.dp).press(onClick), verticalAlignment = Alignment.CenterVertically) {
        leading?.invoke()
        Txt(label, T.action, Modifier.weight(1f), color = labelColor)
        if (value != null) Txt(value, T.value, Modifier.widthIn(max = 180.dp), color = C.Muted, maxLines = 1)
        Chevron(Modifier.padding(start = 10.dp))
    }
}

@Composable
fun Chevron(modifier: Modifier = Modifier, color: Color = C.Muted) =
    Canvas(modifier.size(7.dp, 12.dp)) {
        val p = Path().apply {
            moveTo(size.width * 0.15f, size.height * 0.1f)
            lineTo(size.width * 0.85f, size.height * 0.5f)
            lineTo(size.width * 0.15f, size.height * 0.9f)
        }
        drawPath(p, color, style = Stroke(1.6.dp.toPx(), cap = StrokeCap.Round, join = StrokeJoin.Round))
    }

/** Тумблер: выключен — серый контур, включён — светлый трек и тёмная ручка; ручка едет по ease-out-expo. */
@Composable
fun ToggleRow(label: String, checked: Boolean, onChange: (Boolean) -> Unit, hint: String? = null, enabled: Boolean = true) {
    Row(
        Modifier.fillMaxWidth().heightIn(min = 48.dp).alpha(if (enabled) 1f else 0.35f).press({ onChange(!checked) }, enabled),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f).padding(vertical = 8.dp)) {
            Txt(label, T.label, color = if (checked) C.Fg else C.Muted)
            if (hint != null) Txt(hint, T.hint, Modifier.padding(top = 2.dp))
        }
        Spacer(Modifier.width(12.dp))
        Switch(checked)
    }
}

@Composable
fun Switch(checked: Boolean) {
    val x by animateDpAsState(if (checked) 16.dp else 0.dp, tween(350, easing = Expo), label = "knob")
    val track by animateColorAsState(if (checked) C.Fg else Color.Transparent, tween(200), label = "track")
    val border by animateColorAsState(if (checked) C.Fg else C.Faint, tween(200), label = "border")
    val knob by animateColorAsState(if (checked) C.Bg else C.Muted, tween(200), label = "knobColor")
    Box(
        Modifier.size(38.dp, 22.dp).clip(CircleShape).background(track).border(1.dp, border, CircleShape),
        contentAlignment = Alignment.CenterStart,
    ) {
        Box(Modifier.offset(x = 3.dp + x).size(14.dp).clip(CircleShape).background(knob))
    }
}

/**
 * Основная капсула: светлая, текст тёмный. При нажатии снизу «наливается» серым — как наведение на ПК и на сайте.
 */
@Composable
fun Pill(text: String, onClick: () -> Unit, modifier: Modifier = Modifier, enabled: Boolean = true) {
    val source = remember { MutableInteractionSource() }
    val pressed by source.collectIsPressedAsState()
    val sweep by animateFloatAsState(if (pressed) 1f else 0f, tween(500, easing = Expo), label = "sweep")
    val scale by animateFloatAsState(if (pressed) 0.97f else 1f, tween(200, easing = Expo), label = "scale")
    Box(
        modifier.height(46.dp).scale(scale).alpha(if (enabled) 1f else 0.4f).clip(RoundedCornerShape(23.dp)).background(C.Fg)
            .clickable(source, null, enabled = enabled, onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        Canvas(Modifier.matchParentSize()) {
            val h = size.height * sweep
            drawRect(Color(0xFFD4D4D4), topLeft = androidx.compose.ui.geometry.Offset(0f, size.height - h), size = size.copy(height = h))
        }
        Txt(text, T.action.copy(fontWeight = FontWeight.Medium, color = C.Bg), Modifier.padding(horizontal = 24.dp))
    }
}

/** Текстовая кнопка: «Назад», «Отмена», «Скопировать». */
@Composable
fun TextButton(text: String, onClick: () -> Unit, modifier: Modifier = Modifier, color: Color = C.Muted) =
    Box(modifier.heightIn(min = 46.dp).press(onClick), contentAlignment = Alignment.Center) {
        Txt(text, T.label, color = color)
    }

/** Капсула сервера: точка состояния + имя; выбранная — с заливкой. */
@Composable
fun Chip(name: String, dot: Color, selected: Boolean, onClick: () -> Unit) {
    val bg by animateColorAsState(if (selected) C.Surface else Color.Transparent, tween(250), label = "chipBg")
    Row(
        Modifier.padding(horizontal = 3.dp).height(32.dp).clip(CircleShape).background(bg)
            .border(1.dp, if (selected) C.Faint else C.Line, CircleShape).press(onClick)
            .padding(start = 12.dp, end = 14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(Modifier.size(6.dp).clip(CircleShape).background(dot))
        Spacer(Modifier.width(8.dp))
        Txt(name, T.chip, color = if (selected) C.Fg else C.Muted)
    }
}

/** Значение, которое правится прямо в строке: моно, справа, без рамок; в фокусе — лёгкая подложка. */
@Composable
fun InlineField(
    label: String,
    value: String,
    onValueChange: (String) -> Unit,
    placeholder: String = "",
    password: Boolean = false,
    keyboard: KeyboardType = KeyboardType.Text,
    onDone: () -> Unit = {},
    onFocusLost: () -> Unit = {},
    fieldWidth: Dp = 190.dp,
) {
    var focused by remember { mutableStateOf(false) }
    val bg by animateColorAsState(if (focused) Color(0xFF191919) else if (password) C.Surface else Color.Transparent, tween(200), label = "fieldBg")
    Row(Modifier.fillMaxWidth().height(48.dp), verticalAlignment = Alignment.CenterVertically) {
        Txt(label, T.label, Modifier.weight(1f))
        Box(Modifier.width(fieldWidth).offset(x = 10.dp).clip(RoundedCornerShape(8.dp)).background(bg).padding(horizontal = 10.dp, vertical = 8.dp)) {
            if (value.isEmpty()) Txt(placeholder, T.value, Modifier.fillMaxWidth(), color = C.Faint, align = TextAlign.End, maxLines = 1)
            BasicTextField(
                value, onValueChange,
                Modifier.fillMaxWidth().onFocusChanged {
                    if (focused && !it.isFocused) onFocusLost()
                    focused = it.isFocused
                },
                textStyle = T.value.copy(textAlign = if (password) TextAlign.Start else TextAlign.End),
                singleLine = true,
                cursorBrush = SolidColor(C.Fg),
                visualTransformation = if (password) PasswordVisualTransformation('•') else VisualTransformation.None,
                keyboardOptions = KeyboardOptions(
                    keyboardType = if (password) KeyboardType.Password else keyboard,
                    imeAction = ImeAction.Done, autoCorrectEnabled = false,
                ),
                keyboardActions = KeyboardActions(onDone = { onDone() }),
            )
        }
    }
}

/** «Сделано в LKXA»: бейдж студии; при нажатии ✦ поворачивается, как на сайте. */
@Composable
fun MadeBy(onClick: () -> Unit) {
    val turn = remember { Animatable(0f) }
    val scope = androidx.compose.runtime.rememberCoroutineScope()
    Row(
        Modifier.height(32.dp).clip(CircleShape).background(C.Bg).border(1.dp, Color(0x1FFFFFFF), CircleShape)
            .press({
                scope.launch { turn.animateTo(turn.value + 90f, tween(700, easing = Expo)) }
                onClick()
            })
            .padding(start = 12.dp, end = 14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        val star = remember { PathParser().parsePathString("M12 1Q12 12 23 12Q12 12 12 23Q12 12 1 12Q12 12 12 1Z").toPath() }
        Canvas(Modifier.size(11.dp).rotate(turn.value)) {
            scale(size.width / 24f, size.height / 24f, pivot = androidx.compose.ui.geometry.Offset.Zero) { drawPath(star, C.Violet) }
        }
        Spacer(Modifier.width(8.dp))
        BasicText(
            buildAnnotatedString {
                append("Сделано в ")
                withStyle(SpanStyle(color = C.Fg, fontWeight = FontWeight.Medium)) { append("LKXA") }
            },
            style = T.label.copy(fontSize = 12.sp),
        )
    }
}

/** Контурная иконка в стиле lucide: путь 24×24, обводка 1.5. */
@Composable
fun Glyph(d: String, color: Color, modifier: Modifier = Modifier, size: Dp = 18.dp, stroke: Dp = 1.6.dp) {
    val path = remember(d) { PathParser().parsePathString(d).toPath() }
    Canvas(modifier.size(size)) {
        val k = this.size.width / 24f
        scale(k, k, pivot = androidx.compose.ui.geometry.Offset.Zero) {
            drawPath(path, color, style = Stroke(stroke.toPx() / k, cap = StrokeCap.Round, join = StrokeJoin.Round))
        }
    }
}

/** Знак «тоннель»: арка и точка, как у иконки приложения. */
@Composable
fun Mark(color: Color, modifier: Modifier = Modifier) {
    val arch = remember { PathParser().parsePathString("M 15 50 L 15 32 A 17 17 0 0 1 49 32 L 49 50").toPath() }
    Box(
        modifier.size(22.dp).clip(RoundedCornerShape(5.dp)).background(C.Surface).border(1.dp, C.Line, RoundedCornerShape(5.dp)),
        contentAlignment = Alignment.Center,
    ) {
        Canvas(Modifier.size(14.dp)) {
            val k = size.width / 64f
            scale(k, k, pivot = androidx.compose.ui.geometry.Offset.Zero) {
                drawPath(arch, color, style = Stroke(7f, cap = StrokeCap.Round, join = StrokeJoin.Round))
                drawCircle(color, radius = 6.5f, center = androidx.compose.ui.geometry.Offset(32f, 38f))
            }
        }
    }
}

/** Новый текст въезжает снизу с проявлением — как RollText на сайте. */
@Composable
fun Rolling(text: String, style: TextStyle, modifier: Modifier = Modifier, color: Color = Color.Unspecified) {
    val shift = remember { Animatable(0f) }
    val alpha = remember { Animatable(1f) }
    var shown by remember { mutableStateOf(text) }
    LaunchedEffect(text) {
        if (text == shown) return@LaunchedEffect
        shown = text
        shift.snapTo(10f)
        alpha.snapTo(0f)
        launch { alpha.animateTo(1f, tween(350)) }
        shift.animateTo(0f, tween(600, easing = Expo))
    }
    Txt(shown, style, modifier.offset(y = shift.value.dp).alpha(alpha.value), color = color, maxLines = 1)
}

