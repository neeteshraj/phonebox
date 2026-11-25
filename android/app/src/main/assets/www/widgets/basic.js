import { bridge, runListeners, sessionRuns, monogramColor, sheetDrag, $, API, D, LEVEL_COLOR, LOCALE, mediaHtml, METRICS, PANEL_HEADERS, PREVIEW, Q_STYLE, S_ORDER, ago, api, averageColor, buildLayout, buzz, cfg, cfgVersion, chart, color, cssVar, currentProcessesSort, emit, fillIcon, fmtClock, fmtDur, fmtHM, fmtMem, fmtReset, fmtTime, fmtValue, gotoProcesses, gpuMem, gpusOf, h, en, haptic, hasProcessesPage, hist, hour12, icon, keyButton, levelOf, media, metricText, online, openSheet, paceInfo, pad, prRow, pushView, quotaProviders, r1, rateStr, registerProcessesSorter, renderStatus, ringSvg, setArtColor, configMark, configReadSince, localWriteStored, setMediaState, setVolDragging, shortDur, shortGpu, store, t, tick, toast, volDragging, wxBlock, wxDeg, wxIcon } from '../main.js';
import { W } from '../registry.js';
import { dictateInto, stopDictation } from './dictate.js';
import { lyricsFor, lineAt } from './lyrics.js';
import { invalidateDeckState } from './controls.js';

/** The chance of rain under a forecast cell, from 20 % up, with a drop glyph. */
function rainEl(rain) { const e = h('span', 'fc-r'); if (rain >= 20) e.innerHTML = `<svg class="drop" viewBox="0 0 24 24" aria-hidden="true"><path d="M12 3s6 6.6 6 11a6 6 0 0 1-12 0c0-4.4 6-11 6-11z" fill="currentColor"/></svg>${rain}%`; return e; }
/** A forecast cell's weather glyph (SVG markup from wxIcon). */
function wxEl(svg) { const e = h('span', 'fc-ic'); e.innerHTML = svg; return e; }

function bindProcessLink(box, sortTarget) {
  if (!hasProcessesPage()) return;
  box.style.cursor = 'pointer';
  box.addEventListener('click', () => {
    if (!hasProcessesPage()) return;
    haptic();
    gotoProcesses(sortTarget);
  });
}

// ================================================================= widgets
// Each widget: { needs: [data kinds], bare?: no card background, create(box, ctx) -> { update(kind) } }
// Data kinds: tick (every second), fast (4×/s), state, weather, quotas, work, procs, push, notes.

W.clock = {
  bare: true, needs: ['tick', 'state', 'weather', 'quotas', 'work'],
  create(box, { o }) {
    const status = o.status !== false ? h('div', 'status') : null;
    const hh = h('span', 'hh', '--'), mm = h('span', 'mm', '--'), ss = h('span', 'sec'), ap = h('span', 'ap');
    const date = o.date !== false ? h('div', 'date') : null;
    const wx = o.weather !== false ? wxBlock() : null;
    box.append(status || '', h('div', 'clock', hh, h('span', 'colon', ':'), mm, ss, ap), date || '', wx || '');
    box.style.setProperty('--tf', (0.84 - (status ? 0.1 : 0) - (date ? 0.11 : 0) - (wx ? 0.2 : 0)).toFixed(2));
    return {
      // without the weather line the clock must not make the panel fetch the weather
      needs: wx ? W.clock.needs : W.clock.needs.filter((k) => k !== 'weather'),
      update(kind) {
        if (kind === 'tick') {
          const now = new Date();
          const H = now.getHours(), h12 = hour12();
          hh.textContent = h12 ? String(H % 12 || 12) : pad(H);
          ap.textContent = h12 ? (H < 12 ? 'AM' : 'PM') : '';
          mm.textContent = pad(now.getMinutes());
          ss.textContent = cfg.display?.showSeconds === false ? '' : pad(now.getSeconds());
          if (date) date.textContent = now.toLocaleDateString(LOCALE, { weekday: 'long', day: 'numeric', month: 'long' });
          if (status && now.getSeconds() % 30 === 0) renderStatus(status, o); // battery
        } else if (kind === 'weather') wx?.update();
        else if (status) renderStatus(status, o);
      },
    };
  },
};

W.weather = {
  needs: ['weather'],
  create(box) {
    const wx = wxBlock();
    box.append(wx);
    return { update: () => wx.update() };
  },
};

W.forecast = {
  needs: ['weather'],
  create(box, { o }) {
    const daily = o.mode === 'daily';
    const row = h('div', 'fc');
    box.append(h('div', 'w-title', en(daily ? '7-day forecast' : 'Next hours'), h('span', 'w-sub', '')), row);
    return {
      update() {
        const w = D.weather;
        if (!w?.hourly || !w?.daily) { row.replaceChildren(h('div', 'empty', D.weather === null ? t('w.loading') : t('w.weatherUnavailable'))); return; }
        box.querySelector('.w-sub').textContent = cfg.weather?.city || '';
        const vertical = box.clientHeight > box.clientWidth * 0.9;
        const room = vertical ? box.clientHeight - 30 : box.clientWidth - 16;
        const n = Math.max(3, Math.min(daily ? 7 : 12, Math.floor(room / (vertical ? 30 : 50))));
        row.classList.toggle('vert', vertical);
        const cells = [];
        if (daily) {
          const d = w.daily;
          for (let i = 0; i < Math.min(n, d.time.length); i++) {
            const day = i === 0 ? t('time.today') : new Date(d.time[i] + 'T12:00').toLocaleDateString(LOCALE, { weekday: 'short' });
            const rain = d.precipitation_probability_max?.[i];
            cells.push(h('div', 'fc-i', h('span', 'fc-t', day), wxEl(wxIcon(d.weather_code[i])),
              h('span', 'fc-v', wxDeg(d.temperature_2m_max[i]) + '°', h('small', null, ' ' + wxDeg(d.temperature_2m_min[i]) + '°')),
              rainEl(rain)));
          }
        } else {
          const hr = w.hourly;
          let start = hr.time.indexOf((w.current?.time || '').slice(0, 13) + ':00');
          if (start < 0) start = 0;
          const step = n >= 10 ? 1 : 2;
          for (let k = 0; k < n; k++) {
            const i = start + k * step;
            if (i >= hr.time.length) break;
            const rain = hr.precipitation_probability?.[i];
            cells.push(h('div', 'fc-i', h('span', 'fc-t', k === 0 ? 'Now' : hr.time[i].slice(11, 13)), wxEl(wxIcon(hr.weather_code[i], hr.is_day?.[i])),
              h('span', 'fc-v', wxDeg(hr.temperature_2m[i]) + '°'), rainEl(rain)));
          }
        }
        row.replaceChildren(...cells);
      },
    };
  },
};

/** A sheet to start a new Claude Code session: pick one of the recent project folders, then talk to it. */
function openNewSession() {
  haptic();
  const backdrop = h('div', 'session-backdrop');
  const panel = h('div', 'session-panel has-chat');
  panel.setAttribute('role', 'dialog');
  panel.setAttribute('aria-modal', 'true');
  const close = h('button', 'session-close', icon('x'));
  close.setAttribute('aria-label', 'Close');
  const dismiss = () => { backdrop.remove(); panel._cleanup?.(); };
  close.addEventListener('click', dismiss);
  backdrop.addEventListener('click', (e) => { if (e.target === backdrop) dismiss(); });
  const chips = h('div', 'proj-chips', h('span', 'muted', t('w.loading')));
  let cwd = '';
  panel.append(h('div', 'sheet-grab'), h('div', 'session-panel-head', h('span', null, '✳ Claude Code'), close), h('h2', null, 'New session'), chips);
  const s = { id: 'new:pending', tool: 'claude' };
  panel.append(...sessionChat(s, panel, { newIn: () => cwd }));
  sheetDrag(panel, dismiss);
  backdrop.append(panel);
  document.body.append(backdrop);
  api('/sessions/projects').then((list) => {
    if (!Array.isArray(list) || !list.length) { chips.replaceChildren(h('span', 'muted', 'No recent projects')); return; }
    chips.replaceChildren(...list.slice(0, 12).map((p, i) => {
      const b = h('button', 'proj-chip' + (i === 0 ? ' on' : ''), p.name);
      b.type = 'button';
      b.title = p.cwd;
      b.addEventListener('click', () => { haptic(); cwd = p.cwd; chips.querySelectorAll('.proj-chip').forEach((x) => x.classList.toggle('on', x === b)); });
      return b;
    }));
    cwd = list[0].cwd;
  }).catch(() => chips.replaceChildren(h('span', 'muted', t('panel.pcUnreachable'))));
}

/**
 * The conversation part of a session's sheet: the last prompt sent from the phone and the reply streaming back
 * ("session-run" events), and a box to send the next one. The agent runs it headlessly in the session's folder;
 * tool permissions still arrive as the usual permission prompt.
 */
function sessionChat(s, panel, { newIn = null } = {}) {
  const thread = h('div', 'chat-thread');
  const input = document.createElement('textarea');
  input.className = 'chat-input';
  input.rows = 1;
  input.maxLength = 8000;
  input.placeholder = t('sess.promptPlaceholder');
  const send = h('button', 'chat-send', icon('send'));
  send.type = 'button';
  send.setAttribute('aria-label', t('sess.send'));
  const hint = h('div', 'chat-hint');
  // speak the prompt: the words land in the box (partial results live), you read them over and send
  const mic = h('button', 'chat-mic', icon('mic'));
  mic.type = 'button';
  mic.setAttribute('aria-label', 'Dictate');
  mic.hidden = !(bridge?.listen && bridge?.canListen);
  let speech = 0;
  mic.addEventListener('click', () => {
    haptic();
    if (speech) { stopDictation(speech); return; }
    const before = input.value ? input.value.replace(/\s*$/, ' ') : '';
    speech = dictateInto((kind, text) => {
      if (kind === 'partial' || kind === 'final') { input.value = before + text; grow(); }
      if (kind === 'final' || kind === 'error') { speech = 0; mic.classList.remove('on'); }
    });
    mic.classList.toggle('on', !!speech);
    if (!speech) say('Speech recognition is not available on this phone');
  });
  let run = sessionRuns.get(s.id) || null, sending = false;
  const busy = () => run?.status === 'running';
  const draw = () => {
    const items = run?.items || [];
    thread.replaceChildren(...items.map((it) => {
      if (it.role === 'tool') return h('div', 'chat-tool', icon('tool'), h('span', null, it.text));
      return h('div', 'chat-msg ' + (it.role === 'user' ? 'me' : it.role === 'error' ? 'err' : 'ai'), it.text);
    }), ...(busy() ? [h('div', 'chat-typing', h('i'), h('i'), h('i'))] : []));
    thread.scrollTop = thread.scrollHeight;
    send.replaceChildren(icon(busy() ? 'stop' : 'send'));
    send.classList.toggle('stop', busy());
    send.setAttribute('aria-label', t(busy() ? 'sess.stop' : 'sess.send'));
    panel.classList.toggle('chatting', items.length > 0);
  };
  const say = (msg) => { hint.textContent = msg || ''; };
  const onRun = (r) => { if (r.sessionId === s.id || (r.realSessionId && r.realSessionId === s.id)) { run = r; draw(); } };
  runListeners.add(onRun);
  // the keyboard covers the bottom of the screen: lift the sheet by the part of the window it hides
  const vv = window.visualViewport;
  const lift = () => { panel.style.marginBottom = vv ? Math.max(0, window.innerHeight - vv.height - vv.offsetTop) + 'px' : ''; };
  vv?.addEventListener('resize', lift);
  panel._cleanup = () => { runListeners.delete(onRun); vv?.removeEventListener('resize', lift); };
  const grow = () => { input.style.height = 'auto'; input.style.height = Math.min(input.scrollHeight, 110) + 'px'; };
  input.addEventListener('input', grow);
  const submit = async () => {
    if (busy()) {
      haptic();
      try { await api('/sessions/prompt/cancel?runId=' + encodeURIComponent(run.runId), 'POST'); } catch (_) {}
      return;
    }
    const text = input.value.trim();
    if (!text || sending) return;
    // the agent caps a request line at 16 KB; non-Latin text grows ~9x once URL-encoded
    if (encodeURIComponent(text).length > 15000) { say('Too long to send from the phone: shorten it a little'); return; }
    sending = true; send.disabled = true; say('');
    haptic();
    // shown at once; the agent's first snapshot replaces it
    run = { runId: null, sessionId: s.id, status: 'running', items: [...(run?.items || []), { role: 'user', text }] };
    draw();
    try {
      // in the query string: the phone app's proxy forwards no request body (like the Note widget's text).
      // A new session's first prompt starts it in the chosen folder; later ones continue the real session.
      const fresh = newIn && s.id.startsWith('new:') && !run.realSessionId && run.items.length <= 1;
      if (newIn && run.realSessionId) s.id = run.realSessionId;
      const url = fresh ? `${API}/sessions/new?cwd=${encodeURIComponent(newIn())}&text=${encodeURIComponent(text)}`
        : `${API}/sessions/prompt?id=${encodeURIComponent(s.id)}&text=${encodeURIComponent(text)}`;
      if (fresh && !newIn()) { say('Pick a project first'); run = { ...run, status: 'error', items: run.items.slice(0, -1) }; draw(); return; }
      const r = await fetch(url, { method: 'POST', headers: PANEL_HEADERS });
      if (r.ok) {
        input.value = ''; grow();
        const body = await r.json().catch(() => ({}));
        if (fresh && body.sessionId) { s.id = body.sessionId; run.sessionId = s.id; }
        if (!run.runId) run.runId = body.runId || null;
      }
      else {
        run = { ...run, status: 'error', items: run.items.slice(0, -1) };
        say(r.status === 403 ? t('sess.promptsOff') : r.status === 409 ? t('sess.busyNow') : ((await r.json().catch(() => ({}))).error || t('panel.pcUnreachable')));
        draw();
      }
    } catch (_) { run = { ...run, status: 'error', items: run.items.slice(0, -1) }; say(t('panel.pcUnreachable')); draw(); }
    finally { sending = false; send.disabled = false; }
  };
  send.addEventListener('click', submit);
  input.addEventListener('keydown', (e) => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); submit(); } });
  draw();
  // fetch the last run in case it happened while the sheet was closed
  if (!s.id.startsWith('new:')) api('/sessions/run?id=' + encodeURIComponent(s.id)).then((r) => { if (r && r.sessionId === s.id) { run = r; sessionRuns.set(s.id, r); draw(); } }).catch(() => {});
  return [thread, hint, h('div', 'chat-compose', mic, input, send)];
}

