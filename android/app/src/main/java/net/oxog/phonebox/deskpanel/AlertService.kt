package net.oxog.phonebox.deskpanel

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.RemoteInput
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import org.json.JSONObject
import java.net.URLEncoder

/**
 * Keeps an eye on the computer while the panel is not on screen (another app in front, or the screen off) and turns
 * what Claude Code / Codex does into Android notifications:
 *  - an ongoing, quiet one that says what the sessions are doing (the foreground-service notification);
 *  - a permission prompt, with Allow / Deny that answer it on the computer;
 *  - a question from Claude, and a session that waits for input;
 *  - a session that finished, with an inline Reply that sends the next prompt to it (POST /api/sessions/prompt).
 * While the panel is on screen it stays quiet: the panel shows all of this itself.
 * It also feeds the home-screen widgets (Widgets.kt): the Claude status / prompt, and what the computer is playing.
 */
class AlertService : Service() {

    private lateinit var link: AgentLink
    private lateinit var nm: NotificationManager
    @Volatile private var running = false
    private var worker: Thread? = null

    private val shownApprovals = mutableSetOf<String>()
    private val lastStatus = mutableMapOf<String, String>()
    private var lastOngoing = ""
    private var tick = 0

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        link = AgentLink(this)
        nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        createChannels(this)
        val n = ongoing(getString(R.string.alert_connecting))
        if (Build.VERSION.SDK_INT >= 34) startForeground(ONGOING_ID, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        else startForeground(ONGOING_ID, n)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (!running) {
            running = true
            worker = Thread({ loop() }, "alert-watch").apply { isDaemon = true; start() }
        }
        return START_STICKY
    }

    override fun onDestroy() {
        running = false
        worker?.interrupt()
        link.close()
        super.onDestroy()
    }

    private fun loop() {
        while (running) {
            try { poll() } catch (_: InterruptedException) { return } catch (_: Exception) {}
            try { Thread.sleep(2500) } catch (_: InterruptedException) { return }
        }
    }

    private fun get(path: String): JSONObject? = try {
        val r = link.request("GET", path)
        r.body.use { b -> if (r.code == 200) JSONObject(b.bufferedReader().readText()) else null }
    } catch (_: Exception) { null }

    private fun poll() {
        val work = get("/api/work")
        val approvals = get("/api/approvals")
        val pc = link.pairedPc() ?: "PC"
        val away = !MainActivity.onScreen

        // ---- the ongoing line: what the sessions are doing right now
        val sessions = work?.optJSONArray("sessions")
        var working = 0; var waiting = 0; var firstBusy = ""
        if (sessions != null) for (i in 0 until sessions.length()) {
            val s = sessions.getJSONObject(i)
            when (s.optString("status")) {
                "busy", "shell" -> { working++; if (firstBusy.isEmpty()) firstBusy = s.optString("project").ifEmpty { s.optString("name") } }
                "waiting" -> waiting++
            }
        }
        val line = when {
            work == null -> getString(R.string.alert_offline, pc)
            waiting > 0 -> getString(R.string.alert_waiting_count, waiting)
            working == 1 -> getString(R.string.alert_working_one, firstBusy)
            working > 1 -> getString(R.string.alert_working_many, working)
            else -> getString(R.string.alert_connected, pc)
        }
        if (line != lastOngoing) { lastOngoing = line; nm.notify(ONGOING_ID, ongoing(line)) }

        // ---- permission prompts and questions
        val items = approvals?.optJSONArray("items")
        val pending = mutableSetOf<String>()
        if (items != null) for (i in 0 until items.length()) {
            val a = items.getJSONObject(i)
            val id = a.optString("id")
            if (id.isEmpty()) continue
            pending += id
            if (away && id !in shownApprovals) { shownApprovals += id; nm.notify(approvalId(id), approval(a)) }
        }
        // answered on the computer or on the panel, timed out, or the panel is back on screen: take them down
        for (id in shownApprovals.toList()) if (id !in pending || !away) { nm.cancel(approvalId(id)); if (id !in pending) shownApprovals -= id }
        if (!away) shownApprovals.retainAll(emptySet())

        // ---- session changes: finished, waiting for input
        if (sessions != null) {
            val seen = mutableSetOf<String>()
            for (i in 0 until sessions.length()) {
                val s = sessions.getJSONObject(i)
                val id = s.optString("id"); seen += id
                val now = s.optString("status")
                val before = lastStatus.put(id, now)
                if (!away || before == null || before == now) continue
                val project = s.optString("project").ifEmpty { s.optString("name") }
                val tool = if (s.optString("tool") == "codex") "Codex" else "Claude"
                if (before in setOf("busy", "shell") && now == "idle") nm.notify(sessionId(id), finished(id, tool, project))
                else if (now == "waiting" && pending.isEmpty()) nm.notify(sessionId(id), needsInput(tool, project, s.optString("waitingFor")))
            }
            lastStatus.keys.retainAll(seen)
        }

        // ---- home-screen widgets (redrawn only when their content changes); the media one every other poll (~5 s)
        try {
            Widgets.pushClaude(this, if (work != null && waiting == 0 && working == 0) getString(R.string.widget_all_quiet) else line,
                items?.optJSONObject(0))
            if (tick++ % 2 == 0 && Widgets.has(this, MediaWidget::class.java))
                Widgets.pushMedia(this, link, get("/api/state")?.optJSONObject("media"))
        } catch (_: Exception) {}
    }

