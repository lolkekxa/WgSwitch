package ru.lkxa.wgswitch.ui

import android.content.Context
import android.os.Build
import android.os.VibrationEffect
import android.os.Vibrator
import android.os.VibratorManager

/**
 * Тактильные сигналы. На Android 11+ — «примитивы» (короткие точные импульсы, как в iPhone),
 * ниже — готовые эффекты щелчка, совсем старые — короткая вибрация.
 */
class Haptics(context: Context) {
    var enabled = true

    private val vibrator: Vibrator? =
        if (Build.VERSION.SDK_INT >= 31) (context.getSystemService(Context.VIBRATOR_MANAGER_SERVICE) as? VibratorManager)?.defaultVibrator
        else @Suppress("DEPRECATION") (context.getSystemService(Context.VIBRATOR_SERVICE) as? Vibrator)

    /** Нажатие на кнопку питания. */
    fun press() = play(listOf(P.CLICK to 0.7f), fallback = VibrationEffect.EFFECT_CLICK)

    /** Каждый оборот дуги ожидания — едва заметный тик у верхней точки. */
    fun tick() = play(listOf(P.LOW_TICK to 0.6f), fallback = VibrationEffect.EFFECT_TICK, quietFallback = true)

    /** Подключилось: нарастание и мягкий щелчок — «цепь замкнулась». */
    fun success() = play(listOf(P.QUICK_RISE to 0.5f, P.CLICK to 1f), gapsMs = listOf(0, 60), fallback = VibrationEffect.EFFECT_DOUBLE_CLICK)

    /** Сервер не отвечает: глухой тяжёлый удар. */
    fun error() = play(listOf(P.THUD to 1f, P.CLICK to 0.4f), gapsMs = listOf(0, 120), fallback = VibrationEffect.EFFECT_HEAVY_CLICK)

    /** Отключилось: короткий спад. */
    fun off() = play(listOf(P.QUICK_FALL to 0.6f), fallback = VibrationEffect.EFFECT_TICK)

    /** Тумблеры, выбор сервера, переходы. */
    fun light() = play(listOf(P.TICK to 0.5f), fallback = VibrationEffect.EFFECT_TICK, quietFallback = true)

    private object P {
        const val CLICK = VibrationEffect.Composition.PRIMITIVE_CLICK
        const val TICK = VibrationEffect.Composition.PRIMITIVE_TICK
        const val QUICK_RISE = VibrationEffect.Composition.PRIMITIVE_QUICK_RISE
        const val QUICK_FALL = VibrationEffect.Composition.PRIMITIVE_QUICK_FALL
        // с Android 12; на 11 их нет — areAllPrimitivesSupported вернёт false, и сработает запасной эффект
        const val THUD = VibrationEffect.Composition.PRIMITIVE_THUD
        const val LOW_TICK = VibrationEffect.Composition.PRIMITIVE_LOW_TICK
    }

    private fun play(primitives: List<Pair<Int, Float>>, gapsMs: List<Int> = emptyList(), fallback: Int, quietFallback: Boolean = false) {
        val v = vibrator ?: return
        if (!enabled || !v.hasVibrator()) return
        try {
            if (Build.VERSION.SDK_INT >= 30 && v.areAllPrimitivesSupported(*primitives.map { it.first }.toIntArray())) {
                val c = VibrationEffect.startComposition()
                primitives.forEachIndexed { i, (p, scale) -> c.addPrimitive(p, scale, gapsMs.getOrElse(i) { 0 }) }
                v.vibrate(c.compose())
            } else if (Build.VERSION.SDK_INT >= 29) {
                v.vibrate(VibrationEffect.createPredefined(fallback))
            } else if (!quietFallback) {
                v.vibrate(VibrationEffect.createOneShot(12, VibrationEffect.DEFAULT_AMPLITUDE))
            }
        } catch (_: Exception) {
            // вибрация — украшение, не повод падать
        }
    }
}