W.media = {
  needs: ['state', 'fast'],
  create(box, { o = {} } = {}) {
    box.innerHTML = mediaHtml();
    const q = (s) => box.querySelector(s);
    const img = q('.art'), vol = q('.volr');
    const sourceIcon = q('.m-source-icon');
    const sourceIcons = {
      YouTube: '<svg viewBox="0 0 24 24" aria-hidden="true"><rect x="1" y="4" width="22" height="16" rx="5" fill="#ff0033"/><path d="m10 8 6 4-6 4z" fill="#fff"/></svg>',
      'YouTube Music': '<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="11" fill="#ff0033"/><circle cx="12" cy="12" r="6.5" fill="none" stroke="#fff" stroke-width="1.4"/><path d="m10 8.5 5.5 3.5-5.5 3.5z" fill="#fff"/></svg>',
      Spotify: '<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="11" fill="#1ed760"/><path d="M6.5 9.2c3.8-1.1 8-.8 11.2 1M7.2 12.4c3.1-.9 6.5-.6 9.2.9M7.8 15.4c2.5-.6 5-.4 7.1.7" fill="none" stroke="#000" stroke-width="1.6" stroke-linecap="round"/></svg>',
      Netflix: '<svg viewBox="0 0 24 24" aria-hidden="true"><rect x="2" y="2" width="20" height="20" rx="5" fill="#141414"/><path d="M8.5 5.5h2.6l2.4 7V5.5h2.5v13h-2.5l-2.5-7.2v7.4H8.5z" fill="#e50914"/></svg>',
      'Internet Radio': '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M6.2 6.8 18.5 2a1 1 0 0 1 .7 1.9L10.8 7H20a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V9a2 2 0 0 1 2-2h2.2zm1.8 5.2a3 3 0 1 0 0 6 3 3 0 0 0 0-6zm6 1.5v1.8h4v-1.8h-4zm0 3.2v1.8h4v-1.8h-4z" fill="#38bdf8"/></svg>',
    };
    let artId = -1, volTimer = 0, volumeRelease = 0;
    // Synced lyrics (option): looked up on lrclib.net by artist, title and length when the track changes; the line
    // for the current position is shown under the artist. Off by default: it sends the song's name to that site.
    const lyric = h('div', 'm-lyric');
    if (o.lyrics) q('.m-text').append(lyric);
    let lyricKey = '', lines = null;
    const findLyrics = async (m) => {
      const key = `${m.artist}|${m.title}`;
      if (!o.lyrics || key === lyricKey) return;
      lyricKey = key; lines = null; lyric.replaceChildren();
      const l = await lyricsFor(m);
      if (key === lyricKey) lines = l?.synced?.filter((x) => x.text) || null;
    };
    let lyricShown = -2;
    const paintLyric = (pos) => {
      if (!lines?.length) { if (lyricShown !== -2) { lyric.replaceChildren(); lyricShown = -2; } return; }
      const i = lineAt(lines, pos);
      if (i === lyricShown) return;
      lyricShown = i;
      lyric.replaceChildren(h('b', null, i >= 0 ? lines[i].text : '♪'), h('span', null, lines[i + 1]?.text || ''));
      lyric.classList.remove('in'); void lyric.offsetWidth; lyric.classList.add('in');
    };
    // Several players at once (state.mediaSessions, e.g. two YouTube tabs and Spotify): the card shows one, a dot
    // per player, and switches on a swipe or a dot tap. Controls then go to the player on screen (?session=id).
    let sel = null, view = null, swiped = false;
    const dots = h('div', 'm-dots');
    box.append(dots);
    const sessions = () => { const l = D.state?.mediaSessions; return Array.isArray(l) && l.length > 1 ? l : null; };
    const shown = () => (sessions() && view) || media;
    const sq = () => (sessions() && view ? 'session=' + encodeURIComponent(view.id) : '');
    const pick = (i, dir) => {
      const l = sessions();
      if (!l || !l[i] || l[i].id === sel) return;
      sel = l[i].id;
      haptic();
      box.classList.remove('m-slide-l', 'm-slide-r');
      void box.offsetWidth;
      box.classList.add(dir > 0 ? 'm-slide-l' : 'm-slide-r');
      self.update('state');
    };
    let sx = 0, sy = 0, sid = null;
    box.addEventListener('pointerdown', (e) => {
      if (!sessions() || e.target.closest('input, .m-dots')) return;
      sid = e.pointerId; sx = e.clientX; sy = e.clientY;
    });
    box.addEventListener('pointercancel', () => (sid = null));
    box.addEventListener('pointerup', (e) => {
      if (e.pointerId !== sid) return;
      sid = null;
      const dx = e.clientX - sx, dy = e.clientY - sy, l = sessions();
      if (!l || Math.abs(dx) < 40 || Math.abs(dx) < Math.abs(dy) * 1.3) return;
      swiped = true; // the click that follows this pointerup must not press a button
      setTimeout(() => (swiped = false), 60);
      const i = Math.max(0, l.findIndex((x) => x.id === sel));
      pick(Math.max(0, Math.min(l.length - 1, i + (dx < 0 ? 1 : -1))), dx < 0 ? 1 : -1);
    });
    const renderDots = (l) => {
      if (!l) { dots.replaceChildren(); return; }
      if (dots.childElementCount !== l.length) {
        dots.replaceChildren(...l.map((_, i) => {
          const d = h('button', null);
          d.type = 'button';
          d.addEventListener('click', (e) => {
            e.stopPropagation();
            const cur = sessions(), at = cur ? cur.findIndex((x) => x.id === sel) : 0;
            pick(i, i > at ? 1 : -1);
          });
          return d;
        }));
      }
      [...dots.children].forEach((d, i) => {
        d.classList.toggle('on', l[i].id === sel);
        d.classList.toggle('live', !!l[i].playing);
        d.setAttribute('aria-label', `${l[i].app}: ${l[i].title}`);
      });
    };
    img.addEventListener('load', () => { img.classList.add('on'); setArtColor(averageColor(img)); box.style.setProperty('--art', `url("${img.src}")`); });
    img.addEventListener('error', () => img.classList.remove('on'));
    box.querySelectorAll('[data-media]').forEach((btn) => btn.addEventListener('click', async () => {
      if (swiped) return;
      haptic();
      const action = btn.dataset.media, cur = shown(), qs = sq();
      if (action === 'playpause' && cur) {
        cur.playing = !cur.playing; // optimistic update
        cur.at = performance.now();
        box.classList.toggle('playing', cur.playing);
      }
      try {
        const res = await api('/media/' + action + (qs ? '?' + qs : ''), 'POST');
        if (!qs && res && typeof res === 'object' && 'media' in res) setMediaState(res.media);
        invalidateDeckState();
        emit('tick');
      } catch (_) {}
    }));
    q('.bar').addEventListener('click', async (e) => {
      const cur = shown(), qs = sq();
      if (swiped || !cur || !cur.durMs || !cur.canSeek) return;
      const r = e.currentTarget.getBoundingClientRect();
      const ms = Math.round(((e.clientX - r.left) / r.width) * cur.durMs);
      haptic();
      cur.posMs = ms; cur.at = performance.now();
      try { await api('/media/seek?ms=' + ms + (qs ? '&' + qs : ''), 'POST'); } catch (_) {}
    });
    const paintVol = (v) => { vol.style.setProperty('--p', v + '%'); q('.vol-val').textContent = v; };
    const cancelVolumeDrag = () => {
      clearTimeout(volTimer);
      clearTimeout(volumeRelease);
      setVolDragging(vol, false);
    };
    vol.addEventListener('input', () => {
      clearTimeout(volumeRelease);
      setVolDragging(vol, true);
      paintVol(vol.value);
      clearTimeout(volTimer);
      volTimer = setTimeout(() => api('/volume?level=' + vol.value, 'POST').catch(() => {}), 120);
    });
    vol.addEventListener('change', () => {
      clearTimeout(volumeRelease);
      volumeRelease = setTimeout(() => setVolDragging(vol, false), 600);
    });
    vol.addEventListener('pointercancel', cancelVolumeDrag);
    q('.mute').addEventListener('click', async () => {
      haptic();
      if ((!D.state?.audio || D.state.audio.canControl === false)) return;
      try { const a = await api('/volume?op=mute', 'POST'); D.state.audio = a; emit('state'); } catch (_) {}
    });

    const self = {
      update(kind) {
        if (kind === 'fast') {
          const mv = shown();
          if (!mv) return;
          if (mv.durMs > 0) {
            const pos = Math.min(mv.durMs, mv.posMs + (mv.playing ? performance.now() - mv.at : 0));
            q('.fill').style.width = (pos / mv.durMs) * 100 + '%';
            q('.m-pos').textContent = fmtTime(pos);
            if (o.lyrics) paintLyric(pos);
          } else if (mv.app === 'Internet Radio') {
            const pos = Math.max(0, (mv.posMs || 0) + (mv.playing ? performance.now() - mv.at : 0));
            q('.fill').style.width = mv.playing ? '100%' : '0';
            q('.m-pos').textContent = fmtTime(pos);
          }
          return;
        }
        const l = sessions();
        if (l) {
          const s = l.find((x) => x.id === sel) || l[0];
          sel = s.id;
          view = { ...s, at: performance.now() };
        } else { sel = null; view = null; }
        renderDots(l);
        box.style.touchAction = l ? 'pan-y' : ''; // a sideways swipe here switches players, not pages
        const m = l ? view : D.state?.media, a = D.state?.audio;
        vol.disabled = (!a || a.canControl === false);
        box.classList.toggle('volume-unavailable', (!a || a.canControl === false));
        q('.mute').disabled = (!a || a.canControl === false);
        box.classList.toggle('muted-on', !!a?.muted);
        if (a) {
          if (!volDragging) { vol.value = a.volume; paintVol(a.volume); }
        } else q('.vol-val').textContent = 'n/a';
        box.classList.toggle('idle', !m);
        if (!m) {
          box.classList.remove('playing');
          q('.m-app-name').textContent = 'Media';
          sourceIcon.replaceChildren();
          q('.m-title').textContent = t('w.nothingPlaying');
          q('.m-artist').textContent = t('w.pressToResume');
          q('.fill').style.width = '0';
          q('.m-pos').textContent = q('.m-dur').textContent = '0:00';
          if (artId !== 0) { artId = 0; img.classList.remove('on'); setArtColor(null); box.style.removeProperty('--art'); }
          return;
        }
        box.classList.toggle('playing', m.playing);
        findLyrics(m);
        q('.m-app-name').textContent = m.app || 'Media';
        sourceIcon.innerHTML = sourceIcons[m.app] || '';
        q('.m-title').textContent = m.title || 'Unknown track';
        q('.m-artist').textContent = [m.artist, m.album].filter(Boolean).join(' · ') || '—';
        if (m.app === 'Internet Radio' && !(m.durMs > 0)) {
          q('.fill').style.width = m.playing ? '100%' : '0';
          q('.m-pos').textContent = fmtTime(m.posMs || 0);
          q('.m-dur').textContent = m.playing ? 'LIVE' : '0:00';
        } else {
          q('.m-dur').textContent = fmtTime(m.durMs);
        }
        const artKey = l ? (m.artId ? m.id + ':' + m.artId : 0) : m.artId;
        if (artKey !== artId) {
          artId = artKey;
          if (m.artId) img.src = l ? `${API}/media/art?session=${encodeURIComponent(m.id)}&v=${m.artId}` : `${API}/art?id=${m.artId}`;
          else { img.classList.remove('on'); setArtColor(null); box.style.removeProperty('--art'); }
        }
      },
      destroy: cancelVolumeDrag,
    };
    return self;
  },
};