    // ---------------------------------------------------------------- notifications

    private fun openPanel(): PendingIntent = openPanel(this)

    private fun builder(channel: String): Notification.Builder =
        (if (Build.VERSION.SDK_INT >= 26) Notification.Builder(this, channel) else @Suppress("DEPRECATION") Notification.Builder(this))
            .setSmallIcon(R.drawable.ic_tile)
            .setColor(0xFFD97757.toInt())
            .setContentIntent(openPanel())

    private fun ongoing(text: String): Notification = builder(CH_STATUS)
        .setContentTitle(getString(R.string.alert_status_title))
        .setContentText(text)
        .setOngoing(true)
        .setShowWhen(false)
        .apply { if (Build.VERSION.SDK_INT < 26) @Suppress("DEPRECATION") setPriority(Notification.PRIORITY_MIN) }
        .build()

    private fun approval(a: JSONObject): Notification {
        val id = a.optString("id")
        val question = a.optString("kind") == "question"
        val source = a.optString("source").ifEmpty { "Claude Code" }
        val project = a.optString("project").ifEmpty { a.optString("session") }
        val detail = a.optString("detail")
        val text = listOf(a.optString("description"), detail).filter { it.isNotBlank() }.joinToString("\n")
        val b = builder(CH_PROMPTS)
            .setContentTitle(listOf(source, project).filter { it.isNotBlank() }.joinToString(" · "))
            .setContentText(a.optString("tool"))
            .setStyle(Notification.BigTextStyle().bigText(a.optString("tool") + if (text.isNotBlank()) "\n" + text.take(600) else ""))
            .setAutoCancel(true)
            .setCategory(Notification.CATEGORY_REMINDER)
            .apply { if (Build.VERSION.SDK_INT < 26) @Suppress("DEPRECATION") setPriority(Notification.PRIORITY_HIGH) }
            .setDefaults(Notification.DEFAULT_ALL)
        if (question) {
            b.addAction(action(R.string.alert_got_it, AlertActionReceiver.decide(this, id, "skip")))
            b.addAction(action(R.string.alert_open, openPanel()))
        } else {
            b.addAction(action(R.string.alert_deny, AlertActionReceiver.decide(this, id, "deny")))
            b.addAction(action(R.string.alert_allow, AlertActionReceiver.decide(this, id, "allow")))
        }
        return b.build()
    }

    private fun finished(sessionKey: String, tool: String, project: String): Notification {
        val reply = RemoteInput.Builder(AlertActionReceiver.KEY_REPLY).setLabel(getString(R.string.alert_reply_hint)).build()
        val act = Notification.Action.Builder(null, getString(R.string.alert_reply), AlertActionReceiver.reply(this, sessionKey))
            .addRemoteInput(reply).build()
        return builder(CH_SESSIONS)
            .setContentTitle(getString(R.string.alert_finished, tool))
            .setContentText(project)
            .setAutoCancel(true)
            .addAction(act)
            .build()
    }

    private fun needsInput(tool: String, project: String, what: String): Notification = builder(CH_PROMPTS)
        .setContentTitle(getString(R.string.alert_needs_input, tool))
        .setContentText(listOf(project, what).filter { it.isNotBlank() }.joinToString(" · "))
        .setAutoCancel(true)
        .apply { if (Build.VERSION.SDK_INT < 26) @Suppress("DEPRECATION") setPriority(Notification.PRIORITY_HIGH) }
        .setDefaults(Notification.DEFAULT_ALL)
        .build()

    private fun action(label: Int, pi: PendingIntent) = Notification.Action.Builder(null, getString(label), pi).build()

    companion object {
        const val CH_STATUS = "status"
        const val CH_PROMPTS = "prompts"
        const val CH_SESSIONS = "sessions"
        const val ONGOING_ID = 1

        fun approvalId(id: String) = 1000 + (id.hashCode() and 0x7ffffff)
        fun sessionId(id: String) = 200_000_000 + (id.hashCode() and 0x7ffffff)
        fun immutable() = PendingIntent.FLAG_UPDATE_CURRENT or (if (Build.VERSION.SDK_INT >= 23) PendingIntent.FLAG_IMMUTABLE else 0)
        fun openPanel(c: Context): PendingIntent = PendingIntent.getActivity(c, 0,
            Intent(c, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP), immutable())
        fun encode(s: String): String = URLEncoder.encode(s, "UTF-8").replace("+", "%20")

        fun createChannels(c: Context) {
            if (Build.VERSION.SDK_INT < 26) return
            val nm = c.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
            nm.createNotificationChannel(NotificationChannel(CH_STATUS, c.getString(R.string.alert_ch_status), NotificationManager.IMPORTANCE_MIN))
            nm.createNotificationChannel(NotificationChannel(CH_PROMPTS, c.getString(R.string.alert_ch_prompts), NotificationManager.IMPORTANCE_HIGH)
                .apply { enableVibration(true) })
            nm.createNotificationChannel(NotificationChannel(CH_SESSIONS, c.getString(R.string.alert_ch_sessions), NotificationManager.IMPORTANCE_DEFAULT))
        }

        fun start(c: Context) {
            val i = Intent(c, AlertService::class.java)
            try { if (Build.VERSION.SDK_INT >= 26) c.startForegroundService(i) else c.startService(i) } catch (_: Exception) {}
        }
    }
}
