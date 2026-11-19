package net.oxog.phonebox.deskpanel

import android.appwidget.AppWidgetManager
import android.appwidget.AppWidgetProvider
import android.content.ComponentName
import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.os.Bundle
import android.view.View
import android.widget.RemoteViews
import org.json.JSONObject
import java.io.File

/** Home-screen widget: what Claude Code is doing, and a pending permission prompt with Deny / Allow. */
class ClaudeWidget : AppWidgetProvider() {
    override fun onUpdate(c: Context, m: AppWidgetManager, ids: IntArray) {
        Widgets.render(c, m, ids, javaClass)
        AlertService.start(c)
    }

    override fun onAppWidgetOptionsChanged(c: Context, m: AppWidgetManager, id: Int, options: Bundle?) =
        Widgets.render(c, m, intArrayOf(id), javaClass)
}

/** Home-screen widget: what the computer is playing, with previous / play-pause / next. */
class MediaWidget : AppWidgetProvider() {
    override fun onUpdate(c: Context, m: AppWidgetManager, ids: IntArray) {
        Widgets.render(c, m, ids, javaClass)
        AlertService.start(c)
    }
}

/**
 * Both widgets draw from the last known content kept in SharedPreferences: AlertService (and the widget buttons'
 * receiver) push new content in, and the widgets are redrawn only when that content changes. A freshly added widget
 * draws the cached content, so it is never blank. Nothing here may throw into the service or the receiver.
 */
object Widgets {
    private const val PREFS = "widgets"
    private const val ART_FILE = "widget_art.png"
    private const val ART_PX = 256 // RemoteViews bitmaps count against the launcher's memory: keep the artwork small
    private const val TALL_DP = 140 // ponytail: height heuristic for "two rows"; launchers report cell sizes differently

    @Synchronized
    fun pushClaude(c: Context, status: String, approval: JSONObject?) {
        val j = JSONObject().put("status", status)
        if (approval != null) j
            .put("id", approval.optString("id"))
            .put("question", approval.optString("kind") == "question")
            .put("tool", approval.optString("tool").ifEmpty { approval.optString("description") })
            .put("project", approval.optString("project").ifEmpty { approval.optString("session") })
            .put("detail", approval.optString("detail").trim().take(400))
        if (save(c, "claude", j)) renderAll(c, ClaudeWidget::class.java)
    }

    /** Answered from a widget or a notification: take the buttons down now rather than at the next poll. */
    @Synchronized
    fun dropApproval(c: Context, id: String) {
        val j = load(c, "claude") ?: return
        if (j.optString("id") == id && save(c, "claude", JSONObject().put("status", j.optString("status"))))
            renderAll(c, ClaudeWidget::class.java)
    }

    /** [media] is /api/state's `media` (null when nothing plays). The artwork is fetched once per artId. */
    @Synchronized
    fun pushMedia(c: Context, link: AgentLink, media: JSONObject?) {
        val j = JSONObject()
        val title = media?.optString("title").orEmpty()
        if (media != null && title.isNotBlank()) j.put("title", title).put("artist", media.optString("artist"))
            .put("app", media.optString("app")).put("playing", media.optBoolean("playing"))
        val artId = if (j.has("title")) media?.optInt("artId") ?: 0 else 0
        val p = prefs(c)
        if (artId != p.getInt("artId", 0)) try {
            val file = File(c.filesDir, ART_FILE)
            if (artId <= 0) { file.delete(); p.edit().putInt("artId", 0).apply() }
            else fetchArt(link, artId)?.let { bmp ->
                file.outputStream().use { bmp.compress(Bitmap.CompressFormat.PNG, 100, it) }
                bmp.recycle()
                p.edit().putInt("artId", artId).apply()
            }
        } catch (_: Exception) {}
        if (artId > 0 && p.getInt("artId", 0) == artId) j.put("art", artId)
        if (save(c, "media", j)) renderAll(c, MediaWidget::class.java)
    }

    fun has(c: Context, cls: Class<*>): Boolean = try {
        AppWidgetManager.getInstance(c).getAppWidgetIds(ComponentName(c, cls)).isNotEmpty()
    } catch (_: Exception) { false }

    @Synchronized
    fun render(c: Context, m: AppWidgetManager, ids: IntArray, cls: Class<*>) {
        for (id in ids) try {
            m.updateAppWidget(id, if (cls == ClaudeWidget::class.java) claude(c, tall(m, id)) else media(c))
        } catch (_: Exception) {}
    }

    private fun renderAll(c: Context, cls: Class<*>) = try {
        val m = AppWidgetManager.getInstance(c)
        render(c, m, m.getAppWidgetIds(ComponentName(c, cls)), cls)
    } catch (_: Exception) {}

    private fun tall(m: AppWidgetManager, id: Int) =
        m.getAppWidgetOptions(id).getInt(AppWidgetManager.OPTION_APPWIDGET_MAX_HEIGHT) >= TALL_DP

    // ---------------------------------------------------------------- views