W.stat = {
  needs: ['state'],
  create(box, { o }) {
    const m = METRICS[o.metric] || METRICS.cpu;
    const c = color(m.color);
    box.style.setProperty('--c', c);
    const canvas = h('canvas');
    // an icon badge for the metric, its name, and a small ring for metrics that have a share (CPU %, RAM used …)
    const glyph = { cpu: 'cpu', cputemp: 'thermo', gpu: 'gpu', gputemp: 'thermo', hotspot: 'thermo', gpupower: 'zap', gpuclock: 'gpu',
      ram: 'memory', vram: 'memory', net: 'updown', down: 'updown', up: 'updown', ping: 'pulse' }[o.metric] || 'pulse';
    const name = h('span', 't-name', m.label), ring = h('span', 't-ring');
    const lbl = h('div', 't-label', h('span', 'ts-ic', icon(glyph)), name);
    const val = h('b', null, '—'), unit = h('small'), sub = h('div', 't-sub', ' ');
    box.append(canvas, lbl, ring, h('div', 't-val', val, unit), sub);
    if (o.metric === 'cpu' || o.metric === 'ram') bindProcessLink(box, o.metric === 'cpu' ? 'cpu' : 'mem');
    return {
      update() {
        const s = D.state;
        if (!s) return;
        const [v, u] = metricText(m, s);
        val.textContent = v;
        unit.textContent = u;
        const pctVal = m.pct?.(s);
        name.textContent = pctVal != null && m.label === 'RAM' ? `RAM (${Math.round(pctVal)}%)` : m.label;
        const share = pctVal ?? (m.unit === '%' ? m.val(s) : null);
        ring.hidden = share == null;
        if (share != null) ring.style.setProperty('--p', Math.max(0, Math.min(100, share)));
        sub.textContent = m.sub?.(s) || ' ';
        chart(canvas, [{ data: hist[m.hist || o.metric] || hist.cpu, color: c, max: m.max(s) }]);
      },
    };
  },
};

W.gauge = {
  needs: ['state'],
  create(box, { o }) {
    const m = METRICS[o.metric] || METRICS.cpu;
    box.style.setProperty('--q', color(m.color));
    const dial = h('div', 'g-dial'), pct = h('div', 'g-val'), sub = h('div', 'g-sub');
    box.append(dial, h('div', 'g-label', m.label), sub);
    dial.append(pct);
    if (o.metric === 'cpu' || o.metric === 'ram') bindProcessLink(box, o.metric === 'cpu' ? 'cpu' : 'mem');
    let svg = null;
    return {
      update() {
        const s = D.state;
        if (!s) return;
        const raw = m.pct ? m.pct(s) : m.val(s);
        const p = raw == null ? 0 : (raw / (m.pct ? 100 : m.max(s))) * 100;
        const warn = m.hot ? (raw > m.hot[1] ? '#ef4444' : raw > m.hot[0] ? '#f59e0b' : '') : (p >= 95 ? '#ef4444' : p >= 85 ? '#f59e0b' : '');
        dial.style.setProperty('--qc', warn || 'var(--q)');
        if (svg) svg.remove();
        dial.insertAdjacentHTML('afterbegin', ringSvg(p));
        svg = dial.firstElementChild;
        const [v, u] = metricText(m, s);
        pct.replaceChildren(v, h('small', null, u));
        sub.textContent = m.sub?.(s) || '';
      },
    };
  },
};

W.chart = {
  needs: ['state'],
  create(box, { o }) {
    // a list of only unknown names falls back like no list at all; own keys only, so "constructor" is unknown too
    const keys = (Array.isArray(o.metrics) && o.metrics.length ? o.metrics : ['cpu']).filter((k) => Object.prototype.hasOwnProperty.call(METRICS, k));
    if (!keys.length) keys.push('cpu');
    const single = keys.length === 1 ? METRICS[keys[0]] : null;
    const isNet = keys.every((k) => k === 'down' || k === 'up');
    const label = single ? single.name : isNet ? 'Network' : keys.map((k) => METRICS[k].name).join(' · ');
    box.style.setProperty('--c', color((single || METRICS[keys[0]]).color));
    const head = h('div', 's-head', h('span', 's-label', label), h('span', 's-name', ''));
    const main = h('div', 's-main');
    const canvas = h('canvas');
    box.classList.add('ch');
    box.append(head, main, canvas);
    if (keys.includes('cpu') || keys.includes('ram')) bindProcessLink(box, keys.includes('cpu') ? 'cpu' : 'mem');
    return {
      update() {
        const s = D.state;
        if (!s) return;
        // the window's average and peak, from the same history the chart draws
        const stats = (k) => {
          const d = hist[METRICS[k].hist || k] || [];
          return d.length ? { avg: d.reduce((a, x) => a + x, 0) / d.length, peak: Math.max(...d) } : null;
        };
        const fmtV = (mm, v) => mm.rate ? rateStr(v) : mm.fmt ? mm.fmt(v) + ' ' + mm.unit : Math.round(v) + mm.unit;
        if (single) {
          const [v, u] = metricText(single, s);
          const st = stats(keys[0]);
          const sub = single.sub?.(s), title = single.title?.(s) || '';
          main.replaceChildren(h('b', null, v), h('small', null, u),
            h('span', 'ch-stats',
              sub && sub !== title ? h('span', 'ch-chip', sub) : null,
              st ? h('span', 'ch-stat', h('small', null, 'Avg'), h('b', null, fmtV(single, st.avg))) : null,
              st ? h('span', 'ch-stat', h('small', null, 'Peak'), h('b', null, fmtV(single, st.peak))) : null));
          head.lastChild.textContent = title;
        } else {
          main.replaceChildren(...keys.map((k) => {
            const mm = METRICS[k];
            const [v, u] = metricText(mm, s);
            const st = stats(k);
            const e = h('span', 'multi ch-multi', h('span', 'ch-mtop', h('i', 'ch-key'), h('span', 'ch-mlabel', k === 'down' ? 'Download' : k === 'up' ? 'Upload' : mm.name)),
              h('b', null, v + ' ' + u), st ? h('small', null, 'peak ' + fmtV(mm, st.peak)) : null);
            e.style.setProperty('--k', color(mm.color));
            return e;
          }));
          head.lastChild.textContent = isNet && s.ping ? `Ping ${s.ping.ms != null ? Math.round(s.ping.ms) + ' ms' : '—'} · loss ${s.ping.lossPct}%` : '';
        }
        const max0 = METRICS[keys[0]].max(s), m0 = METRICS[keys[0]];
        chart(canvas, keys.map((k) => ({ data: hist[METRICS[k].hist || k], color: color(METRICS[k].color), max: METRICS[k].max(s) })),
          { grid: true, smooth: true, dot: true, labels: (f) => m0.rate ? rateStr(max0 * f) : m0.unit === '%' ? Math.round(max0 * f) + '%' : r1(max0 * f) + ' ' + m0.unit });
      },
    };
  },
};

W.meters = {
  needs: ['state'],
  create(box) {
    box.style.setProperty('--c', cssVar('--ram'));
    box.classList.add('mx');
    const up = h('span', 's-name mx-up');
    const hero = h('div', 'mx-hero');
    const list = h('div', 'meters');
    box.append(h('div', 's-head', h('span', 's-label', t('w.memory')), up), hero, list);
    bindProcessLink(box, 'mem');
    const level = (pct) => pct >= 90 ? 'hot' : pct >= 75 ? 'warm' : '';
    // a row: name, used of total, a bar, and what is left
    const meter = (name, used, total, unit, c, leftText) => {
      const pct = total ? Math.min(100, (used / total) * 100) : 0;
      const e = h('div', 'meter mx-row ' + level(pct),
        h('div', 'm-row', h('span', null, name), h('span', null, h('b', null, `${r1(used)}`), ` / ${Math.round(total)} ${unit}`)),
        h('div', 'track', h('i')));
      e.title = `${Math.round(pct)}% used · ${leftText}`;
      e.querySelector('i').style.width = pct + '%';
      if (c) e.style.setProperty('--c', c);
      return e;
    };
    return {
      update() {
        const pc = D.state?.pc;
        if (!pc) return;
        const s = pc.uptimeSec, d = Math.floor(s / 86400), hr = Math.floor((s % 86400) / 3600), m = Math.floor((s % 3600) / 60);
        up.textContent = 'Up ' + (d ? `${d} d ${hr} h` : hr ? `${hr} h ${m} min` : `${m} min`);
        // RAM is the headline: how much is in use, of how much, and a thick bar
        const pct = pc.ramTotalGb ? (pc.ramUsedGb / pc.ramTotalGb) * 100 : 0;
        const bar = h('div', 'mx-bar', h('i'));
        bar.firstChild.style.width = Math.min(100, pct) + '%';
        hero.className = 'mx-hero ' + level(pct);
        hero.replaceChildren(h('div', 'mx-big', h('b', null, r1(pc.ramUsedGb)),
          h('small', null, `/ ${Math.round(pc.ramTotalGb)} GB RAM · ${r1(Math.max(0, pc.ramTotalGb - pc.ramUsedGb))} GB free`),
          h('span', 'mx-pill', pct >= 90 ? `${Math.round(pct)}% · pressure` : Math.round(pct) + '%')), bar);
        const rows = [];
        // one row per GPU (the integrated one counts its shared memory); named once there is more than one
        const gpus = gpusOf(D.state);
        for (const g of gpus) {
          const mm = gpuMem(g);
          if (mm) rows.push(meter(gpus.length > 1 ? `${mm.label === 'VRAM' ? 'VRAM' : 'GPU'} · ${shortGpu(g.name)}` : mm.label === 'VRAM' ? 'Video memory' : 'GPU memory',
            mm.used, mm.total, 'GB', cssVar('--gpu'), `${r1(Math.max(0, mm.total - mm.used))} GB free`));
        }
        const disks = h('div', 'disks', ...(pc.disks || []).map((dk) => meter(dk.name === '/' ? 'Disk' : dk.name, dk.usedGb, dk.totalGb, 'GB', '#94a3b8',
          `${Math.round(dk.totalGb - dk.usedGb)} GB free`)));
        list.replaceChildren(...rows, disks);
      },
    };
  },
};

