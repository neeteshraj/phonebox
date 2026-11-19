package net.oxog.phonebox.deskpanel

import android.app.Notification
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.RemoteInput
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.os.Build
import org.json.JSONObject

/**
 * The buttons on PhoneBox's notifications: Allow / Deny / Got it answer a permission prompt or a question on the
 * computer (POST /api/approvals/{id}), and Reply sends the typed text to a session as its next prompt
 * (POST /api/sessions/prompt). The Now playing widget's buttons come here too (POST /api/media/{action}). The network call runs off the main thread while the broadcast is kept alive.
 */
class AlertActionReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        val app = context.applicationContext
        val pending = goAsync()
        val nm = app.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        Thread {
            val link = AgentLink(app)
            try {
                when (intent.action) {
                    ACTION_DECIDE -> {
                        val id = intent.getStringExtra(EXTRA_ID) ?: return@Thread
                        val decision = intent.getStringExtra(EXTRA_DECISION) ?: return@Thread
                        val ok = post(link, "/api/approvals/${AlertService.encode(id)}?decision=$decision")
                        if (ok) { nm.cancel(AlertService.approvalId(id)); Widgets.dropApproval(app, id) }
                        else nm.notify(AlertService.approvalId(id), note(app, AlertService.CH_PROMPTS, app.getString(R.string.alert_unreachable)))
                    }
                    ACTION_REPLY -> {
                        val session = intent.getStringExtra(EXTRA_ID) ?: return@Thread
                        val text = RemoteInput.getResultsFromIntent(intent)?.getCharSequence(KEY_REPLY)?.toString()?.trim().orEmpty()
                        if (text.isEmpty()) return@Thread
                        val code = code(link, "/api/sessions/prompt?id=${AlertService.encode(session)}&text=${AlertService.encode(text)}")
                        // the notification must be replaced after an inline reply, or Android keeps its spinner going
                        val msg = when (code) {
                            200 -> app.getString(R.string.alert_sent, text.take(80))
                            403 -> app.getString(R.string.alert_prompts_off)
                            409 -> app.getString(R.string.alert_busy)
                            else -> app.getString(R.string.alert_unreachable)
                        }
                        nm.notify(AlertService.sessionId(session), note(app, AlertService.CH_SESSIONS, msg, timeout = code == 200))
                    }
                    ACTION_MEDIA -> {
                        val action = intent.getStringExtra(EXTRA_ID) ?: return@Thread
                        val r = link.request("POST", "/api/media/$action")
                        val body = r.body.use { it.bufferedReader().readText() }
                        // the answer carries the new media state: redraw the widget now instead of at the next poll
                        if (r.code == 200) Widgets.pushMedia(app, link, JSONObject(body).optJSONObject("media"))
                    }
                }
            } catch (_: Exception) {
            } finally {
                link.close()
                pending.finish()
            }
        }.start()
    }

    companion object {
        const val ACTION_DECIDE = "net.oxog.phonebox.deskpanel.DECIDE"
        const val ACTION_REPLY = "net.oxog.phonebox.deskpanel.REPLY"
        const val ACTION_MEDIA = "net.oxog.phonebox.deskpanel.MEDIA"
        const val EXTRA_ID = "id"
        const val EXTRA_DECISION = "decision"
        const val KEY_REPLY = "reply"

        fun decide(c: Context, id: String, decision: String): PendingIntent {
            val i = Intent(c, AlertActionReceiver::class.java).setAction(ACTION_DECIDE).putExtra(EXTRA_ID, id).putExtra(EXTRA_DECISION, decision)
            return PendingIntent.getBroadcast(c, (id + decision).hashCode(), i, AlertService.immutable())
        }

        /** Now playing widget: [action] is prev, playpause or next (POST /api/media/{action}). */
        fun media(c: Context, action: String): PendingIntent {
            val i = Intent(c, AlertActionReceiver::class.java).setAction(ACTION_MEDIA).putExtra(EXTRA_ID, action)
            return PendingIntent.getBroadcast(c, ("media" + action).hashCode(), i, AlertService.immutable())
        }

        /** Inline reply: the RemoteInput fills the intent in, so on Android 12+ this one has to be mutable. */
        fun reply(c: Context, session: String): PendingIntent {
            val i = Intent(c, AlertActionReceiver::class.java).setAction(ACTION_REPLY).putExtra(EXTRA_ID, session)
            val flags = PendingIntent.FLAG_UPDATE_CURRENT or (if (Build.VERSION.SDK_INT >= 31) PendingIntent.FLAG_MUTABLE else 0)
            return PendingIntent.getBroadcast(c, ("reply" + session).hashCode(), i, flags)
        }

        private fun code(link: AgentLink, path: String): Int = try {
            val r = link.request("POST", path)
            r.body.close()
            r.code
        } catch (_: Exception) { -1 }

        private fun post(link: AgentLink, path: String) = code(link, path) == 200

        private fun note(c: Context, channel: String, text: String, timeout: Boolean = false): Notification =
            (if (Build.VERSION.SDK_INT >= 26) Notification.Builder(c, channel) else @Suppress("DEPRECATION") Notification.Builder(c))
                .setSmallIcon(R.drawable.ic_tile)
                .setColor(0xFFD97757.toInt())
                .setContentTitle("PhoneBox")
                .setContentText(text)
                .setAutoCancel(true)
                .apply { if (timeout && Build.VERSION.SDK_INT >= 26) setTimeoutAfter(5000) }
                .build()
    }
}