    private fun claude(c: Context, tall: Boolean): RemoteViews {
        val j = load(c, "claude") ?: JSONObject().put("status", c.getString(R.string.alert_connecting))
        val v = RemoteViews(c.packageName, R.layout.widget_claude)
        v.setOnClickPendingIntent(android.R.id.background, AlertService.openPanel(c))
        val id = j.optString("id")
        val pending = id.isNotEmpty()
        val question = j.optBoolean("question")
        val lines = j.optString("detail").lines().map { it.trim() }.filter { it.isNotEmpty() }
        val sub = if (pending) listOf(j.optString("project"), lines.firstOrNull().orEmpty()).filter { it.isNotBlank() }.joinToString(" · ") else ""
        val rest = lines.drop(1).joinToString("\n")
        v.setTextViewText(R.id.w_title, if (pending) j.optString("tool").ifEmpty { j.optString("status") } else j.optString("status"))
        text(v, R.id.w_sub, sub)
        text(v, R.id.w_detail, if (tall && pending) rest else "")
        v.setViewVisibility(R.id.w_deny, if (pending && !question) View.VISIBLE else View.GONE)
        v.setViewVisibility(R.id.w_allow, if (pending) View.VISIBLE else View.GONE)
        if (pending) {
            v.setOnClickPendingIntent(R.id.w_deny, AlertActionReceiver.decide(c, id, "deny"))
            v.setTextViewText(R.id.w_allow, c.getString(if (question) R.string.alert_open else R.string.alert_allow))
            v.setOnClickPendingIntent(R.id.w_allow, if (question) AlertService.openPanel(c) else AlertActionReceiver.decide(c, id, "allow"))
        }
        return v
    }

    private fun media(c: Context): RemoteViews {
        val j = load(c, "media") ?: JSONObject()
        val v = RemoteViews(c.packageName, R.layout.widget_media)
        v.setOnClickPendingIntent(android.R.id.background, AlertService.openPanel(c))
        v.setTextViewText(R.id.m_title, j.optString("title").ifEmpty { c.getString(R.string.widget_nothing_playing) })
        text(v, R.id.m_sub, listOf(j.optString("artist"), j.optString("app")).filter { it.isNotBlank() }.joinToString(" · "))
        v.setImageViewResource(R.id.m_play, if (j.optBoolean("playing")) R.drawable.ic_w_pause else R.drawable.ic_w_play)
        val art = if (j.optInt("art") > 0) try { BitmapFactory.decodeFile(File(c.filesDir, ART_FILE).path) } catch (_: Throwable) { null } else null
        if (art != null) { v.setImageViewBitmap(R.id.m_art, art); v.setViewPadding(R.id.m_art, 0, 0, 0, 0) }
        v.setOnClickPendingIntent(R.id.m_prev, AlertActionReceiver.media(c, "prev"))
        v.setOnClickPendingIntent(R.id.m_play, AlertActionReceiver.media(c, "playpause"))
        v.setOnClickPendingIntent(R.id.m_next, AlertActionReceiver.media(c, "next"))
        return v
    }

    private fun text(v: RemoteViews, id: Int, s: String) {
        v.setTextViewText(id, s)
        v.setViewVisibility(id, if (s.isEmpty()) View.GONE else View.VISIBLE)
    }

    // ---------------------------------------------------------------- cache and artwork

    private fun prefs(c: Context) = c.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    private fun load(c: Context, key: String): JSONObject? = try { prefs(c).getString(key, null)?.let { JSONObject(it) } } catch (_: Exception) { null }

    /** Stores the content; true when it differs from what the widgets show now. */
    private fun save(c: Context, key: String, j: JSONObject): Boolean {
        val s = j.toString()
        val p = prefs(c)
        if (p.getString(key, null) == s) return false
        p.edit().putString(key, s).apply()
        return true
    }

    private fun fetchArt(link: AgentLink, id: Int): Bitmap? = try {
        val r = link.request("GET", "/api/art?id=$id")
        val bytes = r.body.use { it.readBytes() }
        if (r.code == 200) decode(bytes) else null
    } catch (_: Exception) { null } catch (_: OutOfMemoryError) { null }

    private fun decode(b: ByteArray): Bitmap? {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeByteArray(b, 0, b.size, bounds)
        val side = maxOf(bounds.outWidth, bounds.outHeight)
        if (side <= 0) return null
        var sample = 1
        while (side / (sample * 2) >= ART_PX) sample *= 2
        val bmp = BitmapFactory.decodeByteArray(b, 0, b.size, BitmapFactory.Options().apply { inSampleSize = sample }) ?: return null
        val k = ART_PX.toFloat() / maxOf(bmp.width, bmp.height)
        if (k >= 1f) return bmp
        val out = Bitmap.createScaledBitmap(bmp, (bmp.width * k).toInt().coerceAtLeast(1), (bmp.height * k).toInt().coerceAtLeast(1), true)
        if (out !== bmp) bmp.recycle()
        return out
    }
}