W.quotas = {
  needs: ['quotas', 'tick'],
  /**
   * Creates the AI Quotas widget instance with optional paged provider rotation.
   */
  create(box, { o }) {
    const tokens = (n) => n == null ? '—' : n >= 1e6 ? (n / 1e6).toFixed(1) + 'M' : n >= 1e3 ? (n / 1e3).toFixed(1) + 'k' : String(n);
    const grid = h('div', 'q-grid', h('div', 'empty', 'Quota data arrives once the computer is connected.'));
    box.append(grid);
    let last = 0;
    let offset = 0;
    let lastRotate = Date.now();

    const isRotateEnabled = () => {
      if (o.rotate !== undefined && o.rotate !== null) return !!o.rotate;
      if (cfg.codexBar?.rotate !== undefined && cfg.codexBar?.rotate !== null) return !!cfg.codexBar.rotate;
      return true;
    };

    const getPageSize = () => {
      if (o.pageSize !== undefined && o.pageSize !== null && o.pageSize !== '') {
        const n = Number(o.pageSize);
        return Number.isFinite(n) ? Math.max(0, Math.min(6, Math.floor(n))) : 2;
      }
      return cfg.codexBar?.pageSize ?? 2;
    };

    const getRotateInterval = () => {
      if (!isRotateEnabled()) return 0;
      if (o.rotateInterval !== undefined && o.rotateInterval !== null && o.rotateInterval !== '') {
        const n = Number(o.rotateInterval);
        return Number.isFinite(n) ? Math.max(2, Math.min(300, n)) : 8;
      }
      return cfg.codexBar?.rotateInterval ?? 8;
    };

    const getProviders = () => {
      if (Array.isArray(o.providers)) return o.providers;
      if (Array.isArray(cfg.codexBar?.providers)) return cfg.codexBar.providers;
      return ['claude', 'codex', 'antigravity'];
    };

    const isOverviewEnabled = () => o.showOverview === true;

    const getItems = () => {
      const want = getProviders();
      const items = quotaProviders().filter((p) => want.includes(p.id));
      if (isOverviewEnabled() && D.quotas?.overview) {
        return [{ id: '__overview__', isOverview: true, overview: D.quotas.overview }, ...items];
      }
      return items;
    };

    const fmtCurrency = (amount, code = 'USD') => {
      const n = Number(amount || 0);
      const cur = (code || 'USD').toUpperCase();
      try { return new Intl.NumberFormat(LOCALE, { style: 'currency', currency: cur }).format(n); }
      catch (_) { return `${cur === 'USD' ? '$' : cur + ' '}${n.toFixed(2)}`; }
    };

    const renderOverviewCard = (ov, flexVal) => {
      const card = h('div', 'q-card q-overview-card');
      card.style.setProperty('--q', '#f97316');
      card.style.flex = flexVal;
      const days = ov.windowDays || 30;
      const age = ov.updatedAt ? Math.max(0, Date.now() - ov.updatedAt) : null;
      const updStr = age != null ? `checked ${age < 60_000 ? 'just now' : fmtDur(age) + ' ago'}` : '';
      card.appendChild(h('div', 'q-head',
        h('span', 'q-logo', '📊'),
        h('span', 'q-name', 'Overview'),
        h('span', 'q-plan', `${days}-day spend`),
        updStr ? h('span', 'q-upd', updStr) : null
      ));
      const summary = h('div', 'q-ov-hero',
        h('div', 'q-ov-stat', h('span', 'q-ov-lbl', 'Estimated spend'), h('strong', 'q-ov-val', fmtCurrency(ov.totalCost, ov.currencyCode))),
        h('div', 'q-ov-stat', h('span', 'q-ov-lbl', 'Recorded tokens'), h('strong', 'q-ov-val', tokens(ov.totalTokens || 0)))
      );
      card.appendChild(summary);
      const details = h('div', 'q-details q-ov-details');
      for (const s of (ov.subscriptions || []).slice(0, 3)) {
        details.appendChild(h('div', 'q-detail',
          h('span', null, s.name),
          h('strong', null, `${tokens(s.tokens)} · ${fmtCurrency(s.cost, s.currencyCode || ov.currencyCode)}`)
        ));
      }
      for (const m of (ov.topModels || []).slice(0, 2)) {
        details.appendChild(h('div', 'q-detail q-breakdown',
          h('span', null, m.modelName || m.name),
          h('strong', null, fmtCurrency(m.cost, m.currencyCode || ov.currencyCode))
        ));
      }
      if (details.childNodes.length) card.appendChild(details);
      const count = ov.providerCount ?? (ov.subscriptions || []).length;
      card.appendChild(h('div', 'q-foot', h('span', null, `${count} ${count === 1 ? 'provider' : 'providers'}`), h('span')));
      return card;
    };

    const render = () => {
      if (!D.quotas) return;
      const want = getProviders();
      const list = getItems();
      grid.replaceChildren();
      if (!list.length) { grid.appendChild(h('div', 'empty', want.length ? 'Quota data is unavailable for the selected providers' : 'No quota providers enabled')); return; }

      const ps = getPageSize();
      const needsPaging = isRotateEnabled() && ps > 0 && list.length > ps;
      if (offset >= list.length) offset = 0;

      let visible = list;
      if (needsPaging) {
        visible = [];
        for (let i = 0; i < ps; i++) {
          visible.push(list[(offset + i) % list.length]);
        }
      }

      for (const p of visible) {
        if (p.isOverview) {
          grid.appendChild(renderOverviewCard(p.overview, needsPaging && ps === 2 ? '1 1 0' : '1.5'));
          continue;
        }
        const st = Q_STYLE[p.id] || { color: '#94a3b8', logo: '●' };
        const card = h('div', 'q-card' + (p.limits.length >= 4 ? ' q-card-compact' : ''));
        card.style.setProperty('--q', st.color);
        // width by content: a ring is one share, the Codex details box another, so a one-ring card is not squeezed
        // three rings sit two over one, so such a card is two rings wide
        const tri = p.limits.length === 3, cols = tri ? 2 : Math.max(1, p.limits.length);
        card.style.flex = needsPaging && ps === 2 ? '1 1 0' : String(cols + (p.id === 'codex' && p.codex ? 1.2 : 0) + 0.3);
        // and never narrower than its rings' labels need
        if (!(needsPaging && ps === 2) && p.limits.length < 4) card.style.minWidth = cols * (tri ? 132 : 104) + 30 + 'px';
        card.appendChild(h('div', 'q-head', h('span', 'q-logo', st.logo), h('span', 'q-name', p.name), h('span', 'q-plan', p.plan),
          p.updatedAt ? h('span', 'q-upd', fmtDur(Date.now() - p.updatedAt) + ' ago') : null));
        const compact = p.limits.length >= 4;
        const rings = h('div', 'q-rings' + (compact ? ' q-rings-grid' : p.limits.length === 3 ? ' q-rings-tri' : ''));
        for (const l of p.limits) {
          const pace = paceInfo(l);
          const dial = h('div', 'q-dial');
          if (compact) {
            dial.style.width = '86px';
            dial.style.height = '86px';
          }
          if (l.percent >= 95 || l.severity === 'critical') dial.style.setProperty('--qc', '#ef4444');
          else if (l.percent >= 80 || l.severity === 'warning') dial.style.setProperty('--qc', '#f59e0b');
          dial.innerHTML = ringSvg(l.percent, pace?.elapsed); // numbers only
          const pctEl = h('div', 'q-pct', String(Math.round(l.percent)), h('small', null, '%'));
          if (compact) pctEl.style.fontSize = 'calc(22px * var(--widget-font-scale, 1))';
          dial.appendChild(pctEl);
          rings.appendChild(h('div', 'q-ring', dial, h('div', 'q-label', l.label), p.id === 'codex' ? h('div', 'q-left', `${Math.max(0, Math.round(100 - l.percent))}% left`) : null, h('div', 'q-reset', ...String(fmtReset(l.resetsAt) || '').split(' · ').map((x) => h('span', null, x))),
            pace ? h('div', 'q-pace ' + pace.cls, pace.text) : null));
        }
        if (!p.limits.length) rings.appendChild(h('div', 'q-reset', 'No data'));
        card.append(rings);
        if (p.id === 'codex' && p.codex) {
          const c = p.codex;
          const details = h('div', 'q-details');
          if (c.model) details.append(h('div', 'q-detail', h('span', null, 'Latest local session'), h('strong', null, c.model + (c.effort ? ` · ${c.effort}` : ''))));
          if (c.totalTokens != null) details.append(h('div', 'q-detail', h('span', null, 'Session tokens'), h('strong', null, tokens(c.totalTokens))));
          if (c.totalInput != null || c.totalOutput != null) details.append(h('div', 'q-detail q-breakdown', `In ${tokens(c.totalInput)} · Out ${tokens(c.totalOutput)} · Cached ${tokens(c.totalCached)} · Reasoning ${tokens(c.totalReasoning)}`));
          if (c.lastInput != null && c.contextWindow) details.append(h('div', 'q-detail q-breakdown', `Last input ${tokens(c.lastInput)} / ${tokens(c.contextWindow)} context window`));
          if (details.childNodes.length) card.append(details);
        }
        card.append(h('div', 'q-foot', h('span', p.extra?.startsWith('Limit reached') ? 'q-alert' : null, p.extra || ''), p.error ? h('span', 'q-err', p.error) : h('span')));

        grid.appendChild(card);
      }

      if (needsPaging) {
        const dots = h('div', 'q-dots');
        for (let i = 0; i < list.length; i++) {
          const dot = h('span', 'q-dot' + (i === offset ? ' active' : ''));
          dot.onclick = (e) => {
            e.stopPropagation();
            offset = i;
            lastRotate = Date.now();
            render();
            haptic();
          };
          dots.appendChild(dot);
        }
        grid.appendChild(dots);
      }
    };

    grid.onclick = () => {
      const ps = getPageSize();
      const list = getItems();
      if (ps > 0 && list.length > ps) {
        offset = (offset + 1) % list.length;
        lastRotate = Date.now();
        render();
        haptic();
      }
    };

    /**
     * Checks whether an enclosing Smart Stack widget has paused rotation.
     */
    const isStackPaused = () => box.closest('.w-stack')?.dataset.stackPaused === '1';

    return {
      /**
       * Updates quota display and advances paged provider rotation on ticks.
       */
      update(kind) {
        if (kind === 'tick') {
          const sec = getRotateInterval();
          if (isStackPaused()) {
            lastRotate = Date.now();
          } else if (sec > 0 && Date.now() - lastRotate >= sec * 1000) {
            const ps = getPageSize();
            const list = getItems();
            if (ps > 0 && list.length > ps) {
              offset = (offset + 1) % list.length;
              lastRotate = Date.now();
              render();
            }
          }
          if (Date.now() - last > 30_000) { last = Date.now(); render(); }
          return;
        }
        last = Date.now();
        render();
      },
    };
  },
};

W.codexbar = W.quotas;

W.sessions = {
  needs: ['work', 'tick'],
  create(box) {
    const sum = h('span', 'w-sum ai-sum');
    const list = h('div', 's-list', h('div', 'empty', t('sess.waitingData')));
    const add = h('button', 'mini-btn sess-new', '+ New');
    add.type = 'button';
    add.addEventListener('click', () => openNewSession());
    // only the agents at work (and ones that just finished, to reply to) are cards; the idle rest fold behind this
    const more = h('button', 'mini-btn ai-more');
    more.type = 'button';
    let showIdle = store.get('sess-idle', false);
    more.addEventListener('click', () => { haptic(); showIdle = !showIdle; store.set('sess-idle', showIdle); draw(); });
    box.append(h('div', 'w-head', h('span', 's-label', t('w.sessions')), sum, more, add), list);
    box.style.setProperty('--c', '#d97757');
    box.classList.add('ai');
    // Claude sessions' model, tokens and context come from the usage reader (/api/usage "sessions")
    let usage = new Map(), usageAt = 0;
    const loadUsage = async () => {
      if (Date.now() - usageAt < 30_000) return;
      usageAt = Date.now();
      try { const u = await api('/usage'); usage = new Map((u?.sessions || []).map((x) => ['claude:' + x.id, x])); draw(); } catch (_) {}
    };
    const short = (n) => n >= 1e6 ? (n / 1e6).toFixed(1) + 'M' : n >= 1e3 ? Math.round(n / 1e3) + 'k' : String(n);
    const TOOL = { claude: 'Claude Code', codex: 'Codex', cursor: 'Cursor' };
    const RECENT = 10 * 60_000; // a session that finished this recently still counts as active
    const active = (s) => s.status !== 'idle' || Date.now() - s.since < RECENT;
    const modelName = (m) => {
      const x = /^claude-(\w+)-(\d+)-(\d+)/.exec(m || '');
      return x ? x[1][0].toUpperCase() + x[1].slice(1) + ' ' + x[2] + '.' + x[3] : m || '';
    };
    const tokens = (n) => n == null ? '—' : n >= 1e6 ? (n / 1e6).toFixed(1) + 'M' : n >= 1e3 ? (n / 1e3).toFixed(1) + 'k' : String(n);
    const statusText = (s) => s.status === 'busy' ? t('sess.working') : s.status === 'waiting' ? t('sess.waiting') : s.status === 'shell' ? t('sess.shell') : s.outcome === 'completed' ? t('sess.completed') : s.outcome === 'aborted' ? t('sess.aborted') : t('sess.idle');
    const ageText = (since) => Date.now() - since < 60_000 ? 'just now' : shortDur(Date.now() - since) + ' ago';
    // A Claude Code chat's title (the agent sends it only when Settings › Claude Code › "Show session titles on the phone"
    // is on) is the main label and the folder goes under it; without a title the folder is the label, as before.
    const titleOf = (s) => s.title || s.project || s.name;
    const folderOf = (s) => (s.title ? s.project : '');
    const openDetails = (s) => {
      haptic();
      const backdrop = h('div', 'session-backdrop');
      const panel = h('div', 'session-panel');
      panel.setAttribute('role', 'dialog');
      panel.setAttribute('aria-modal', 'true');
      const title = h('h2', null, titleOf(s));
      const close = h('button', 'session-close', icon('x'));
      close.setAttribute('aria-label', 'Close session details');
      const onKey = (e) => { if (e.key === 'Escape') dismiss(); };
      const dismiss = () => { backdrop.remove(); document.removeEventListener('keydown', onKey); panel._cleanup?.(); };
      close.addEventListener('click', dismiss);
      backdrop.addEventListener('click', (e) => { if (e.target === backdrop) dismiss(); });
      panel.append(h('div', 'session-panel-head', h('span', null, s.tool === 'codex' ? '◎ Codex' : s.tool === 'cursor' ? '◆ Cursor' : '✳ Claude Code'), close), title);
      const rows = [
        ['Folder', folderOf(s) || null],
        ['Status', statusText(s)],
        ['Started', s.startedAt ? ageText(s.startedAt) : null],
        ['Last activity', s.since ? ageText(s.since) : null],
        ['Current turn', s.status === 'busy' && s.turnStartedAt ? shortDur(Date.now() - s.turnStartedAt) : null],
        ['Model', s.model], ['Reasoning', s.effort],
        ['Session tokens', s.totalTokens != null ? tokens(s.totalTokens) : null],
        ['Input / output', s.inputTokens != null || s.outputTokens != null ? `${tokens(s.inputTokens)} / ${tokens(s.outputTokens)}` : null],
        ['Cached input', s.cachedInputTokens != null ? tokens(s.cachedInputTokens) : null],
        ['Reasoning output', s.reasoningTokens != null ? tokens(s.reasoningTokens) : null],
        ['Last input / context window', s.lastInputTokens != null && s.contextWindow ? `${tokens(s.lastInputTokens)} / ${tokens(s.contextWindow)}` : null],
        ['Recent tool', s.lastTool],
      ];
      // the facts: a small label over each value, four to a row, folded away under a summary line so the
      // conversation gets the room
      const facts = h('details', 'session-facts', h('summary', null, [statusText(s), s.model].filter(Boolean).join(' · ') || 'Details'),
        h('div', 'session-grid', rows.filter(([, value]) => value != null)
          .map(([label, value]) => h('div', 'session-cell', h('span', null, label), h('strong', null, value)))));
      if (s.tool === 'codex') facts.append(h('p', 'session-note', 'Token totals are for this local session, not your account allowance.'));
      panel.append(facts);
      panel.classList.add('has-chat');
      panel.append(...sessionChat(s, panel));
      panel.prepend(h('div', 'sheet-grab'));
      sheetDrag(panel, dismiss);
      backdrop.append(panel);
      document.body.append(backdrop);
      close.focus();
      document.addEventListener('keydown', onKey);
    };
    const stateOf = (s) => s.status === 'busy' ? ['Working', shortDur(Date.now() - (s.turnStartedAt || s.since))]
      : s.status === 'waiting' ? ['Needs you', shortDur(Date.now() - s.since)]
      : s.status === 'shell' ? ['Running', shortDur(Date.now() - s.since)]
      : [s.outcome === 'aborted' ? t('sess.aborted') : 'Done', ageText(s.since)];
    const card = (s) => {
      const u = usage.get(s.id);
      const model = modelName(u?.model || s.model);
      const tok = u?.tokens ?? s.totalTokens;
      const facts = s.status === 'waiting' && s.waitingFor ? [s.waitingFor]
        : [folderOf(s), model, tok != null ? short(tok) : '', u?.context ? short(u.context) + ' ctx' : ''].filter(Boolean);
      const [label, when] = stateOf(s);
      const row = h('button', 'sess ai-card ' + s.status + (active(s) ? '' : ' old'),
        h('span', 'tl ' + s.tool, s.tool === 'claude' ? '✳' : s.tool === 'cursor' ? '◆' : '◎', h('i', 'tl-st')),
        h('span', 'sess-main',
          h('span', 'ai-top', h('span', 'nm', titleOf(s)), h('span', 'ai-pill', label)),
          h('span', 'ai-bot', h('span', 'sess-meta', facts.join(' · ') || TOOL[s.tool] || s.tool), h('small', 'ai-when', when))),
        s.status === 'busy' || s.status === 'shell' ? h('i', 'ai-run') : null);
      row.type = 'button';
      row.addEventListener('click', () => openDetails(s));
      return row;
    };
    const draw = () => {
      const sessions = D.work?.sessions;
      if (!sessions) return;
      const counts = { waiting: 0, busy: 0, shell: 0 };
      sessions.forEach((s) => (counts[s.status] = (counts[s.status] || 0) + 1));
      const live = sessions.filter(active), idle = sessions.length - live.length;
      const chip = (cls, n, text) => n ? h('span', 'ai-chip ' + cls, h('i'), `${n} ${text}`) : null;
      sum.replaceChildren(...[chip('busy', counts.busy + counts.shell, 'working'), chip('waiting', counts.waiting, 'need you')].filter(Boolean));
      more.hidden = !idle;
      more.textContent = showIdle ? 'Hide idle' : `+${idle} idle`;
      const shown = (showIdle ? sessions : live).slice()
        .sort((a, b) => (active(b) - active(a)) || (S_ORDER[a.status] ?? 9) - (S_ORDER[b.status] ?? 9) || b.since - a.since);
      box.classList.toggle('ai-few', shown.length > 0 && shown.length <= 3); // few agents: full-width cards with room for every detail
      if (shown.length) { list.replaceChildren(...shown.map(card)); return; }
      const start = h('button', 'ai-start', '+ New session');
      start.type = 'button';
      start.addEventListener('click', () => openNewSession());
      list.replaceChildren(h('div', 'ai-empty', h('div', 'ai-empty-ic', h('span', 'tl claude', '✳'), h('span', 'tl codex', '◎')),
        h('b', null, 'No agents running'), h('span', null, idle ? `${idle} idle ${idle === 1 ? 'session' : 'sessions'} hidden` : 'Start one here or in your terminal'), start));
    };
    return {
      update(kind) {
        loadUsage();
        if (kind === 'tick' && !box.isConnected) return;
        draw();
      },
    };
  },
};

W.github = {
  needs: ['work'],
  create(box) {
    box.style.setProperty('--c', '#c9d1d9');
    const user = h('span', 'w-sum'), bell = h('span', 'gh-bell');
    const rc = h('b'), mc = h('b'), rev = h('div', 'gh-list'), mine = h('div', 'gh-list'), err = h('div', 'gh-err', 'Waiting for GitHub data…');
    box.append(h('div', 'w-head', h('span', 's-label', 'GitHub'), user, bell),
      h('div', 'gh-sec', h('span', null, 'Review requests'), rc), rev,
      h('div', 'gh-sec', h('span', null, 'My pull requests'), mc), mine, err);
    return {
      update() {
        const g = D.work?.github;
        if (!g) return;
        const rows = Math.max(1, Math.floor((box.clientHeight - 90) / 25));
        user.textContent = g.user ? '@' + g.user : '';
        bell.replaceChildren(icon('bell'), ` ${g.notifications >= 50 ? '50+' : g.notifications}`);
        rc.textContent = g.reviewRequests.length;
        mc.textContent = g.myPrs.length;
        const nRev = Math.min(g.reviewRequests.length || 1, Math.max(1, Math.floor(rows / 2)));
        rev.replaceChildren(...(g.reviewRequests.length ? g.reviewRequests.slice(0, nRev).map((p) => prRow(p, false)) : [h('div', 'gh-pr', t('w.noReviews'))]));
        mine.replaceChildren(...(g.myPrs.length ? g.myPrs.slice(0, Math.max(1, rows - nRev)).map((p) => prRow(p, true)) : [h('div', 'gh-pr', t('w.noPrs'))]));
        err.textContent = g.error ? 'GitHub: ' + g.error : '';
      },
    };
  },
};

W.processes = {
  needs: ['procs', 'state'],
  create(box, { o }) {
    const canKill = o.allowKill !== false;
    let sort = currentProcessesSort();
    const sum = h('span', 'p-sum');
    const seg = h('div', 'seg', h('button', sort === 'cpu' ? 'on' : null, 'CPU'), h('button', sort === 'mem' ? 'on' : null, 'RAM'));
    // who takes the CPU (or the memory): one bar split by the busiest apps, with a legend
    const share = h('div', 'p-share', h('div', 'p-share-bar'), h('div', 'p-share-key'));
    const grid = h('div', 'p-grid', h('div', 'empty', t('w.procsWait')));
    box.style.setProperty('--c', '#f472b6');
    box.classList.add('px');
    box.append(h('div', 'p-head', h('span', 's-label', t('w.processes')), sum, seg), share, grid);
    const els = new Map();
    const rowEl = (g) => {
      let e = els.get(g.name);
      if (e) return e;
      e = h('div', 'proc');
      e.innerHTML = '<div class="p-ico"></div><div class="p-info"><div class="p-title"><span class="p-t"></span><span class="p-count"></span><span class="p-lock"></span></div>' +
        '<div class="p-sub"></div><div class="p-bar"><i></i></div></div><div class="p-nums"><b></b><small></small></div><i class="p-meter"></i>';
      fillIcon(e.querySelector('.p-ico'), g);
      e.style.setProperty('--hue', monogramColor(g.title)); // the app's colour, the same as in the share bar
      e.addEventListener('click', () => { haptic(); openSheet(e._g, canKill); });
      els.set(g.name, e);
      return e;
    };
    const val = (g) => (sort === 'cpu' ? g.cpu : g.memMb);
    const fmtVal = (v) => (sort === 'cpu' ? v.toFixed(1) + '%' : fmtMem(v));
    const kpi = (label, value) => h('span', 'p-kpi', h('small', null, label), h('b', null, value));
    const render = () => {
      const data = D.procs;
      if (!data) return;
      if (!data.groups.length) { sum.textContent = ''; grid.replaceChildren(h('div', 'empty', t('w.measuring'))); return; }
      const cols = box.clientWidth > 460 ? 2 : 1;
      const rowH = document.body.dataset.theme === 'material' || document.body.dataset.theme === 'shadcn' ? 50 : 44;
      const showShare = box.clientHeight > 260;
      share.hidden = !showShare;
      // every app (the top 80), in a list that scrolls; rows keep their height
      grid.style.gridTemplateColumns = `repeat(${cols}, minmax(0, 1fr))`;
      grid.style.gridTemplateRows = '';
      grid.style.gridAutoRows = rowH - 8 + 'px';
      const sorted = [...data.groups].sort((a, b) => (sort === 'cpu' ? b.cpu - a.cpu || b.memMb - a.memMb : b.memMb - a.memMb));
      const list = sorted.slice(0, 80);
      const max = Math.max(0.1, ...list.map(val));
      const pc = D.state?.pc;
      sum.replaceChildren(...[
        pc?.cpu != null ? kpi('CPU', Math.round(pc.cpu) + '%') : null,
        pc?.ramTotalGb ? kpi('RAM', `${r1(pc.ramUsedGb)} / ${Math.round(pc.ramTotalGb)} GB`) : null,
        box.clientWidth > 520 ? kpi('Processes', String(data.total)) : null,
        box.clientWidth > 640 ? kpi('Apps', String(data.groupCount ?? data.groups.length)) : null,
      ].filter(Boolean));
      if (showShare) {
        // the top five by app name (helpers folded into their app), the rest as "Other"
        const byApp = new Map();
        for (const g of sorted) byApp.set(g.title, (byApp.get(g.title) || 0) + val(g));
        const apps = [...byApp].sort((x, y) => y[1] - x[1]);
        const total = apps.reduce((a, x) => a + x[1], 0) || 1;
        const top = apps.slice(0, 5), rest = total - top.reduce((a, x) => a + x[1], 0);
        const parts = [...top.map(([n, v]) => [n, v, monogramColor(n)]), ...(rest > 0 ? [['Other', rest, '#52525b']] : [])];
        share.firstChild.replaceChildren(...parts.map(([n, v, c]) => { const i = h('i'); i.style.flexGrow = String(v); i.style.background = c; i.title = n; return i; }));
        share.lastChild.replaceChildren(...parts.map(([n, v, c]) => {
          const k = h('span', 'p-key', h('i'), h('span', null, n), h('b', null, Math.round((v / total) * 100) + '%'));
          k.firstChild.style.background = c;
          return k;
        }));
      }
      if (grid.querySelector('.empty')) grid.replaceChildren();
      // an app shown more than once (VS Code's helpers): each row says which process it is
      const titles = new Map();
      for (const g of list) titles.set(g.title, (titles.get(g.title) || 0) + 1);
      const keep = new Set(list.map((g) => g.name));
      for (const [n, e] of els) if (!keep.has(n)) { e.remove(); els.delete(n); }
      for (const g of list) {
        const e = rowEl(g);
        e._g = g;
        e.querySelector('.p-t').textContent = g.title;
        e.querySelector('.p-count').textContent = g.count > 1 ? '×' + g.count : '';
        e.querySelector('.p-lock').replaceChildren(...(g.locked ? [icon('lock')] : []));
        const named = titles.get(g.title) > 1 || !g.title.toLowerCase().includes(g.name.toLowerCase()); // "code" under Visual Studio Code says nothing
        const subParts = [named ? g.name : '', g.count > 1 ? `${g.count} processes` : ''].filter((x) => x && x !== g.title);
        e.querySelector('.p-sub').textContent = subParts.join(' · ');
        const pct = (val(g) / max) * 100;
        e.querySelector('.p-bar i').style.width = pct + '%';
        e.style.setProperty('--w', pct.toFixed(1) + '%');
        e.querySelector('.p-nums b').textContent = fmtVal(val(g));
        e.querySelector('.p-nums small').textContent = sort === 'cpu' ? fmtMem(g.memMb) : g.cpu.toFixed(1) + '% CPU';
        grid.appendChild(e); // keeps the order
      }
    };
    const setSort = (s) => {
      sort = s === 'mem' ? 'mem' : 'cpu';
      [...seg.children].forEach((x, i) => x.classList.toggle('on', (i === 0 ? 'cpu' : 'mem') === sort));
      render();
    };
    registerProcessesSorter(setSort);
    [...seg.children].forEach((b, i) => b.addEventListener('click', () => {
      haptic();
      setSort(i === 0 ? 'cpu' : 'mem');
    }));
    return { update: render };
  },
};

// On / off keys light up from the agent's state (D.state.toggles: { id: true | false }).
export function applyToggles(root) {
  const t = D.state?.toggles || {};
  root.querySelectorAll('.key.toggle').forEach((b) => b.classList.toggle('on', t[b.dataset.id] === true));
}

const ACTION_STATE = { 'monitor-off': 'Turn the display off', monitoroff: 'Turn the display off', screenoff: 'Turn the display off', lock: 'Lock the Mac', sleep: 'Put the Mac to sleep' };

// a built-in action's line icon (the uploaded 3D pictures read as clip-art next to the real app icons)
const ACTION_ICON = { mute: 'volumeX', 'monitor-off': 'moon', monitoroff: 'moon', screenoff: 'moon', lock: 'lock', sleep: 'moon', playpause: 'play', search: 'search' };

function launcher(box, list, o) {
  // A full-page launcher: the apps with their real Mac icons, labelled, a dot on the running ones and a ring on the
  // one in front; the built-in actions (mute, screen off, lock …) as control tiles beside them.
  box.classList.add('ln');
  const apps = list.filter((s) => s.kind !== 'action'), acts = list.filter((s) => s.kind === 'action');
  const appKey = (s) => {
    // an app the Mac knows by bundle id shows its own icon, not the upload
    const b = keyButton(s.bundle ? { ...s, iconRev: null } : s);
    b.classList.add('ln-app');
    b.append(h('i', 'ln-dot'));
    if (s.bundle) {
      const img = new Image();
      img.alt = '';
      img.onload = () => { const ic = b.querySelector('.k-icon'); ic.replaceChildren(img); ic.classList.add('k-icon-img', 'ln-real'); };
      img.src = `${API}/system/apps/icon?bundle=${encodeURIComponent(s.bundle)}`;
    }
    return b;
  };
  const appKeys = apps.map(appKey);
  const actKeys = acts.map((s) => {
    const name = ACTION_ICON[s.id] || (/mute/i.test(s.id) ? 'volumeX' : /lock/i.test(s.id) ? 'lock' : /off|sleep/i.test(s.id) ? 'moon' : null);
    const b = keyButton(name ? { ...s, iconRev: null } : s);
    b.classList.add('ln-ctl');
    if (name) b.querySelector('.k-icon').replaceChildren(icon(name));
    b.append(h('span', 'ln-state'));
    return b;
  });
  const runSum = h('span', 'ln-sub');
  const grid = h('div', 'ln-grid', ...appKeys);
  grid.style.setProperty('--cols', String(Math.min(5, Math.max(3, Math.ceil(apps.length / 2)))));
  box.append(
    h('section', 'ln-apps', h('div', 'ln-head', h('span', 'ln-title', 'Apps'), runSum), grid),
    ...(acts.length ? [h('section', 'ln-ctls', h('div', 'ln-head', h('span', 'ln-title', 'Controls')), h('div', 'ln-ctl-list', ...actKeys))] : []));
  let running = null, at = 0, busy = false;
  const draw = () => {
    const tg = D.state?.toggles || {};
    acts.forEach((s, i) => {
      const on = tg[s.id] === true;
      const k = actKeys[i];
      k.classList.toggle('on', on);
      if (/mute/i.test(s.id)) k.querySelector('.k-icon').replaceChildren(icon(on ? 'volumeX' : 'volume'));
      k.querySelector('.ln-state').textContent = /mute/i.test(s.id) ? (on ? 'Muted' : 'Sound on')
        : s.toggle ? (on ? 'On' : 'Off') : (ACTION_STATE[s.id] || 'Tap to run');
    });
    const live = running ? new Map(running.map((a) => [a.bundleId, a])) : null;
    let n = 0;
    apps.forEach((s, i) => {
      const a = live && s.bundle ? live.get(s.bundle) : null;
      if (a) n++;
      appKeys[i].classList.toggle('running', !!a);
      appKeys[i].classList.toggle('front', !!a?.active);
    });
    runSum.textContent = live ? `${n} of ${apps.length} running` : '';
  };
  const load = async () => {
    if (busy || Date.now() - at < 3000 || !apps.some((s) => s.bundle)) return;
    busy = true;
    try { running = await api('/system/apps'); at = Date.now(); } catch (_) {} finally { busy = false; }
    draw();
  };
  draw();
  return { update() { draw(); load(); } };
}

W.shortcuts = {
  needs: ['state'],
  create(box, { o }) {
    const all = cfg.shortcuts || [];
    const ids = Array.isArray(o.ids) && o.ids.length ? o.ids : null;
    const list = ids ? ids.map((id) => all.find((s) => s.id === id)).filter(Boolean) : all;
    if (!list.length) {
      box.append(h('div', 'empty', all.length ? t('w.shortcutsMissing') : t('w.shortcutsWait')));
      return {};
    }
    // A full-page launcher: apps as a big icon grid that shows which are running, built-in actions (mute, screen off,
    // lock …) as tall buttons beside it. Smaller slots keep the plain key grid.
    // (the size is known only once the page is laid out, so the choice is made on the first update)
    const big = () => box.clientWidth >= 480 && box.clientHeight >= 240 && o.layout !== 'grid';
    const cols = Math.min(8, Math.max(1, o.columns || 4));
    const grid = h('div', 'deck-grid', ...list.map(keyButton));
    grid.style.gridTemplateColumns = `repeat(${Math.min(cols, list.length)}, 1fr)`;
    grid.style.gridTemplateRows = `repeat(${Math.ceil(list.length / cols)}, 1fr)`;
    let lx = null;
    if (big()) lx = launcher(box, list, o); else box.append(grid);
    return {
      update() {
        if (!lx && big()) { grid.remove(); lx = launcher(box, list, o); }
        if (lx) lx.update(); else applyToggles(box);
      },
    };
  },
};

W.button = {
  bare: true, needs: ['state'],
  create(box, { o }) {
    const s = (cfg.shortcuts || []).find((x) => x.id === o.id) || (cfg.shortcuts || [])[0];
    box.append(s ? keyButton(s, { cover: o.cover === true, label: o.showLabel !== false }) : h('div', 'empty', t('w.pickShortcut')));
    return { update: () => applyToggles(box) };
  },
};

W.worldclock = {
  needs: ['tick'],
  create(box, { o }) {
    const zones = (Array.isArray(o.zones) && o.zones.length ? o.zones : ['Tallinn=Europe/Tallinn', 'Istanbul=Europe/Istanbul', 'London=Europe/London', 'New York=America/New_York', 'Tokyo=Asia/Tokyo'])
      .map((z) => { const i = z.indexOf('='); return i > 0 ? [z.slice(0, i).trim(), z.slice(i + 1).trim()] : [z.split('/').pop().replace(/_/g, ' '), z.trim()]; });
    const items = zones.map(([name, tz]) => {
      let fmt = null;
      try { fmt = new Intl.DateTimeFormat('en-GB', { timeZone: tz, year: 'numeric', month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }); } catch (_) {}
      const t = h('div', 'wc-t', fmt ? '--:--' : '??'), s = h('div', 'wc-s', fmt ? '' : 'unknown zone');
      return { fmt, t, s, el: h('div', 'wc', h('div', 'wc-n', name), t, s) };
    });
    const row = h('div', 'wc-row', items.map((i) => i.el));
    box.append(row);
    let shown = -1;
    return {
      update() {
        // once a minute, and at once after ticks were missed (the page was not visible, the night screen was on)
        const minute = Math.floor(Date.now() / 60000);
        if (minute === shown) return;
        shown = minute;
        const now = new Date();
        const localUtc = Date.UTC(now.getFullYear(), now.getMonth(), now.getDate(), now.getHours(), now.getMinutes());
        row.classList.toggle('vert', box.clientHeight > box.clientWidth * 1.1);
        row.classList.toggle('h12', hour12());
        for (const it of items) {
          if (!it.fmt) continue;
          const p = Object.fromEntries(it.fmt.formatToParts(now).map((x) => [x.type, x.value]));
          const [hm, ap] = fmtClock(+p.hour % 24, +p.minute).split(' ');
          it.t.replaceChildren(hm, ap ? h('span', 'wc-ap', ' ' + ap) : '');
          const zoneUtc = Date.UTC(+p.year, +p.month - 1, +p.day, +p.hour, +p.minute);
          const diffH = Math.round((zoneUtc - localUtc) / 360000) / 10;
          const dayDiff = Math.round((Date.UTC(+p.year, +p.month - 1, +p.day) - Date.UTC(now.getFullYear(), now.getMonth(), now.getDate())) / 86400000);
          const day = dayDiff > 0 ? t('time.tomorrow') : dayDiff < 0 ? t('time.yesterday') : t('time.today');
          it.s.textContent = diffH === 0 ? day : `${day}, ${diffH > 0 ? '+' : '−'}${Math.abs(diffH)}h`;
        }
      },
    };
  },
};

W.calendar = {
  needs: ['tick'],
  create(box) {
    let shown = '';
    return {
      update() {
        const now = new Date();
        const key = now.toDateString();
        if (key === shown) return;
        shown = key;
        const y = now.getFullYear(), m = now.getMonth();
        const first = new Date(y, m, 1), days = new Date(y, m + 1, 0).getDate();
        const lead = (first.getDay() + 6) % 7; // Monday first
        const cells = ['M', 'T', 'W', 'T', 'F', 'S', 'S'].map((d, i) => h('span', 'cal-h' + (i > 4 ? ' we' : ''), d));
        const prevDays = new Date(y, m, 0).getDate();
        for (let i = 0; i < lead; i++) cells.push(h('span', 'cal-d out', prevDays - lead + 1 + i));
        for (let d = 1; d <= days; d++) {
          const wd = (lead + d - 1) % 7;
          cells.push(h('span', 'cal-d' + (d === now.getDate() ? ' today' : '') + (wd > 4 ? ' we' : ''), d));
        }
        const total = Math.ceil(cells.length / 7) * 7;
        for (let d = 1; cells.length < total; d++) cells.push(h('span', 'cal-d out', d));
        const grid = h('div', 'cal', cells);
        grid.style.gridTemplateRows = `auto repeat(${total / 7 - 1}, 1fr)`;
        box.replaceChildren(h('div', 'w-title', now.toLocaleDateString(LOCALE, { month: 'long', year: 'numeric' }),
          h('span', 'w-sub', 'Week ' + isoWeek(now))), grid);
      },
    };
  },
};

export function isoWeek(d) {
  const t = new Date(Date.UTC(d.getFullYear(), d.getMonth(), d.getDate()));
  t.setUTCDate(t.getUTCDate() + 4 - (t.getUTCDay() || 7));
  return Math.ceil(((t - Date.UTC(t.getUTCFullYear(), 0, 1)) / 86400000 + 1) / 7);
}

/**
 * A Pomodoro sequence from text such as "25 focus, 5 break, 25 focus, 15 long" ("focus 25", "25 min long break" and
 * "f / b / l" work too). Returns { steps, bad }: steps is null when the text is empty or not understood.
 */
export function parsePomodoro(text) {
  const kindOf = (k) => (/^l/i.test(k) ? 'long' : /^(b|s)/i.test(k) ? 'break' : 'focus');
  const KIND = '(focus|work|f|short\\s*break|break|b|long\\s*break|long|l)';
  const re = new RegExp(`^(?:${KIND}\\s*(\\d{1,3})\\s*(?:m|min|minutes?)?|(\\d{1,3})\\s*(?:m|min|minutes?)?\\s*${KIND})$`, 'i');
  const steps = [];
  for (const raw of String(text || '').split(/[,;\n]+/)) {
    const part = raw.trim();
    if (!part) continue;
    const m = part.match(re);
    const min = m ? +(m[2] || m[3]) : 0;
    if (!m || min < 1 || min > 600) return { steps: null, bad: part };
    steps.push({ kind: kindOf(m[1] || m[4]), ms: min * 60_000 });
    if (steps.length > 48) return { steps: null, bad: 'more than 48 steps' };
  }
  return { steps: steps.length ? steps : null, bad: null };
}

W.pomodoro = {
  needs: ['tick'],
  always: true, // the alarm goes off on whatever page is showing
  create(box, { o, page, w }) {
    const key = `pomo:${page.id}/${w.id}`;
    const minutes = (v, d, max) => Math.min(max, Math.max(1, +v || d)) * 60_000;
    const focus = { kind: 'focus', ms: minutes(o.focus, 25, 180) }, brk = { kind: 'break', ms: minutes(o.break, 5, 60) };
    const long = { kind: 'long', ms: minutes(o.longBreak, 15, 120) };
    const every = Math.min(12, Math.max(0, Math.floor(+o.longEvery || 0)));
    const rounds = Math.min(99, Math.max(0, Math.floor(+o.rounds || 0)));
    const custom = parsePomodoro(o.sequence);
    // the plan: the own sequence, or focus / break with a long break after every N focus sessions
    let plan = custom.steps;
    if (!plan) {
      plan = [];
      if (!every) plan.push(focus, brk);
      else for (let i = 0; i < every; i++) plan.push(focus, i === every - 1 ? long : brk);
    }
    const sig = plan.map((s) => `${s.kind[0]}${s.ms / 60_000}`).join(' ') + `/${rounds}`;
    const focusTotal = plan.filter((s) => s.kind === 'focus').length;
    const today = new Date().toDateString();
    let st = store.get(key, null);
    if (!st || typeof st !== 'object') st = { phase: 'focus', endsAt: null, left: plan[0].ms, count: 0, day: today };
    if (st.sig === undefined) {
      // state from before sequences (focus / break only): keep a running timer where it is
      st.step = st.phase === 'break' && plan.length > 1 ? 1 : 0;
      st.round = 0;
      st.sig = sig;
    } else if (st.sig !== sig) {
      // the sequence was changed in the settings: start it from the beginning
      Object.assign(st, { step: 0, round: 0, endsAt: null, left: plan[0].ms, sig });
    }
    if (!(st.step >= 0 && st.step < plan.length)) st.step = 0;
    if (st.day !== today) { st.count = 0; st.day = today; }
    const save = () => { st.phase = plan[st.step].kind; store.set(key, st); };
    save();
    const dial = h('div', 'g-dial'), time = h('div', 'g-val'), phase = h('div', 'g-label'), count = h('div', 'g-sub');
    const play = h('button', 'pb main', '▶'), reset = h('button', 'pb', '↺'), skip = h('button', 'pb', '⏭');
    dial.append(time);
    box.append(dial, phase, count, h('div', 'pomo-btns', reset, play, skip));
    let svg = null;
    const total = () => plan[st.step].ms;
    const remaining = () => (st.endsAt ? Math.max(0, st.endsAt - Date.now()) : st.left);
    /** Moves to the next step; returns true when the last round of the sequence is over (the timer then stops). */
    const advance = (manual) => {
      if (plan[st.step].kind === 'focus' && !manual) st.count++;
      st.step++;
      if (st.step >= plan.length) { st.step = 0; st.round = (st.round || 0) + 1; }
      st.left = total();
      const finished = rounds > 0 && st.round >= rounds;
      if (finished) st.round = 0;
      st.endsAt = manual || finished ? null : Date.now() + st.left;
      save();
      return finished;
    };
    const label = () => {
      const s = plan[st.step];
      if (s.kind === 'long') return t('w.longBreak');
      if (s.kind === 'break') return t('w.break');
      const n = plan.slice(0, st.step + 1).filter((x) => x.kind === 'focus').length;
      return focusTotal > 1 ? `${t('w.focus')} ${n}/${focusTotal}` : t('w.focus');
    };
    const render = () => {
      // "today" starts over at midnight also while the panel stays open
      const day = new Date().toDateString();
      if (st.day !== day) { st.count = 0; st.day = day; save(); }
      if (st.endsAt && remaining() <= 0) {
        const was = plan[st.step].kind;
        const finished = advance(false);
        buzz(finished ? '0,200,120,200,120,200,120,600' : '0,200,120,200,120,400');
        const next = plan[st.step].kind;
        toast(finished ? 'Sequence finished, well done'
          : next === 'long' ? 'Time for a long break' : next === 'break' ? 'Time for a break'
            : was === 'focus' ? 'Next focus session' : 'Back to focus', 'ok', '🍅 Pomodoro', 5);
      }
      const left = remaining();
      const kind = plan[st.step].kind;
      box.classList.toggle('brk', kind !== 'focus');
      box.classList.toggle('long', kind === 'long');
      box.classList.toggle('run', !!st.endsAt);
      if (svg) svg.remove();
      dial.insertAdjacentHTML('afterbegin', ringSvg(100 * (1 - left / total())));
      svg = dial.firstElementChild;
      time.textContent = fmtTime(left + 999);
      phase.textContent = label();
      const tomatoes = st.count ? '🍅'.repeat(Math.min(st.count, 6)) + (st.count > 6 ? ` ×${st.count}` : '') : t('w.noSessionsToday');
      count.textContent = custom.bad ? `Sequence not understood: "${custom.bad}"`
        : rounds > 1 ? `Round ${(st.round || 0) + 1}/${rounds} · ${tomatoes}` : tomatoes;
      play.textContent = st.endsAt ? '⏸' : '▶';
    };
    play.addEventListener('click', () => {
      haptic();
      if (st.endsAt && remaining() <= 0) {
        render();
        if (!st.endsAt) return;
      }
      if (st.endsAt) { st.left = remaining(); st.endsAt = null; } else st.endsAt = Date.now() + st.left;
      save(); render();
    });
    // ↺ restarts this step; pressed again on a step that has not started, it goes back to the start of the sequence
    reset.addEventListener('click', () => {
      haptic();
      if (!st.endsAt && st.left === total()) { st.step = 0; st.round = 0; }
      st.endsAt = null; st.left = total(); save(); render();
    });
    skip.addEventListener('click', () => { haptic(); advance(true); render(); });
    return { update: render };
  },
};

W.countdown = {
  needs: ['tick'],
  create(box, { o }) {
    const m = String(o.target || '').trim().match(/^(\d{4})-(\d{2})-(\d{2})(?:[ T](\d{1,2}):(\d{2})(?::(\d{2}))?)?$/);
    let target = NaN;
    if (m) {
      const [, year, month, day, hour = '0', minute = '0', second = '0'] = m;
      const date = new Date(0);
      date.setFullYear(+year, +month - 1, +day);
      date.setHours(+hour, +minute, +second, 0);
      if (date.getFullYear() === +year && date.getMonth() === +month - 1 &&
          date.getDate() === +day && +hour < 24 && +minute < 60 && +second < 60) {
        // the time is checked as numbers: one the clocks skip (spring forward) still counts, moved on like before
        target = date.getTime();
      }
    }
    const big = h('div', 'cd-v'), sub = h('div', 'cd-s');
    box.append(h('div', 'cd-l', o.label || 'Countdown'), big, sub);
    if (Number.isNaN(target)) { big.textContent = '—'; sub.textContent = t('w.setDateFormat'); return {}; }
    sub.textContent = new Date(target).toLocaleDateString(LOCALE, { weekday: 'short', day: 'numeric', month: 'short', year: 'numeric' }) + ', ' + fmtHM(new Date(target));
    return {
      update() {
        let d = target - Date.now();
        const past = d < 0;
        d = Math.abs(d);
        const days = Math.floor(d / 86400000), hrs = Math.floor((d % 86400000) / 3600000), mins = Math.floor((d % 3600000) / 60000), secs = Math.floor((d % 60000) / 1000);
        big.textContent = (past ? '+' : '') + (days ? `${days}d ${hrs}h` : hrs ? `${hrs}h ${pad(mins)}m` : `${mins}:${pad(secs)}`);
        box.classList.toggle('past', past);
      },
    };
  },
};

const NOTE_MAX = 500;

W.note = {
  bare: true, needs: [],
  create(box, { o, page, w }) {
    const paint = () => {
      const text = String(o.text ?? '');
      box.style.setProperty('--n', o.color || '#fbbf24');
      box.style.setProperty('--nf', text.length < 25 ? 15 : text.length < 70 ? 10.5 : text.length < 160 ? 8 : 6.5);
      box.replaceChildren(h('div', 'note-t', text));
    };
    paint();
    if (PREVIEW || String(w.id).includes(':')) return {};
    box.addEventListener('click', () => {
      if (!$('overview').hidden) return;
      haptic();
      const wrap = h('div', 'note-edit');
      wrap.onclick = (e) => { if (e.target === wrap) wrap.remove(); };
      const card = h('div', 'note-card');
      const ta = document.createElement('textarea');
      ta.className = 'note-ta';
      ta.value = String(o.text ?? '');
      let opened = cfgVersion;
      let unsure = null; // the text of a save that timed out: the agent may have stored it anyway
      ta.maxLength = NOTE_MAX;
      ta.rows = 6;
      const row = h('div', 'note-actions');
      const cancel = h('button', 'btn sm', t('w.note.cancel'));
      cancel.onclick = () => wrap.remove();
      const save = h('button', 'btn primary sm', t('w.note.save'));
      save.onclick = async () => {
        const submitted = ta.value;
        save.disabled = true;
        ta.disabled = true;
        try {
          const path = '/widget/' + encodeURIComponent(page.id) + '/' + encodeURIComponent(w.id)
            + '/note?text=' + encodeURIComponent(submitted) + '&version=' + encodeURIComponent(opened ?? '');
          const mark = configMark();
          const result = await api(path, 'POST', 8000);
          o.text = submitted;
          // a config read that started after this save already holds it (or something newer): leave it alone.
          // Otherwise the config the panel holds gets the text first, then the version, so an identical refresh keeps
          // it and a rebuild doesn't bring the old text back; reads that started before the save can't apply over it
          if (!configReadSince(mark)) {
            const saved = (cfg.pages || []).find((p) => p.id === page.id)?.widgets?.find((x) => x.id === w.id);
            if (saved) { saved.options = { ...(saved.options || {}), text: submitted }; store.set('cfg', cfg); }
            localWriteStored(result?.version);
          }
          paint();
          // a refresh while the editor was open rebuilt the page under it: draw the new widget from the updated config
          if (!box.isConnected) buildLayout();
          wrap.remove();
        } catch (error) {
          if (error?.status === 409) {
            // a save that timed out may have been stored: then this conflict is our own, so save again on top of it
            if (unsure != null) {
              const earlier = unsure;
              unsure = null;
              // read the agent's config without applying it: a rebuild now would replace this widget under the editor
              const current = await api('/config').catch(() => null);
              if (!wrap.isConnected) return; // the editor was closed meanwhile: nothing to save
              const now = (current?.config?.pages || []).find((p) => p.id === page.id)?.widgets?.find((x) => x.id === w.id)?.options?.text;
              if (now === earlier && current?.version != null) {
                opened = current.version;
                return save.onclick();
              }
            }
            wrap.remove();
            toast(t('w.note.changed'));
            return;
          }
          // no HTTP answer (a timeout or a dropped connection): the agent may still have stored it
          if (error?.status == null) unsure = submitted;
          save.disabled = false;
          ta.disabled = false;
          toast(t('w.note.failed'));
        }
      };
      row.append(cancel, save);
      card.append(ta, row);
      wrap.append(card);
      document.body.append(wrap);
      ta.focus();
    });
    return {};
  },
};

W.label = {
  bare: true, needs: [],
  create(box, { o }) {
    box.append(h('div', 'lbl-t', o.text || ''));
    return {};
  },
};

W.command = {
  needs: ['tick'],
  create(box, { o, page, w }) {
    const out = h('div', 'cmd-o', '…'), foot = h('div', 'cmd-f', '');
    box.append(h('div', 'w-title', o.title || en('Command')), out, foot);
    let last = 0, busy = false, res = null;
    const load = async (force) => {
      if (busy) return;
      busy = true;
      try {
        res = await api(`/widget/${encodeURIComponent(page.id)}/${encodeURIComponent(w.id)}${force ? '?force=true' : ''}`, 'GET', 4000);
        const text = res.output ?? '';
        out.textContent = text || (res.running ? t('w.running') : t('w.noOutput'));
        const short = text.length <= 14 && !text.includes('\n');
        out.classList.toggle('big', short);
        box.classList.toggle('bad', res.ok === false);
      } catch (_) { if (!res) out.textContent = online ? t('w.unavailable') : '—'; }
      busy = false;
    };
    box.addEventListener('click', () => { haptic(); last = Date.now(); load(true).then(() => setTimeout(() => load(false), 1500)); });
    return {
      update() {
        if (res?.at) foot.textContent = (res.running ? 'refreshing · ' : '') + ago(res.at) + (res.durationMs ? ` · ${Math.round(res.durationMs)} ms` : '');
        if (!online || Date.now() - last < (res?.running || !res?.at ? 2000 : 5000)) return;
        last = Date.now();
        load(false);
      },
    };
  },
};

W.push = {
  needs: ['push', 'tick'],
  create(box, { o }) {
    const key = o.key || '';
    let lastAt = -1;
    const render = () => {
      const item = D.push[key];
      if (!item) {
        if (lastAt === 0) return;
        lastAt = 0;
        box.style.removeProperty('--c');
        box.replaceChildren(h('div', 'w-title', o.title || key), h('div', 'push-empty', t('w.noDataYet'), h('code', null, `POST /api/push/${key}`)));
        return;
      }
      const ageEl = box.querySelector('.push-age');
      if (item.at === lastAt) { if (ageEl) ageEl.textContent = ago(item.at); return; }
      lastAt = item.at;
      const v = pushView(item);
      const lvl = levelOf(v);
      const c = v.color || LEVEL_COLOR[lvl] || '';
      if (c) box.style.setProperty('--c', c); else box.style.removeProperty('--c');
      const unit = v.unit ?? o.unit ?? '';
      const style = o.style && o.style !== 'auto' ? o.style : v.items ? 'list' : 'big';
      const title = h('div', 'w-title', (v.icon ? v.icon + ' ' : '') + (o.title || v.title || key), h('span', 'push-age w-sub', ago(item.at)));
      let body;
      if (style === 'list') {
        const items = v.items || (v.value != null ? [v.value] : []);
        body = h('div', 'push-list', items.slice(0, 30).map((x) => {
          if (x && typeof x === 'object') {
            const li = h('div', 'pl-i', h('span', 'pl-l', x.label ?? x.title ?? x.name ?? ''), h('span', 'pl-v', fmtValue(x.value ?? x.status ?? '')));
            const lc = LEVEL_COLOR[x.level || levelOf({ value: x.value ?? x.status })];
            if (lc) li.style.setProperty('--lc', lc);
            return li;
          }
          return h('div', 'pl-i', h('span', 'pl-l', fmtValue(x)));
        }));
      } else if (style === 'gauge') {
        const min = v.min ?? (typeof o.min === 'number' ? o.min : 0), max = v.max ?? (typeof o.max === 'number' ? o.max : 100);
        const num = typeof v.value === 'number' ? v.value : parseFloat(v.value);
        const pct = v.progress != null ? v.progress * 100 : Number.isFinite(num) ? ((num - min) / (max - min || 1)) * 100 : 0;
        const dial = h('div', 'g-dial');
        dial.innerHTML = ringSvg(pct);
        dial.appendChild(h('div', 'g-val', fmtValue(v.value ?? Math.round(pct)), h('small', null, unit)));
        box.style.setProperty('--q', c || 'var(--cpu)');
        body = h('div', 'push-gauge', dial, v.sub ? h('div', 'g-sub', v.sub) : null);
      } else {
        body = h('div', 'push-big', h('div', 'pv', fmtValue(v.value), unit ? h('small', null, unit) : null),
          v.sub ? h('div', 'ps', String(v.sub)) : null,
          v.progress != null ? h('div', 'track', h('i')) : null);
        const bar = body.querySelector('.track i');
        if (bar) bar.style.width = Math.max(0, Math.min(100, v.progress * 100)) + '%';
      }
      box.className = box.className.replace(/\bps-\w+/g, '') + ' ps-' + style;
      box.replaceChildren(title, body);
    };
    return { update: render };
  },
};

W.inbox = {
  needs: ['notes', 'tick'],
  create(box) {
    const list = h('div', 'inbox');
    const clear = h('button', 'mini-btn', 'Clear');
    clear.addEventListener('click', () => { haptic(); D.notes = []; store.set('notes', []); emit('notes'); });
    box.append(h('div', 'w-title', en('Notifications'), clear), list);
    let shown = '';
    return {
      update(kind) {
        const key = D.notes.length + ':' + (D.notes[0]?.at || 0) + ':' + Math.floor(Date.now() / 60000);
        if (key === shown && kind === 'tick') return;
        shown = key;
        list.replaceChildren(...(D.notes.length ? D.notes.map((n) => {
          const e = h('div', 'ib-i', h('div', 'ib-h', h('b', null, n.title || n.text), h('span', null, ago(n.at))), n.title && n.text ? h('div', 'ib-t', n.text) : null);
          e.style.setProperty('--lc', LEVEL_COLOR[n.level] || 'var(--dim)');
          return e;
        }) : [h('div', 'empty', t('w.notifyEmpty'))]));
      },
    };
  },
};

// ---------------------------------------------------------------- Claude usage
// Tokens and cost from the local Claude Code transcripts (GET /api/usage, refreshed by the agent every minute).
W.usage = {
  needs: ['state'],
  create(box, { o }) {
    box.style.setProperty('--c', '#d97757');
    box.classList.add('us');
    const head = h('div', 'w-head', h('span', 's-label', o.title || 'Claude usage'), h('span', 'w-sum'));
    const hero = h('div', 'us-hero');
    // today, hour by hour: an area chart with the peak marked and a dot on the current hour
    const area = h('div', 'us-area');
    const ticks = h('div', 'us-ticks');
    const today = h('div', 'us-today', h('div', 'us-cap', h('span', null, 'Today'), h('b', 'us-peak')), area, ticks);
    const bars = h('div', 'us-bars');
    const week = h('div', 'us-week', h('div', 'us-cap', h('span', null, 'Last 7 days'), h('b', 'us-wk')), bars);
    const projs = h('div', 'us-projs');
    box.append(head, hero, today, week, projs);
    let at = 0, busy = false;
    const gid = 'usg' + Math.random().toString(36).slice(2, 8); // two usage cards must not share a gradient id
    const fmtTok = (n) => n >= 1e9 ? (n / 1e9).toFixed(1) + 'B' : n >= 1e6 ? (n / 1e6).toFixed(1) + 'M' : n >= 1e3 ? Math.round(n / 1e3) + 'k' : String(n || 0);
    const usd = (v) => v == null ? '' : '$' + (v >= 100 ? Math.round(v).toLocaleString('en-US') : v.toFixed(2));
    const hourLabel = (hr) => hour12() ? `${hr % 12 || 12}${hr < 12 ? 'a' : 'p'}` : String(hr).padStart(2, '0');
    const smooth = (pts) => pts.map(([x, y], i) => {
      if (!i) return `M${x},${y}`;
      const [px, py] = pts[i - 1], mx = (px + x) / 2;
      return `C${mx},${py} ${mx},${y} ${x},${y}`;
    }).join('');
    const drawArea = (hourly) => {
      const now = new Date().getHours();
      const vals = hourly.slice(0, now + 1);
      const max = Math.max(1, ...vals);
      const X = (i) => (i / 23) * 100, Y = (v) => 100 - (v / max) * 88;
      const pts = vals.map((v, i) => [X(i), Y(v)]);
      const line = smooth(pts);
      area.innerHTML = `<svg viewBox="0 0 100 100" preserveAspectRatio="none" aria-hidden="true">
        <defs><linearGradient id="${gid}" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="var(--c)" stop-opacity=".45"/><stop offset="1" stop-color="var(--c)" stop-opacity="0"/></linearGradient></defs>
        <path class="us-grid" d="M0,34H100M0,67H100" vector-effect="non-scaling-stroke"/>
        <path d="${line}L${X(vals.length - 1)},100L0,100Z" fill="url(#${gid})"/>
        <path class="us-line" d="${line}" vector-effect="non-scaling-stroke"/></svg>`;
      const peak = vals.indexOf(Math.max(...vals));
      const dot = h('i', 'us-dot');
      dot.style.left = X(now) + '%'; dot.style.top = Y(vals[now] || 0) + '%';
      area.append(dot);
      box.querySelector('.us-peak').textContent = vals[peak] ? `Peak ${fmtTok(vals[peak])} at ${hourLabel(peak)}` : '';
      ticks.replaceChildren(...[0, 6, 12, 18, 23].map((hr) => { const e = h('span', null, hourLabel(hr)); e.style.left = X(hr) + '%'; return e; }));
    };
    const draw = (u) => {
      // the cost is what these tokens would cost at API prices, not what a Claude plan charges
      head.querySelector('.w-sum').textContent = `${usd(u.week?.costUsd)}${u.costPartial ? '+' : ''} this week at API prices`;
      const top = (u.models || [])[0]?.model;
      const m = /^claude-(\w+)-(\d+)-(\d+)/.exec(top || '');
      hero.replaceChildren(h('b', null, fmtTok(u.today?.tokens)), h('span', null, 'tokens today'),
        h('em', null, usd(u.today?.costUsd)), m ? h('small', 'us-model', m[1][0].toUpperCase() + m[1].slice(1) + ' ' + m[2] + '.' + m[3]) : null);
      today.hidden = !u.hourly;
      if (u.hourly) drawArea(u.hourly);
      const days = u.daily || [];
      const max = Math.max(1, ...days.map((d) => d.tokens || 0));
      box.querySelector('.us-wk').textContent = fmtTok(u.week?.tokens);
      bars.replaceChildren(...days.map((d, i) => {
        const day = new Date(d.date + 'T12:00:00').toLocaleDateString(LOCALE, { weekday: 'narrow' });
        const col = h('div', 'us-bar' + (i === days.length - 1 ? ' today' : ''), h('i'), h('span', null, day));
        col.querySelector('i').style.height = Math.max(4, (d.tokens / max) * 100) + '%';
        col.title = `${d.date}: ${fmtTok(d.tokens)} · ${usd(d.costUsd)}`;
        return col;
      }));
      const p = (u.projects || []).slice(0, 4), pmax = Math.max(1, ...p.map((x) => x.tokensWeek || 0));
      projs.replaceChildren(...p.map((x) => {
        const r = h('div', 'us-proj', h('span', null, x.name), h('b', null, fmtTok(x.tokensWeek)));
        r.style.setProperty('--w', ((x.tokensWeek || 0) / pmax) * 100 + '%');
        return r;
      }));
    };
    return {
      async update() {
        if (busy || Date.now() - at < 30000) return;
        busy = true;
        try { const u = await api('/usage'); if (u) { draw(u); at = Date.now(); } } catch (_) {} finally { busy = false; }
      },
    };
  },
};

// ---------------------------------------------------------------- Lyrics
// Spotify-style lyrics for what the computer is playing (YouTube, Spotify, Music …): the line being sung lit up and
// kept in view, past lines dimmed, a tap on a line seeks the song there. From lrclib.net, looked up by the phone.
W.lyrics = {
  needs: ['state', 'fast'],
  create(box, { o }) {
    box.classList.add('ly');
    const art = document.createElement('img'); art.className = 'ly-art'; art.alt = '';
    const title = h('b', 'ly-title'), sub = h('span', 'ly-sub');
    const expand = h('button', 'ly-btn', icon('expand')); expand.type = 'button'; expand.setAttribute('aria-label', 'Full screen');
    const scroller = h('div', 'ly-lines');
    const note = h('div', 'ly-note');
    // the song itself sits under the lyrics: artwork, title, artist, progress and the transport buttons
    const prog = h('i', 'ly-prog');
    const tbtn = (glyph, action, label) => {
      const b = h('button', 'ly-tb ' + action, glyph); b.type = 'button'; b.setAttribute('aria-label', label);
      b.addEventListener('click', async () => { haptic(); try { const r = await api('/media/' + action, 'POST'); if (r && 'media' in r) setMediaState(r.media); } catch (_) {} });
      return b;
    };
    const play = tbtn('', 'playpause', 'Play / pause');
    const svg = (d) => { const t = document.createElement('template'); t.innerHTML = `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="${d}"/></svg>`; return t.content.firstElementChild; };
    const prevB = tbtn('', 'prev', 'Previous'), nextB = tbtn('', 'next', 'Next');
    prevB.append(svg('M6 5h2v14H6zM20 5v14L9.5 12z')); nextB.append(svg('M16 5h2v14h-2zM4 5v14l10.5-7z'));
    const now = h('div', 'ly-now', art, h('div', 'ly-meta', title, sub, h('div', 'ly-track', prog)), prevB, play, nextB);
    box.append(h('div', 'ly-head', h('span', 'ly-label', 'Lyrics'), expand), scroller, note, now);
    expand.addEventListener('click', () => { haptic(); box.classList.toggle('ly-full'); document.body.classList.toggle('rm-fullscreen', box.classList.contains('ly-full')); });
    let key = null, lines = null, els = [], shown = -2, artId = -1, userAt = 0;
    scroller.addEventListener('scroll', () => { if (!scroller._auto) userAt = Date.now(); scroller._auto = false; }, { passive: true });
    const say = (text) => { note.textContent = text || ''; note.hidden = !text; };
    const render = (l, m) => {
      shown = -2; els = [];
      if (!l) { scroller.replaceChildren(); say('No lyrics found for this song'); return; }
      if (l.instrumental) { scroller.replaceChildren(); say('♪ Instrumental'); return; }
      say('');
      if (l.synced?.length) {
        lines = l.synced;
        els = lines.map((x) => {
          const e = h('p', 'ly-l' + (x.text ? '' : ' gap'), x.text || '♪');
          e.addEventListener('click', async () => {
            if (!m.canSeek && !media?.canSeek) return;
            haptic();
            try { await api('/media/seek?ms=' + x.at, 'POST'); if (media) { media.posMs = x.at; media.at = performance.now(); } userAt = 0; } catch (_) {}
          });
          return e;
        });
        scroller.replaceChildren(h('div', 'ly-pad'), ...els, h('div', 'ly-pad'));
        scroller.classList.remove('plain');
      } else {
        lines = null;
        scroller.replaceChildren(...String(l.plain).split('\n').map((t) => h('p', 'ly-l plain', t || ' ')));
        scroller.classList.add('plain');
      }
    };
    const follow = (i) => {
      if (i === shown) return;
      els[shown]?.classList.remove('now');
      els.forEach((e, k) => e.classList.toggle('past', k < i));
      els[i]?.classList.add('now');
      shown = i;
      if (Date.now() - userAt < 4000 || !els[i]) return; // the user is reading ahead: leave the scroll alone
      scroller._auto = true;
      scroller.scrollTo({ top: els[i].offsetTop - scroller.clientHeight * 0.38, behavior: 'smooth' });
    };
    return {
      async update(kind) {
        const m = media;
        if (kind === 'fast') {
          if (m && m.durMs > 0) prog.style.width = Math.min(100, (100 * (m.posMs + (m.playing ? performance.now() - m.at : 0))) / m.durMs) + '%';
          if (m) play.replaceChildren(svg(m.playing ? 'M7 5h4v14H7zM13 5h4v14h-4z' : 'M8 5v14l11-7z'));
          if (m && lines && els.length) follow(lineAt(lines, Math.min(m.durMs || Infinity, m.posMs + (m.playing ? performance.now() - m.at : 0))));
          return;
        }
        if (!m?.title) {
          if (key !== '') { key = ''; lines = null; scroller.replaceChildren(); title.textContent = 'Nothing playing'; sub.textContent = ''; art.removeAttribute('src'); say('Play a song on the computer to see its lyrics'); }
          return;
        }
        if (m.artId && m.artId !== artId) { artId = m.artId; art.src = `${API}/art?id=${m.artId}`; }
        const k = `${m.artist}|${m.title}`;
        if (k === key) return;
        key = k; lines = null;
        title.textContent = m.title; sub.textContent = [m.artist, m.app].filter(Boolean).join(' · ');
        scroller.replaceChildren(); say('Looking for lyrics…');
        const l = await lyricsFor(m);
        if (key === k) render(l, m);
      },
      destroy() { document.body.classList.remove('rm-fullscreen'); },
    };
  },
};
