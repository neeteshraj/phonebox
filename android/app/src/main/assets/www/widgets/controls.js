import { icon, API, PANEL_HEADERS, api, applyState, bridge, D, evSeq, h, en, haptic, toast, store, t } from '../main.js';
import { W } from '../registry.js';

let cached = null, at = 0, epoch = 0, pending = null;
export function invalidateDeckState() {
  epoch++;
  cached = null;
  at = 0;
  pending = null;
}
export async function deckState(force = false) {
  if (!force && cached && performance.now() - at < 650) return cached;
  if (!force && pending) return pending;
  const stamp = epoch;
  const task = api('/controls/state', 'GET', 15000).then(data => {
    if (stamp === epoch) { cached = data; at = performance.now(); }
    return data;
  }).finally(() => { if (pending === task) pending = null; });
  pending = task;
  return task;
}
export async function deckCommand(kind, values = {}) {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value != null) query.set(key, String(value));
  invalidateDeckState();
  const result = await api(`/controls/command/${encodeURIComponent(kind)}?${query}`, 'POST', 20000);
  if (!result.ok) throw new Error(result.error || 'Control is unavailable');
  invalidateDeckState();
  if (kind === 'action' || kind === 'sound-stop' || kind === 'media') {
    api('/state' + (evSeq != null ? '?ev=' + evSeq : '')).then(s => {
      if (s && (evSeq == null || s.evSeq == null || s.evSeq >= evSeq) && (!D.state?.ts || !s.ts || s.ts >= D.state.ts)) applyState(s);
    }).catch(() => {});
  }
  return result;
}
const fail = error => toast(error?.message || 'The computer did not answer', 'err');
const button = (label, click, name = label) => {
  const b = h('button', 'deck-button', label); b.type = 'button'; b.setAttribute('aria-label', name);
  b.onclick = async () => { haptic(); b.disabled = true; try { await click(); } catch (e) { fail(e); } finally { b.disabled = false; } };
  return b;
};
const selectedPlayer = (data, options) => {
  const chosen = options.player || store.get('control-player', '');
  return data.media?.find(p => p.id === chosen) || (!options.player ? data.media?.[0] : null);
};
function reconcile(root, nodes) {
  nodes.forEach((node, index) => { if (root.children[index] !== node) root.insertBefore(node, root.children[index] || null); });
  [...root.children].filter(node => !nodes.includes(node)).forEach(node => node.remove());
}
function empty(root, text) { root.replaceChildren(h('div', 'deck-empty', text)); }
function slider(label, send, max = 100) {
  const input = h('input', 'deck-slider'); input.type = 'range'; input.min = 0; input.max = max; input.step = 1;
  input.setAttribute('aria-label', label);
  const number = h('output', 'deck-value');
  let editing = false;
  // the track's filled part (style.css reads --p); min and max may be changed by the caller after creation
  const fill = () => { const lo = +input.min || 0, hi = +input.max || 100; input.style.setProperty('--p', (hi > lo ? Math.max(0, Math.min(100, (input.value - lo) / (hi - lo) * 100)) : 0) + '%'); };
  input.onpointerdown = () => { editing = true; };
  input.oninput = () => { number.textContent = input.value; fill(); };
  input.onchange = async () => { editing = false; try { await send(+input.value); } catch (e) { fail(e); } };
  // a touch that does not move the fader sends no "change": the fader must follow the computer again afterwards
  input.onpointerup = input.onpointercancel = input.onlostpointercapture = input.onblur = () => { editing = false; };
  return { input, number, set(value) { if (!editing && document.activeElement !== input) input.value = String(value); number.textContent = String(editing ? input.value : value); fill(); } };
}
function meter(value) { const e = h('div', 'deck-meter', h('i')); e.firstChild.style.transform = `scaleX(${Math.max(0, Math.min(1, value || 0))})`; return e; }

function audioView(box, options, mode) {
  const root = h('div', `deck-list ${options.layout === 'vertical' ? 'deck-vertical' : ''}`);
  box.append(root);
  const rows = new Map();
  return data => {
    if (!data.audio?.available) { empty(root, data.audio?.error || 'Audio is unavailable on this computer'); return; }
    let items = mode === 'apps' || mode === 'app' ? data.audio.sessions || [] : data.audio.devices || [];
    if (mode === 'mic') items = items.filter(d => d.direction === 'input');
    if (mode === 'output') items = items.filter(d => d.direction === 'output');
    if (options.device) items = items.filter(d => d.id === options.device);
    if (options.application) items = items.filter(d => d.name.toLowerCase().includes(options.application.toLowerCase()));
    if (mode === 'mic' && !options.device) items = items.filter(d => d.default);
    if (mode === 'balance') items = items.filter(d => d.direction === 'output' && d.channels >= 2 && (options.device || d.default));
    if (!items.length) { empty(root, mode === 'app' ? 'Start the selected application and play audio' : 'No matching audio devices'); return; }
    const nodes = items.map(item => {
      let row = rows.get(item.id);
      if (!row) {
        const el = h('div', 'deck-channel');
        const name = h('div', 'deck-channel-name', item.name);
        const signal = meter(item.peak);
        const kind = mode === 'app' || mode === 'apps' ? 'session' : 'device';
        const controls = slider(item.name + ' volume', value => deckCommand(kind, { id: item.id, value }));
        const mute = button('Mute', () => deckCommand(kind, { id: item.id, muted: !row.item.muted }), 'Mute ' + item.name);
        const balance = [];
        el.append(name, signal);
        if (mode === 'balance') {
          const left = slider('Left channel', value => deckCommand('device', { id: item.id, op: 'left', value }));
          const right = slider('Right channel', value => deckCommand('device', { id: item.id, op: 'right', value }));
          left.set(item.volume); right.set(item.volume);
          balance.push(left, right);
          el.append(h('label', '', 'L', left.input, left.number), h('label', '', 'R', right.input, right.number));
        } else if (mode !== 'meter') el.append(controls.input, h('div', 'deck-channel-foot', controls.number, mute));
        row = { el, name, signal, controls, mute, item, balance }; rows.set(item.id, row);
      }
      row.item = item; row.name.textContent = item.name;
      row.controls.set(item.volume); row.mute.textContent = item.muted ? t('w.unmute') : t('w.mute');
      row.controls.input.disabled = item.canControl === false; row.mute.disabled = item.canControl === false;
      row.balance.forEach((slider, channel) => { const value = item.channelVolumes?.[channel]; slider.input.disabled = value == null; slider.set(value || 0); if (value == null) slider.number.textContent = 'n/a'; });
      row.mute.setAttribute('aria-pressed', String(item.muted));
      row.el.classList.toggle('deck-muted', item.muted); row.signal.firstChild.style.transform = `scaleX(${Math.max(0, Math.min(1, item.peak || 0))})`;
      return row.el;
    });
    reconcile(root, nodes);
    for (const key of rows.keys()) if (!items.some(item => item.id === key)) rows.delete(key);
  };
}

function chooser(box, options, which) {
  const list = h('div', 'deck-list'); box.append(list);
  let signature = '';
  return data => {
    const items = which === 'media' ? data.media || [] : which === 'plans' ? data.powerPlans || [] : (data.audio?.devices || []).filter(d => d.direction === which);
    const role = options.role || 'all';
    const roleIndex = {console:0,media:1,communications:2}[role];
    const chosen = which === 'media' ? selectedPlayer(data, options)?.id : roleIndex != null && ['input','output'].includes(which)
      ? data.audio?.defaults?.[which+':'+roleIndex] : items.find(d => d.default || d.state === 'Active')?.id;
    const key = JSON.stringify([items.map(i => [i.id, i.name]), chosen]); if (key === signature) return; signature = key;
    if (!items.length) { empty(list, which === 'media' ? 'Start a media player on the computer' : 'No compatible devices found'); return; }
    list.replaceChildren(...items.map(item => {
      const b = button(item.name, async () => {
        if (which === 'media') store.set('control-player', item.id);
        else await deckCommand(which === 'plans' ? 'power-plan' : 'default', { id: item.id, op:role });
        signature = '';
      });
      b.classList.toggle('selected', item.id === chosen); b.setAttribute('aria-pressed', String(item.id === chosen)); return b;
    }));
  };
}

function hold(box, options, power = null) {
  const b = h('button', 'deck-hold', power ? `Hold: ${power}` : 'Hold to talk'); b.type = 'button';
  b.disabled = true;
  const status = h('div', 'deck-caption', power ? 'Hold for 2 seconds' : 'Release to restore the microphone'); box.append(b, status);
  let held = false, lease = null, timer = 0, renewal = 0, device = null;
  const end = async () => {
    held = false; clearTimeout(timer); clearInterval(renewal); b.classList.remove('held');
    const old = lease; lease = null;
    if (old && !power) try { await deckCommand('ptt', { op: 'end', lease: old }); } catch (_) { /* the server lease expires independently */ }
  };
  const begin = async event => {
    if (held || b.disabled) return;
    event.preventDefault(); held = true; b.classList.add('held');
    if (event.pointerId != null) try { b.setPointerCapture(event.pointerId); } catch (_) {}
    try {
      const result = await deckCommand(power ? 'power-arm' : 'ptt', { id: power || device, op: 'begin' });
      lease = result.lease;
      if (!held) { await end(); return; }
      if (power) timer = setTimeout(async () => {
        try { await deckCommand('power', { id: power, lease }); status.textContent = 'Power action scheduled'; } catch (e) { fail(e); }
        await end();
      }, 1700);
      else renewal = setInterval(() => deckCommand('ptt', { op: 'renew', lease }).catch(end), 1000);
    } catch (e) { fail(e); await end(); }
  };
  b.onpointerdown = begin; b.onpointerup = end; b.onpointercancel = end; b.onlostpointercapture = end;
  b.onkeydown = e => { if (!e.repeat && (e.key === ' ' || e.key === 'Enter')) begin(e); };
  b.onkeyup = end;
  const visibility = () => { if (document.hidden) end(); };
  const observer = typeof IntersectionObserver === 'function' ? new IntersectionObserver(entries => { if (entries.some(entry => !entry.isIntersecting)) end(); }) : null;
  observer?.observe(b);
  window.addEventListener('blur', end); document.addEventListener('visibilitychange', visibility);
  return {
    render(data) {
      device = options.device || data.audio?.devices?.find(d => d.direction === 'input' && d.default)?.id;
      b.disabled = power ? !data.allowPower : !device;
      if (b.disabled) status.textContent = power ? 'Enable power controls in desktop settings' : 'No microphone is available';
    },
    destroy() { end(); observer?.disconnect(); window.removeEventListener('blur', end); document.removeEventListener('visibilitychange', visibility); },
  };
}

function mediaView(box, options, mode) {
  const title = h('div', 'deck-track'), source = h('div', 'deck-caption'); box.append(title, source);
  let current = null;
  const call = (op, value) => current ? deckCommand('media', { id: current.id, op, value }) : Promise.reject(new Error('No media player selected'));
  const controls = h('div', 'deck-toolbar'); box.append(controls);
  let seek = null, shuffle = null, repeat = null;
  if (mode === 'seek') { seek = slider('Playback position', value => call('seek', value)); box.append(seek.input, seek.number); }
  else if (mode === 'modes') {
    shuffle = button('Shuffle', () => call('shuffle', current.shuffle ? 0 : 1));
    repeat = button('Repeat', () => call('repeat', current.repeat === 'None' ? 1 : current.repeat === 'Track' ? 2 : 0));
    controls.append(shuffle, repeat);
  } else controls.append(button('⏮', () => call('prev'), 'Previous track'), button('⏯', () => call('playpause'), 'Play or pause'), button('⏭', () => call('next'), 'Next track'));
  return data => {
    current = selectedPlayer(data, options);
    title.textContent = current?.title || t('w.noMediaSelected'); source.textContent = current?.name || 'Choose a source in Media sources';
    controls.querySelectorAll('button').forEach(b => b.disabled = !current);
    if (seek) { seek.input.max = Math.max(1, current?.duration || 1); seek.set(current?.position || 0); seek.number.textContent = `${Math.floor((current?.position || 0) / 1000)} / ${Math.floor((current?.duration || 0) / 1000)} s`; seek.input.disabled = !current?.canSeek; }
    if (shuffle) { shuffle.disabled = !current?.canShuffle; shuffle.setAttribute('aria-pressed', String(!!current?.shuffle)); }
    if (repeat) { repeat.disabled = !current?.canRepeat; repeat.textContent = 'Repeat: ' + (current?.repeat || 'None'); }
  };
}

const groups = {
  'app-dock': ['app', 'shortcut'], hotkeys: ['keys'], 'macro-pad': ['macro'], 'window-layouts': ['window-layout'],
  'project-launcher': ['project', 'app'], snippets: ['snippet', 'text'], presentation: ['presentation'],
  'display-profiles': ['display-profile'], 'focus-profile': ['focus', 'macro'],
  'meeting-controls': ['meeting'], 'discord-controls': ['discord'], 'meeting-profile': ['meeting', 'macro'],
  'task-runner': ['task'], 'dev-servers': ['server'], services: ['service'], 'workflow-runner': ['workflow'], 'script-pad': ['task', 'shortcut'],
  soundboard: ['sound'], radio: ['radio'], playlists: ['playlist'], 'audio-routing': ['audio-route'],
  'external-mixer': ['mixer', 'mixer-mute'], 'midi-pad': ['midi'], 'osc-pad': ['osc'],
};
function actionsView(box, options, type) {
  const grid = h('div', 'deck-actions'); box.append(grid); let signature = '';
  return data => {
    const permitted = groups[type] || [];
    let items = [...(data.actions || []), ...(data.shortcuts || []).map(s => ({ ...s, id: 'shortcut:' + s.id }))]
      .filter(item => permitted.includes(item.kind));
    if(type === 'focus-profile' || type === 'meeting-profile') items.push(...(data.profiles || []));
    if (options.ids?.length) items = items.filter(item => options.ids.includes(item.id));
    const key = JSON.stringify(items); if (key === signature) return; signature = key;
    if (!items.length) { empty(grid, 'Assign controls in the computer’s Control Deck settings'); return; }
    grid.replaceChildren(...items.map(item => {
      const label = h('span', '', item.name), state = h('small', '', item.state || 'Ready');
      const b = button('', async () => { await deckCommand(item.kind === 'profile' ? 'profile' : 'action', { id: item.id, op: item.kind === 'radio' ? 'toggle' : (type === 'app-dock' ? 'focus' : '') }); signature = ''; });
      b.disabled = item.available === false;
      b.append(label, state);
      if (type === 'dev-servers') return h('div', 'deck-task', b, button('Stop', () => deckCommand('action', { id: item.id, op: 'stop' }), 'Stop ' + item.name));
      if (type === 'soundboard') return h('div', 'deck-task', b, button('Stop', () => deckCommand('sound-stop', { id: item.id }), 'Stop ' + item.name));
      return b;
    }));
  };
}

function radioInitials(label) {
  const parts = (label || 'FM').replace(/\([^)]*\)/g, '').trim().split(/[\s\-_.]+/).filter(Boolean);
  if (!parts.length) return 'FM';
  const chars = (s, n) => Array.from(s).slice(0, n).join('');
  return parts.length > 1 ? (chars(parts[0], 1) + chars(parts[1], 1)).toUpperCase() : chars(parts[0], 2).toUpperCase();
}

function radioView(box, options) {
  const isCarousel = options.view === 'carousel';
  box.classList.add('deck-radio-widget', isCarousel ? 'is-carousel' : 'is-grid');
  const wrap = h('div', isCarousel ? 'radio-carousel' : 'radio-grid-wrap');
  const track = h('div', isCarousel ? 'radio-carousel-track' : 'radio-grid');
  let prevBtn = null, nextBtn = null;

  const syncNav = () => {
    if (!isCarousel) return;
    const max = Math.max(0, track.scrollWidth - track.clientWidth);
    const canScroll = max > 6;
    wrap.classList.toggle('can-scroll', canScroll);
    if (prevBtn) prevBtn.disabled = !canScroll || track.scrollLeft <= 4;
    if (nextBtn) nextBtn.disabled = !canScroll || track.scrollLeft >= max - 4;
  };

  if (isCarousel) {
    const stepScroll = (dir) => {
      haptic();
      const step = Math.max(130, Math.round(track.clientWidth * 0.72));
      track.scrollBy({ left: dir * step, behavior: 'smooth' });
    };
    prevBtn = h('button', 'radio-nav radio-nav-prev', '‹');
    prevBtn.type = 'button';
    prevBtn.setAttribute('aria-label', 'Previous stations');
    prevBtn.onclick = (e) => { e.stopPropagation(); stepScroll(-1); };

    nextBtn = h('button', 'radio-nav radio-nav-next', '›');
    nextBtn.type = 'button';
    nextBtn.setAttribute('aria-label', 'Next stations');
    nextBtn.onclick = (e) => { e.stopPropagation(); stepScroll(1); };

    track.addEventListener('scroll', syncNav, { passive: true });

    let startX = 0, startY = 0;
    track.addEventListener('touchstart', (e) => {
      if (e.touches.length !== 1) return;
      startX = e.touches[0].clientX;
      startY = e.touches[0].clientY;
    }, { passive: true });
    track.addEventListener('touchmove', (e) => {
      if (e.touches.length !== 1) return;
      const dx = e.touches[0].clientX - startX;
      const dy = e.touches[0].clientY - startY;
      if (Math.abs(dx) <= Math.abs(dy)) return;
      const max = Math.max(0, track.scrollWidth - track.clientWidth);
      if (max <= 1 || (track.scrollLeft <= 0 && dx > 0) || (track.scrollLeft >= max - 1 && dx < 0)) {
        if (e.cancelable) e.preventDefault();
      }
    }, { passive: false });

    let dragDown = false, dragged = false, dragStartX = 0, dragScrollLeft = 0;
    track.addEventListener('pointerdown', (e) => {
      if (e.pointerType !== 'mouse' || e.button !== 0) return;
      dragDown = true; dragged = false;
      dragStartX = e.clientX; dragScrollLeft = track.scrollLeft;
    });
    track.addEventListener('pointermove', (e) => {
      if (!dragDown) return;
      const dx = e.clientX - dragStartX;
      if (Math.abs(dx) > 6) {
        dragged = true;
        track.classList.add('is-dragging');
      }
      if (dragged) track.scrollLeft = dragScrollLeft - dx;
    });
    const endDrag = () => {
      if (!dragDown) return;
      dragDown = false;
      track.classList.remove('is-dragging');
      if (dragged) setTimeout(() => { dragged = false; }, 0);
    };
    track.addEventListener('pointerup', endDrag);
    track.addEventListener('pointercancel', endDrag);
    track.addEventListener('pointerleave', endDrag);
    track.addEventListener('click', (e) => {
      if (dragged) { e.preventDefault(); e.stopPropagation(); dragged = false; }
    }, true);
    track.addEventListener('wheel', (e) => {
      if (Math.abs(e.deltaY) > Math.abs(e.deltaX) && track.scrollWidth > track.clientWidth) {
        const max = Math.max(0, track.scrollWidth - track.clientWidth);
        const canLeft = e.deltaY < 0 && track.scrollLeft > 0;
        const canRight = e.deltaY > 0 && track.scrollLeft < max;
        if (canLeft || canRight) {
          e.preventDefault();
          track.scrollLeft += e.deltaY;
        }
      }
    }, { passive: false });

    wrap.append(prevBtn, track, nextBtn);
  } else {
    wrap.append(track);
  }
  box.append(wrap);

  const cards = new Map();
  let latestItems = [];
  let optimisticId = null;
  let optimisticUntil = 0;
  let lastScrolledPlayingId = '';
  let cmdSeq = 0;
  let commandBusy = false;

  const effectivePlayingId = () => {
    if (optimisticId !== null && performance.now() < optimisticUntil) return optimisticId;
    optimisticId = null;
    return latestItems.find(i => i.state === 'Playing')?.id || '';
  };

  const syncCardsState = (allowAutoScroll) => {
    const activeId = effectivePlayingId();
    for (const [id, entry] of cards.entries()) {
      const playing = id === activeId;
      const unavail = entry.item.available === false;
      const subState = unavail && entry.item.state ? entry.item.state : '';
      entry.el.classList.toggle('is-playing', playing);
      entry.el.setAttribute('aria-pressed', String(playing));
      entry.el.disabled = unavail;
      entry.el.title = subState || entry.item.name;
      entry.name.textContent = subState ? `${entry.item.name} · ${subState}` : entry.item.name;
      entry.fallback.textContent = radioInitials(entry.item.name);
      entry.el.setAttribute('aria-label', entry.item.name + (playing ? ' (Playing)' : subState ? ` (${subState})` : ''));
    }
    if (isCarousel && allowAutoScroll && activeId && activeId !== lastScrolledPlayingId) {
      const activeEntry = cards.get(activeId);
      if (activeEntry) {
        const firstMount = !lastScrolledPlayingId;
        lastScrolledPlayingId = activeId;
        requestAnimationFrame(() => {
          const target = Math.max(0, activeEntry.el.offsetLeft - (track.clientWidth - activeEntry.el.offsetWidth) / 2);
          track.scrollTo({ left: target, behavior: firstMount ? 'auto' : 'smooth' });
          syncNav();
        });
      }
    } else if (!activeId) {
      lastScrolledPlayingId = '';
    }
  };

  const filterItems = (actions) => {
    const list = (actions || []).filter(item => item.kind === 'radio');
    if (!options.ids?.length) return list;
    const byId = new Map(list.map(item => [item.id, item]));
    return options.ids.map(id => byId.get(id)).filter(Boolean);
  };

  return (data) => {
    const items = filterItems(data.actions);
    latestItems = items;
    if (!items.length) {
      cards.clear();
      empty(track, 'Assign radio streams in the computer’s Control Deck settings');
      syncNav();
      return;
    }
    const emptyEl = track.querySelector('.deck-empty');
    if (emptyEl) emptyEl.remove();

    const nodes = items.map(item => {
      let entry = cards.get(item.id);
      if (!entry) {
        const fallback = h('span', 'radio-fallback', radioInitials(item.name));
        const img = h('img', 'radio-logo');
        img.alt = '';
        img.loading = 'lazy';
        img.decoding = 'async';
        img.draggable = false;
        const eq = h('span', 'radio-eq', h('i'), h('i'), h('i'));
        eq.setAttribute('aria-hidden', 'true');
        const logoBox = h('div', 'radio-logo-box', fallback, img, eq);
        img.onload = () => logoBox.classList.add('has-logo');
        img.onerror = () => logoBox.classList.remove('has-logo');

        const name = h('span', 'radio-name', item.name);
        const el = h('button', 'radio-card');
        el.type = 'button';
        el.append(logoBox, name);

        entry = { el, img, name, fallback, logoBox, item, busy: false, logoKey: '' };
        el.onclick = async () => {
          if (commandBusy || entry.busy) return;
          haptic();
          const seq = ++cmdSeq;
          const wasPlaying = effectivePlayingId() === entry.item.id;
          optimisticId = wasPlaying ? '' : entry.item.id;
          optimisticUntil = performance.now() + 3000;
          lastScrolledPlayingId = optimisticId;
          syncCardsState(false);
          if (isCarousel && optimisticId) {
            const target = Math.max(0, el.offsetLeft - (track.clientWidth - el.offsetWidth) / 2);
            track.scrollTo({ left: target, behavior: 'smooth' });
          }
          commandBusy = true;
          entry.busy = true;
          try {
            await deckCommand('action', { id: entry.item.id, op: 'toggle' });
            const fresh = await deckState(true);
            if (seq !== cmdSeq) return;
            latestItems = filterItems(fresh.actions);
            for (const next of latestItems) { const c = cards.get(next.id); if (c) c.item = next; }
            optimisticId = null; optimisticUntil = 0;
            syncCardsState(false);
          } catch (e) {
            if (seq === cmdSeq) {
              optimisticId = null; optimisticUntil = 0;
              syncCardsState(false);
            }
            fail(e);
          } finally {
            commandBusy = false;
            entry.busy = false;
          }
        };
        cards.set(item.id, entry);
      } else {
        entry.item = item;
      }
      return entry.el;
    });

    reconcile(track, nodes);
    for (const [id, entry] of cards.entries()) {
      const rev = entry.item.rev || entry.item.name || '';
      const nextKey = `${id}:${rev}`;
      if (entry.logoKey !== nextKey) {
        entry.logoKey = nextKey;
        entry.logoBox.classList.remove('has-logo');
        entry.img.src = `${API}/controls/radio-logo?id=${encodeURIComponent(id)}&v=${encodeURIComponent(rev)}`;
      }
    }
    for (const id of [...cards.keys()]) {
      if (!items.some(i => i.id === id)) cards.delete(id);
    }
    syncCardsState(true);
    requestAnimationFrame(syncNav);
  };
}

function mixerView(box,options){
  const root=h('div','deck-list');box.append(root);const rows=new Map();
  return data=>{
    const items=(data.actions || []).filter(a=>['mixer','mixer-mute'].includes(a.kind)&&(!options.ids?.length || options.ids.includes(a.id)));
    if(!items.length){empty(root,'Configure Voicemeeter bindings in desktop settings');return;}
    reconcile(root,items.map(item=>{
      let row=rows.get(item.id);
      if(!row){
        const el=h('div','deck-channel'),label=h('div','deck-channel-name',item.name),state=h('div','deck-caption');
        el.append(label,state);row={el,label,state,item};
        if(item.kind==='mixer'){
          row.slider=slider(item.name+' gain',value=>deckCommand('action',{id:item.id,value}),12);row.slider.input.min=-60;
          el.append(row.slider.input,row.slider.number);
        }else {row.toggle=button('Toggle',()=>deckCommand('action',{id:item.id,value:Number(row.item.state)>0?0:1}));el.append(row.toggle);}
        rows.set(item.id,row);
      }
      row.item=item;row.label.textContent=item.name;row.state.textContent=item.available===false?item.state || t('w.notConnected'):'';
      if(row.slider){row.slider.input.disabled=item.available===false;row.slider.set(Number(item.state)||0);row.slider.number.textContent=item.available===false?'n/a':item.state+' dB';}
      if(row.toggle){row.toggle.disabled=item.available===false;row.toggle.textContent=Number(item.state)>0?t('w.disable'):t('w.enable');row.toggle.setAttribute('aria-pressed',String(Number(item.state)>0));}
      return row.el;
    }));
    for(const id of rows.keys())if(!items.some(i=>i.id===id))rows.delete(id);
  };
}

function listening(box) {
  const state = h('div', 'deck-caption', t('w.listeningOff')); let ctl = null, context = null, next = 0, check = 0;
  const stop = () => { ctl?.abort(); ctl = null; context?.close(); context = null; clearInterval(check); toggle.textContent = t('w.listen'); state.textContent = t('w.listeningOff'); };
  const toggle = button(t('w.listen'), async () => {
    if (ctl) { stop(); return; }
    const Audio = window.AudioContext || window.webkitAudioContext;
    if (!Audio || !window.ReadableStream) throw new Error('This WebView does not support live audio');
    context = new Audio(); await context.resume(); ctl = new AbortController(); toggle.textContent = t('w.stopListening');
    state.textContent = t('w.listeningOn');
    const own = ctl;
    check = setInterval(() => { const r = box.getBoundingClientRect(); if (document.hidden || r.right <= 0 || r.left >= innerWidth) stop(); }, 250);
    (async () => {
      try {
        const response = await fetch(API + '/controls/listen', { headers: PANEL_HEADERS, signal: own.signal });
        if (!response.ok || !response.body) throw new Error('Listening is unavailable');
        const reader = response.body.getReader(), decoder = new TextDecoder(); let text = '';
        while (ctl === own) {
          const chunk = await reader.read(); if (chunk.done) break;
          text += decoder.decode(chunk.value, { stream: true });
          if (text.length > 1e6) throw new Error('Invalid audio stream');
          let newline;
          while ((newline = text.indexOf('\n')) >= 0) {
            const packet = JSON.parse(text.slice(0, newline)); text = text.slice(newline + 1);
            if (!packet.data || !context) continue;
            const raw = Uint8Array.from(atob(packet.data), c => c.charCodeAt(0)); const view = new DataView(raw.buffer);
            const bytes = packet.bits / 8, frames = raw.length / bytes / packet.channels;
            if (!Number.isInteger(frames) || ![16, 32].includes(packet.bits)) throw new Error('Unsupported audio format');
            const buffer = context.createBuffer(packet.channels, frames, packet.rate);
            for (let channel = 0; channel < packet.channels; channel++) {
              const samples = buffer.getChannelData(channel);
              for (let frame = 0; frame < frames; frame++) {
                const offset = (frame * packet.channels + channel) * bytes;
                samples[frame] = packet.encoding === 'IeeeFloat' ? view.getFloat32(offset, true)
                  : packet.bits === 16 ? view.getInt16(offset, true) / 32768 : view.getInt32(offset, true) / 2147483648;
              }
            }
            const source = context.createBufferSource(); source.buffer = buffer; source.connect(context.destination);
            if (next < context.currentTime || next > context.currentTime + 0.5) next = context.currentTime + 0.06;
            source.start(next); next += buffer.duration;
          }
        }
      } catch (e) { if (!own.signal.aborted) fail(e); }
      finally { if (ctl === own) stop(); }
    })();
  });
  toggle.disabled = true;
  box.append(toggle, state);
  return { render(data) { toggle.disabled = !data.allowListening; if (!data.allowListening) state.textContent = 'Enable listening in desktop settings'; }, destroy: stop };
}

export function registerDeckWidget(type, title, mode = type) {
  W[type] = {
    needs: ['tick'],
    create(box, { o }) {
      box.classList.add('control-deck'); box.append(h('div', 'w-title', o.title || title));
      let render = null, component = null, alive = true, loading = false;
      if (['apps', 'app', 'mic', 'output', 'balance', 'meter'].includes(mode)) render = audioView(box, o, mode);
      else if(mode === 'external-mixer')render=mixerView(box,o);
      else if (mode === 'inputs' || mode === 'outputs') render = chooser(box, o, mode === 'inputs' ? 'input' : 'output');
      else if (mode === 'sources' || mode === 'plans') render = chooser(box, o, mode === 'sources' ? 'media' : 'plans');
      else if (mode === 'ptt') component = hold(box, o);
      else if (mode === 'listen') component = listening(box);
      else if (['remote', 'seek', 'modes'].includes(mode)) render = mediaView(box, o, mode);
      else if (mode === 'panic') box.append(button('Mute audio + microphones', () => deckCommand('panic')));
      else if (mode === 'desktops') box.append(button('← Desktop', () => deckCommand('desktop', { value: -1 })), button('Desktop →', () => deckCommand('desktop', { value: 1 })));
      else if (mode === 'system') box.append(button('Lock', () => deckCommand('lock')), button('Display off', () => deckCommand('monitor-off')));
      else if (mode === 'power') {
        const group = h('div', 'deck-power-grid'); box.append(group);
        const components = ['sleep', 'restart', 'shutdown'].map(action => { const item = h('div', 'deck-power-item'); group.append(item); return hold(item, o, action); });
        component = { render(data) { components.forEach(c => c.render(data)); }, destroy() { components.forEach(c => c.destroy()); } };
      } else if (mode === 'duck') {
        let enabled = false; const b = button('Enable ducking', () => deckCommand('duck', { op: enabled ? 'off' : 'on', value: o.level ?? 25 })); box.append(b);
        render = data => { enabled = data.ducking; b.textContent = enabled ? t('w.disableDucking') : t('w.enableDucking'); b.setAttribute('aria-pressed', String(enabled)); };
      } else if (mode === 'profiles') {
        const list = h('div', 'deck-actions'); box.append(list); let key = '';
        render = data => { const next = JSON.stringify(data.profiles); if (next === key) return; key = next;
          if (!data.profiles?.length) empty(list, 'Save an audio profile in desktop settings');
          else list.replaceChildren(...data.profiles.map(p => button(p.name, () => deckCommand('profile', { id: p.id })))); };
      } else if (mode === 'sleep') {
        let player = null; const caption = h('div', 'deck-caption');
        box.append(button(`Sleep in ${o.minutes || 30} min`, () => deckCommand('sleep', { id: player?.id || '', value: o.minutes || 30 })),
          button('Cancel timer', () => deckCommand('sleep', { id: player?.id || '', value: 0 })), caption);
        render = data => { player = selectedPlayer(data, o); const timer = data.sleep?.find(t => t.id === player?.id);
          caption.textContent = timer ? `Pausing ${player?.name || 'player'} in ${Math.max(0, Math.ceil((timer.at - Date.now()) / 60000))} min` : t('w.noSleepTimer'); };
      } else if (mode === 'brightness') {
        const list = h('div', 'deck-list'); box.append(list); const rows = new Map();
        render = data => {
          const items = (data.monitors || []).filter(m => !o.device || m.id === o.device);
          if (!items.length) { empty(list, t('w.noMonitors')); return; }
          reconcile(list, items.map(m => { if (!rows.has(m.id)) { const s = slider(m.name + ' brightness', value => deckCommand('brightness', { id: m.id, value }));
            rows.set(m.id, { ...s, el: h('div', 'deck-channel', h('div', 'deck-channel-name', m.name), s.input, s.number) }); }
            const row = rows.get(m.id); row.input.disabled = m.brightness == null; row.set(m.brightness || 0); if (m.brightness == null) row.number.textContent = 'DDC/CI unavailable'; return row.el; }));
        };
      } else if (mode === 'radio') render = radioView(box, o);
      else render = actionsView(box, o, type);
      return {
        async update() {
          if (loading || !alive || (!render && !component)) return;
          loading = true;
          try { const data = await deckState(); if (alive) { render?.(data); component?.render(data); box.classList.remove('deck-offline'); } }
          catch (_) { if (alive) box.classList.add('deck-offline'); }
          finally { loading = false; }
        },
        destroy() { alive = false; component?.destroy(); },
      };
    },
  };
}

for (const [type, title, mode] of [
  ['audio-mixer', 'Application mixer', 'apps'], ['app-volume', 'Application volume', 'app'],
  ['device-volume', 'Device volume', 'output'], ['audio-balance', 'Channel balance', 'balance'], ['audio-meter', 'Audio levels', 'meter'],
  ['panic-mute', 'Emergency mute', 'panic'], ['microphone', 'Microphone', 'mic'], ['audio-inputs', 'Microphone selection', 'inputs'],
  ['audio-outputs', 'Output selection', 'outputs'], ['push-to-talk', 'Push to talk', 'ptt'], ['audio-profiles', 'Audio profiles', 'profiles'],
  ['audio-ducking', 'Audio ducking', 'duck'], ['media-sources', 'Media sources', 'sources'], ['media-remote', 'Media remote', 'remote'],
  ['media-seek', 'Playback position', 'seek'], ['media-modes', 'Shuffle and repeat', 'modes'], ['media-sleep', 'Music sleep timer', 'sleep'],
  ['brightness', 'Display brightness', 'brightness'], ['power-plans', 'Power plans', 'plans'], ['desktops', 'Virtual desktops', 'desktops'],
  ['system-keys', 'Lock and displays', 'system'], ['power-controls', 'Power controls', 'power'], ['pc-listen', 'Listen to PC audio', 'listen'],
]) registerDeckWidget(type, title, mode);
for (const type of Object.keys(groups)) registerDeckWidget(type, type.split('-').map(s => s[0].toUpperCase() + s.slice(1)).join(' '));

W['meeting-status'] = { needs: ['state', 'push'], create: (box, ctx) => W.onair.create(box, ctx) };

async function integrationCommand(path, values = {}) {
  const result = await api('/controls/' + path + '?' + new URLSearchParams(values), 'POST', 20000);
  if (!result.ok) throw new Error(result.error || 'Integration refused this control');
  return result;
}
function integrationWidget(type, title, endpoint, create) {
  W[type] = { needs: ['tick'], create(box, {o}) {
    box.classList.add('control-deck'); box.append(h('div', 'w-title', o.title || title));
    const root = h('div', 'deck-list'); box.append(root);
    let alive = true, busy = false, signature = '', data = null;
    return { async update() {
      if (!alive || busy) return; busy = true;
      try {
        data = await api('/controls/' + endpoint, 'GET', 20000); if (!alive) return;
        if (root.contains(document.activeElement) && document.activeElement.matches('input,textarea,select') || root.matches(':active') || root.querySelector(':active')) return;
        const key = JSON.stringify(data); if (key === signature) return; signature = key;
        if (!data.connected) { empty(root, data.error || 'Connect this integration in desktop settings'); return; }
        root.replaceChildren();
        create(root, data, (action, args) => integrationCommand(endpoint === 'obs' ? 'obs/' + action : 'spotify/action/' + action, args).then(()=>{signature='';}), o);
      } catch (error) { if (alive) empty(root, error.message || 'Integration is unavailable'); }
      finally { busy = false; }
    }, destroy() { alive = false; } };
  } };
}
const timeText = ms => { const s = Math.floor((ms || 0) / 1000); return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0'); };
integrationWidget('obs-mixer', 'OBS mixer', 'obs', (root, data, send) => {
  if (!data.inputs?.length) { empty(root, 'No OBS inputs found'); return; }
  for (const input of data.inputs) {
    const s = slider(input.name + ' volume', value => send('volume', {id:input.id,value}));
    s.set(input.volume || 0); s.input.disabled = input.volume == null;
    const mute = button(input.muted ? 'Unmute' : 'Mute', ()=>send('mute',{id:input.id,enabled:!input.muted}), 'Mute ' + input.name);
    mute.disabled = input.muted == null;
    root.append(h('div','deck-channel',h('div','deck-channel-name',input.name),s.input,h('div','deck-channel-foot',s.number,mute)));
  }
});
integrationWidget('obs-scenes', 'OBS scenes', 'obs', (root, data, send) => {
  for (const name of data.scenes || []) { const b=button(name,()=>send('scene',{id:name}));b.classList.toggle('selected',name===data.scene);root.append(b); }
});
integrationWidget('obs-sources', 'OBS sources', 'obs', (root, data, send) => {
  if (!data.sources?.length) empty(root,'No sources in the current scene');
  for(const source of data.sources || []) { const b=button((source.enabled?'● ':'○ ')+source.name,()=>send('source',{id:source.id,enabled:!source.enabled}));b.setAttribute('aria-pressed',String(source.enabled));root.append(b); }
});
integrationWidget('obs-recording', 'OBS recording and stream', 'obs', (root,data,send)=>{
  root.append(h('div','deck-track','Recording '+timeText(data.recordMs)),h('div','deck-caption','Stream '+timeText(data.streamMs)),
    button(data.recording?'Stop recording':'Start recording',()=>send('record')),
    button(data.streaming?'Stop stream':'Start stream',()=>send('stream')));
  const pause=button(data.recordPaused?'Resume recording':'Pause recording',()=>send('pause'));pause.disabled=!data.recording;root.append(pause);
});
integrationWidget('obs-replay', 'OBS replay buffer', 'obs', (root,data,send)=>{
  const toggle=button(data.replayActive?'Stop replay buffer':'Start replay buffer',()=>send('replay'));toggle.disabled=!data.replayAvailable;
  const save=button('Save replay',()=>send('save-replay'));save.disabled=!data.replayActive;root.append(toggle,save);
});
integrationWidget('obs-transition', 'OBS transition', 'obs', (root,data,send)=>{
  for(const t of data.transitions || []) {const b=button(t.transitionName,()=>send('transition',{id:t.transitionName}));b.classList.toggle('selected',data.transition===t.transitionName);root.append(b);}
  const s=slider('Transition duration',value=>send('duration',{value}),10000);s.input.min=50;s.input.step=50;s.set(data.duration || 300);s.input.disabled=data.duration==null;root.append(s.input,s.number);
});
integrationWidget('obs-camera', 'OBS virtual camera', 'obs', (root,data,send)=>{
  const b=button(data.cameraActive?'Stop virtual camera':'Start virtual camera',()=>send('camera'));b.disabled=!data.cameraAvailable;root.append(b);
});
// Spotify's image server only (the agent passes nothing else); the phone loads covers itself while the option is on.
const SPOTIFY_COVER = /^https:\/\/i\.scdn\.co\/image\/[A-Za-z0-9]+$/;
integrationWidget('media-queue', 'Spotify queue', 'spotify/state', (root,data,send,o)=>{
  const cover = (url) => {
    if (o.covers === false) return null; // no cover column at all: more room for the titles
    const c = h('span','sq-cover');
    if (SPOTIFY_COVER.test(url || '')) {
      const img = h('img'); img.alt = ''; img.decoding = 'async'; img.referrerPolicy = 'no-referrer';
      img.onerror = () => img.remove(); img.src = url; c.append(img);
    }
    return c;
  };
  const text = (name, artist) => h('div','sq-txt', h('div','sq-name',name || ''), artist ? h('div','sq-artist',artist) : null);
  const cur = data.current;
  root.classList.add('sq');
  root.append(h('div','sq-now' + (data.playing ? ' playing' : ''), cover(cur?.image),
    h('div','sq-txt', h('div','sq-label', t(cur ? 'w.nowPlaying' : 'w.nothingPlaying')),
      cur ? h('div','sq-name',cur.name || '') : null, cur?.artist ? h('div','sq-artist',cur.artist) : null)));
  if (!data.queue?.length) { root.append(h('div','deck-empty',t('w.queueEmpty'))); return; }
  root.append(h('div','sq-head',t('w.upNext')));
  for (const item of data.queue) {
    const b = h('button','sq-item', cover(item.image), text(item.name, item.artist));
    b.type = 'button';
    b.setAttribute('aria-label', item.name + (item.artist ? ' · ' + item.artist : ''));
    b.onclick = () => { haptic(); send('play-queued',{id:item.id}).catch(fail); };
    root.append(b);
  }
});
integrationWidget('media-favorites', 'Spotify favorites', 'spotify/state', (root,data,send)=>{
  root.append(h('div','deck-track',data.current?.name || t('w.nothingPlaying')),h('div','deck-caption',data.current?.artist || ''));
  const b=button('Save to library',()=>send('favorite'));b.disabled=!data.current?.id;root.append(b);
});
W['meeting-timer'] = {needs:['tick'],create(box,{o}){
  const key='meeting-timer:'+ (o.timerId || 'default');
  const clock=h('div','deck-track');box.classList.add('control-deck');box.append(h('div','w-title',o.title || en('Meeting timer')),clock);
  let state=store.get(key,{elapsed:0,started:0});
  const persist=()=>store.set(key,state);
  const start=button(state.started?'Pause':'Start',()=>{state=store.get(key,state);if(state.started){state.elapsed+=Date.now()-state.started;state.started=0;}else state.started=Date.now();persist();render();});
  box.append(start,button('Reset',()=>{state={elapsed:0,started:0};persist();render();}));
  function render(){state=store.get(key,state);const elapsed=state.elapsed+(state.started?Date.now()-state.started:0);clock.textContent=timeText(elapsed);start.textContent=state.started?t('w.pause'):t('w.start');start.setAttribute('aria-label',start.textContent);clock.classList.toggle('deck-overdue',elapsed>=(o.minutes || 30)*60000);}
  render();return {update:render};
}};

W.trackpad = {
  needs: [],
  create(box, { o }) {
    box.classList.add('control-deck');
    const controlEnabled = Boolean(o.control);
    const sensitivity = Number(o.sensitivity) || 1.0;
    const acceleration = o.acceleration !== false;
    const scrollSpeed = Number(o.scrollSpeed) || 1.0;
    const natural = Boolean(o.naturalScroll);
    const holdDuration = Math.max(100, Number(o.holdDuration) || 350);
    const scrollBar = o.scrollBar || 'right';
    const scrollBarWidth = Math.max(16, Number(o.scrollBarWidth) || 36);

    const surface = h('div', 'deck-trackpad-surface',
      h('div', 'deck-trackpad-icon', controlEnabled ? '🖱️' : '🔒'),
      h('div', 'deck-trackpad-hint', !controlEnabled
        ? 'Enable mouse control in widget settings'
        : (scrollBar !== 'none'
          ? 'Drag to move · Tap to click · Hold to drag · Bar or 2 fingers to scroll'
          : 'Drag to move · Tap to click · Hold to drag · Two fingers to scroll'))
    );

    let scrollBarEl = null;
    if (controlEnabled && scrollBar !== 'none') {
      scrollBarEl = h('div', 'deck-trackpad-bar',
        h('div', 'deck-trackpad-bar-icon',
          h('span', null, '▲'),
          h('span', null, '▼')
        )
      );
      scrollBarEl.style.width = scrollBarWidth + 'px';
    }

    const pad = h('div', 'deck-trackpad' + (scrollBar === 'left' ? ' has-scroll-left' : ''), surface);
    pad.setAttribute('tabindex', '0');
    pad.setAttribute('role', 'region');
    pad.setAttribute('aria-label', o.title || 'Trackpad');
    if (scrollBarEl) pad.append(scrollBarEl);
    box.append(h('div', 'w-title', o.title || 'Trackpad'), pad);

    if (!controlEnabled) {
      pad.classList.add('is-disabled');
      return { update() {}, destroy() {} };
    }

    let ws = null, destroyed = false, reconnectTimer = null;
    function getWsUrl() {
      const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
      if (location.host && !location.host.includes('androidplatform.net')) return `${proto}//${location.host}/api/trackpad`;
      return 'ws://127.0.0.1:8765/api/trackpad';
    }
    function connectWs() {
      if (destroyed || (bridge && typeof bridge.trackpad === 'function')) return;
      try {
        ws = new WebSocket(getWsUrl());
        ws.onclose = () => { if (!destroyed) reconnectTimer = setTimeout(connectWs, 2000); };
        ws.onerror = () => { try { ws.close(); } catch (_) {} };
      } catch (_) {
        if (!destroyed) reconnectTimer = setTimeout(connectWs, 2000);
      }
    }
    connectWs();

    function send(msg) {
      if (ws && ws.readyState === WebSocket.OPEN) {
        ws.send(JSON.stringify(msg));
      } else if (bridge && typeof bridge.trackpad === 'function') {
        bridge.trackpad(msg.type || '', msg.dx || 0, msg.dy || 0, msg.delta ?? msg.dy ?? 0);
      } else {
        fetch('/api/trackpad', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(msg),
          cache: 'no-store'
        }).catch(() => {});
      }
    }

    // Windows Precision Touchpad ballistics curve: 3.5x base gain with controlled +1.6x dynamic flick boost
    function getGain(v) {
      const vp = v * v;
      return 3.5 + (1.6 * vp) / (2.0 + vp);
    }

    let accDx = 0, accDy = 0, moveScheduled = false;
    function queueMove(dx, dy) {
      accDx += dx; accDy += dy;
      if (!moveScheduled) {
        moveScheduled = true;
        requestAnimationFrame(() => {
          moveScheduled = false;
          flushMove();
        });
      }
    }
    function flushMove() {
      if (destroyed) return;
      moveScheduled = false;
      const sendDx = Math.round(accDx), sendDy = Math.round(accDy);
      accDx -= sendDx; accDy -= sendDy;
      if (sendDx !== 0 || sendDy !== 0) send({ type: 'move', dx: sendDx, dy: sendDy });
    }

    let accScrollX = 0, accScrollY = 0, scrollScheduled = false;
    function queueScroll(dx, dy) {
      accScrollX += dx;
      accScrollY += dy;
      if (!scrollScheduled) {
        scrollScheduled = true;
        requestAnimationFrame(() => {
          scrollScheduled = false;
          flushScroll();
        });
      }
    }
    function flushScroll() {
      if (destroyed) return;
      scrollScheduled = false;
      const sx = Math.round(accScrollX);
      const sy = Math.round(accScrollY);
      accScrollX -= sx;
      accScrollY -= sy;
      if (sx !== 0 || sy !== 0) send({ type: 'scroll', dx: sx, dy: sy, delta: sy });
    }

    const activePointers = new Map();
    let maxActivePointers = 0, moved = false, tapFired = false;
    let isDragging = false, holdTimer = null;
    let scrollAxis = null, cumScrollX = 0, cumScrollY = 0;

    pad.addEventListener('pointerdown', (e) => {
      e.stopPropagation(); e.preventDefault();
      try { pad.setPointerCapture(e.pointerId); } catch (_) {}
      const isBar = scrollBarEl && (e.target === scrollBarEl || scrollBarEl.contains(e.target));
      if (isBar) scrollBarEl.classList.add('is-scrolling');
      const now = performance.now();
      activePointers.set(e.pointerId, {
        startX: e.clientX, startY: e.clientY,
        lastX: e.clientX, lastY: e.clientY,
        downTime: now,
        lastMoveTime: now,
        history: [{ t: now, x: e.clientX, y: e.clientY }],
        isBar,
      });
      maxActivePointers = Math.max(maxActivePointers, activePointers.size);
      if (activePointers.size > 1) {
        if (holdTimer) { clearTimeout(holdTimer); holdTimer = null; }
        if (isDragging) {
          isDragging = false;
          send({ type: 'mouseup' });
        }
        scrollAxis = null;
        cumScrollX = 0;
        cumScrollY = 0;
      }
      if (!isBar && activePointers.size === 1) {
        moved = false;
        tapFired = false;
        holdTimer = setTimeout(() => {
          if (!moved && !tapFired && maxActivePointers === 1 && activePointers.size === 1 && !isDragging) {
            isDragging = true;
            flushMove();
            send({ type: 'mousedown' });
            haptic();
          }
        }, holdDuration);
      }
    });

    pad.addEventListener('pointermove', (e) => {
      e.stopPropagation(); e.preventDefault();
      const pt = activePointers.get(e.pointerId);
      if (!pt) return;
      const rawDx = e.clientX - pt.lastX, rawDy = e.clientY - pt.lastY;
      pt.lastX = e.clientX; pt.lastY = e.clientY;

      if (pt.isBar) {
        const factor = natural ? 1 : -1;
        queueScroll(0, rawDy * scrollSpeed * 10 * factor);
        return;
      }

      if (Math.hypot(e.clientX - pt.startX, e.clientY - pt.startY) > 6) {
        moved = true;
        if (!isDragging && holdTimer) { clearTimeout(holdTimer); holdTimer = null; }
      }

      if (activePointers.size === 1) {
        let gain = 1.0;
        if (acceleration) {
          const now = performance.now();
          const elapsed = now - pt.lastMoveTime;
          if (elapsed > 60) {
            const dt = Math.max(12, Math.min(35, elapsed));
            const dist = Math.hypot(rawDx, rawDy);
            gain = getGain(dist / dt);
            pt.history = [
              { t: now - dt, x: pt.lastX - rawDx, y: pt.lastY - rawDy },
              { t: now, x: e.clientX, y: e.clientY },
            ];
          } else {
            pt.history.push({ t: now, x: e.clientX, y: e.clientY });
            while (pt.history.length > 2 && (now - pt.history[0].t) > 45) {
              pt.history.shift();
            }
            const oldest = pt.history[0];
            const dt = now - oldest.t;
            if (dt > 0) {
              const dist = Math.hypot(e.clientX - oldest.x, e.clientY - oldest.y);
              gain = getGain(dist / dt);
            } else {
              gain = getGain(0);
            }
          }
          pt.lastMoveTime = now;
        }
        queueMove(rawDx * gain * sensitivity, rawDy * gain * sensitivity);
      } else if (activePointers.size === 2) {
        if (isDragging) {
          isDragging = false;
          send({ type: 'mouseup' });
        }
        const factorY = natural ? 1 : -1;
        const factorX = natural ? -1 : 1;
        // Two pointers concurrently fire pointermove; scale by 5 so total 2-finger sensitivity matches 1-finger scrollbar
        const scale = scrollSpeed * 5;
        const dxDelta = rawDx * scale * factorX;
        const dyDelta = rawDy * scale * factorY;

        cumScrollX += Math.abs(rawDx);
        cumScrollY += Math.abs(rawDy);

        if (!scrollAxis) {
          if (cumScrollX + cumScrollY > 3) {
            moved = true;
            if (cumScrollY >= cumScrollX * 1.25) scrollAxis = 'y';
            else if (cumScrollX >= cumScrollY * 1.25) scrollAxis = 'x';
            else scrollAxis = 'free';
          }
        } else if (scrollAxis === 'y') {
          if (Math.abs(rawDx) > Math.abs(rawDy) * 2.5 && Math.abs(rawDx) > 7) {
            scrollAxis = 'x';
            cumScrollX = Math.abs(rawDx);
            cumScrollY = 0;
          }
        } else if (scrollAxis === 'x') {
          if (Math.abs(rawDy) > Math.abs(rawDx) * 2.5 && Math.abs(rawDy) > 7) {
            scrollAxis = 'y';
            cumScrollY = Math.abs(rawDy);
            cumScrollX = 0;
          }
        }

        if (scrollAxis === 'y') {
          queueScroll(0, dyDelta);
        } else if (scrollAxis === 'x') {
          queueScroll(dxDelta, 0);
        } else {
          const sendX = Math.abs(rawDx) >= 1.5 ? dxDelta : 0;
          const sendY = Math.abs(rawDy) >= 1.5 ? dyDelta : 0;
          queueScroll(sendX, sendY);
        }
      }
    });

    const onPointerEnd = (e) => {
      e.stopPropagation(); e.preventDefault();
      try { pad.releasePointerCapture(e.pointerId); } catch (_) {}
      if (holdTimer) { clearTimeout(holdTimer); holdTimer = null; }
      const pt = activePointers.get(e.pointerId);
      if (pt?.isBar) {
        if (scrollBarEl) scrollBarEl.classList.remove('is-scrolling');
        flushScroll();
      } else if (isDragging) {
        flushMove();
        send({ type: 'mouseup' });
        isDragging = false;
        tapFired = true;
      } else if (!moved && !tapFired && e.type !== 'pointercancel') {
        const duration = pt ? performance.now() - pt.downTime : 9999;
        if (maxActivePointers === 1 && duration < 300) {
          flushMove();
          send({ type: 'click' });
          haptic();
          tapFired = true;
        } else if (maxActivePointers === 2 && duration < 400) {
          flushMove();
          flushScroll();
          send({ type: 'rightClick' });
          haptic();
          tapFired = true;
        }
      }
      activePointers.delete(e.pointerId);
      if (activePointers.size < 2) {
        scrollAxis = null;
        cumScrollX = 0;
        cumScrollY = 0;
      }
      if (activePointers.size === 0) {
        maxActivePointers = 0; moved = false; tapFired = false;
        if (scrollBarEl) scrollBarEl.classList.remove('is-scrolling');
        flushMove(); flushScroll();
      }
    };

    pad.addEventListener('pointerup', onPointerEnd);
    pad.addEventListener('pointercancel', onPointerEnd);
    pad.addEventListener('touchstart', (e) => { e.stopPropagation(); }, { passive: false });
    pad.addEventListener('touchmove', (e) => { e.stopPropagation(); e.preventDefault(); }, { passive: false });
    pad.addEventListener('touchend', (e) => { e.stopPropagation(); }, { passive: false });
    pad.addEventListener('contextmenu', (e) => { e.preventDefault(); e.stopPropagation(); });

    return {
      update() {},
      destroy() {
        destroyed = true;
        moveScheduled = false;
        scrollScheduled = false;
        accScrollX = 0;
        accScrollY = 0;
        scrollAxis = null;
        cumScrollX = 0;
        cumScrollY = 0;
        if (reconnectTimer) clearTimeout(reconnectTimer);
        if (holdTimer) clearTimeout(holdTimer);
        if (isDragging) { send({ type: 'mouseup' }); isDragging = false; }
        if (ws) { try { ws.close(); } catch (_) {} ws = null; }
        activePointers.clear();
      }
    };
  }
};

// ---------------------------------------------------------------- Control center
// Volume, display and keyboard brightness as thick sliders, Spotlight search, and the connected displays: a tap on
// one moves the pointer (and so the focus) there. Reads /api/system/controls every 2 s while on screen.
W.controlcenter = {
  needs: ['state'],
  create(box, { o }) {
    box.classList.add('cc2');
    const head = h('div', 'cc2-head', h('span', 's-label', o.title || 'Control center'), h('span', 'cc2-count'));
    const sliders = h('div', 'cc2-sliders');
    const side = h('div', 'cc2-side');
    const form = h('form', 'cc2-search');
    const q = document.createElement('input');
    q.type = 'search'; q.enterKeyHint = 'search'; q.maxLength = 200; q.placeholder = 'Search on Mac';
    form.append(icon('search'), q);
    const screens = h('div', 'cc2-screens');
    side.append(form, screens);
    box.append(head, h('div', 'cc2-body', sliders, side));
    let sys = null, fetchedAt = 0, busy = false;

    // a thick pill slider with its icon inside, like Android's quick settings brightness bar
    const slider = (glyph, label, send, onIcon) => {
      const row = h('label', 'cc2-sl');
      const input = document.createElement('input');
      input.type = 'range'; input.min = '0'; input.max = '100'; input.step = '1';
      input.setAttribute('aria-label', label);
      const ic = h('button', 'cc2-ic', icon(glyph));
      ic.type = 'button';
      ic.setAttribute('aria-label', label);
      const val = h('span', 'cc2-val');
      row.append(input, ic, h('span', 'cc2-name', label), val);
      let timer = 0, dragging = false, release = 0;
      const paint = (v, text) => { row.style.setProperty('--p', v + '%'); val.textContent = text ?? v + '%'; };
      input.addEventListener('input', () => {
        dragging = true; clearTimeout(release);
        paint(+input.value);
        clearTimeout(timer);
        timer = setTimeout(() => send(+input.value), 110);
      });
      input.addEventListener('change', () => { release = setTimeout(() => (dragging = false), 1500); });
      if (onIcon) ic.addEventListener('click', (e) => { e.preventDefault(); haptic(); onIcon(); });
      else ic.tabIndex = -1;
      return {
        row,
        set(v, { disabled = false, text, hidden = false } = {}) {
          row.hidden = hidden;
          row.classList.toggle('off', disabled);
          input.disabled = disabled;
          if (!dragging) { if (v != null) input.value = v; paint(v ?? 0, text); }
        },
        glyph(g) { ic.replaceChildren(icon(g)); },
      };
    };
    const post = (path) => api(path, 'POST', 3000).catch((e) => { if (e?.status) toast(t('panel.pcNoAnswer'), 'warn'); });
    const vol = slider('volume', 'Volume', (v) => post('/volume?level=' + v), async () => {
      try { const a = await api('/volume?op=mute', 'POST'); D.state.audio = a; draw(); } catch (_) {}
    });
    const scr = slider('sun', 'Display', (v) => { const d = target(); if (d) post(`/system/display/brightness?id=${encodeURIComponent(d.id)}&value=${v}`); });
    const kbd = slider('keyboard', 'Keyboard', (v) => post('/system/keyboard?value=' + v));
    sliders.append(vol.row, scr.row, kbd.row);
    // the display whose brightness the slider drives: the built-in one when it can, else the first that can
    const target = () => (sys?.displays || []).find((d) => d.builtin && d.brightness != null) || (sys?.displays || []).find((d) => d.brightness != null) || null;

    form.addEventListener('submit', async (e) => {
      e.preventDefault();
      const text = q.value.trim();
      haptic();
      q.blur();
      try { await api('/system/search?q=' + encodeURIComponent(text), 'POST', 4000); q.value = ''; toast(text ? `Searching “${text}” on the Mac` : 'Spotlight is open on the Mac', 'ok'); }
      catch (err) { toast(err?.status === 400 ? 'Allow PhoneBox in System Settings › Privacy & Security › Accessibility' : t('panel.pcNoAnswer'), 'warn'); }
    });

    const drawScreens = () => {
      const list = sys?.displays || [];
      head.querySelector('.cc2-count').textContent = list.length ? `${list.length} ${list.length === 1 ? 'display' : 'displays'}` : '';
      screens.replaceChildren(...list.map((d) => {
        const b = h('button', 'cc2-scr' + (d.focused ? ' on' : ''), icon(d.builtin ? 'laptop' : 'monitor'),
          h('span', 'cc2-scr-t', h('b', null, d.name), h('small', null, `${d.width}×${d.height}${d.hz ? ' · ' + Math.round(d.hz) + ' Hz' : ''}`)));
        b.type = 'button';
        b.setAttribute('aria-pressed', d.focused ? 'true' : 'false');
        b.addEventListener('click', async () => {
          haptic();
          for (const x of list) x.focused = x.id === d.id; // shown at once; the next read confirms it
          drawScreens();
          try { await api('/system/display/focus?id=' + encodeURIComponent(d.id), 'POST', 3000); } catch (_) { toast(t('panel.pcNoAnswer'), 'warn'); }
        });
        return b;
      }));
    };
    const draw = () => {
      const a = D.state?.audio;
      vol.set(a ? a.volume : null, { disabled: !a || a.canControl === false, text: a?.muted ? 'Muted' : undefined });
      vol.glyph(a?.muted ? 'volumeX' : 'volume');
      const d = target();
      scr.set(d ? d.brightness : null, { disabled: !d, text: d ? undefined : 'n/a' });
      const k = sys?.keyboard;
      kbd.set(k?.brightness ?? null, { hidden: !k?.available, disabled: k?.brightness == null });
      form.hidden = sys ? sys.search === false : false;
      drawScreens();
    };
    const load = async () => {
      if (busy || Date.now() - fetchedAt < 2000) return;
      busy = true;
      try { sys = await api('/system/controls'); fetchedAt = Date.now(); } catch (_) {}
      finally { busy = false; }
      draw();
    };
    draw();
    return { update() { draw(); load(); } };
  },
};

// ---------------------------------------------------------------- Mac controls
// Running apps (tap: bring to front; hold: move its window to the other display), keep awake / sleep display / lock /
// Focus, the sound output, and a clipboard bridge. Reads /api/system/apps, /api/system/audio, /api/system/controls.
W.mac = {
  needs: ['state'],
  create(box, { o }) {
    box.classList.add('mc');
    const apps = h('div', 'mc-apps');
    const power = h('div', 'mc-power');
    const outs = h('div', 'mc-outs');
    const clip = document.createElement('input');
    clip.type = 'text'; clip.placeholder = 'Send text to the Mac clipboard'; clip.maxLength = 100000;
    const clipSend = h('button', 'mc-iconbtn', icon('send')); clipSend.type = 'button'; clipSend.setAttribute('aria-label', 'Send to clipboard');
    const clipGet = h('button', 'mc-iconbtn', icon('download')); clipGet.type = 'button'; clipGet.setAttribute('aria-label', 'Get the Mac clipboard');
    box.append(h('div', 'mc-head', h('span', 's-label', o.title || 'Mac'), h('span', 'mc-sub')),
      h('div', 'mc-body', apps, h('div', 'mc-side',
        h('div', 'mc-sec', 'Power'), power,
        h('div', 'mc-sec', 'Sound output'), outs,
        h('div', 'mc-sec', 'Clipboard'), h('div', 'mc-clip', icon('clipboard'), clip, clipSend, clipGet))));
    let list = null, audio = null, sys = null, at = 0, busy = false;
    const post = async (path, okMsg) => {
      haptic();
      try { await api(path, 'POST', 4000); if (okMsg) toast(okMsg, 'ok'); at = 0; load(); return true; }
      catch (e) { toast(e?.status === 400 ? 'Allow PhoneBox in System Settings › Privacy & Security › Accessibility' : t('panel.pcNoAnswer'), 'warn'); return false; }
    };
    const otherDisplay = () => { const d = sys?.displays || []; return d.find((x) => !x.focused) || null; };
    const drawApps = () => {
      if (!list) return;
      apps.replaceChildren(...list.slice(0, 16).map((a) => {
        const img = document.createElement('img');
        img.dataset.src = `${API}/system/apps/icon?bundle=${encodeURIComponent(a.bundleId)}`; // set once on the page
        img.alt = '';
        img.onerror = () => img.replaceWith(h('span', 'mc-letter', (a.name || '?').charAt(0)));
        const b = h('button', 'mc-app' + (a.active ? ' on' : ''), img, h('span', 'mc-name', a.name));
        b.type = 'button';
        let hold = 0, held = false;
        b.addEventListener('pointerdown', () => { held = false; hold = setTimeout(async () => {
          held = true;
          const d = otherDisplay();
          if (!d) { toast('Only one display is connected', 'warn'); return; }
          await post(`/system/apps/move?bundle=${encodeURIComponent(a.bundleId)}&display=${encodeURIComponent(d.id)}`, `${a.name} → ${d.name}`);
        }, 550); });
        const cancel = () => clearTimeout(hold);
        b.addEventListener('pointerup', cancel); b.addEventListener('pointerleave', cancel); b.addEventListener('pointercancel', cancel);
        b.addEventListener('click', () => { if (!held) post('/system/apps/activate?bundle=' + encodeURIComponent(a.bundleId)); });
        return b;
      }));
      apps.querySelectorAll('img[data-src]').forEach((img) => { img.src = img.dataset.src; img.removeAttribute('data-src'); });
      box.querySelector('.mc-sub').textContent = `${list.length} apps · hold one to move it to the other display`;
    };
    const drawPower = () => {
      const p = sys?.power || {};
      const btn = (cls, glyph, label, on, act) => {
        const b = h('button', 'mc-pw ' + cls + (on ? ' on' : ''), icon(glyph), h('span', null, label));
        b.type = 'button'; b.addEventListener('click', act); return b;
      };
      power.replaceChildren(
        btn('awake', 'coffee', 'Keep awake', p.caffeinated, () => post('/system/caffeinate?on=' + !p.caffeinated, p.caffeinated ? 'The Mac may sleep again' : 'The Mac stays awake')),
        btn('sleep', 'moon', 'Sleep display', false, () => post('/system/display/sleep')),
        btn('lock', 'lock', 'Lock', false, () => post('/system/lock', 'Locked')),
        ...(p.focusShortcuts ? [btn('focus', 'focus', 'Focus', !!p.focus, () => post('/system/focus?on=' + !p.focus))] : []));
    };
    const drawOuts = () => {
      const l = audio?.outputs || [];
      outs.replaceChildren(...l.map((d) => {
        const b = h('button', 'mc-out' + (d.current ? ' on' : ''), icon(d.transport === 'bluetooth' ? 'headphones' : d.transport === 'builtin' ? 'laptop' : 'volume'), h('span', null, d.name));
        b.type = 'button';
        b.addEventListener('click', () => { for (const x of l) x.current = x === d; drawOuts(); post('/system/audio/output?id=' + encodeURIComponent(d.id)); });
        return b;
      }));
      if (!l.length) outs.replaceChildren(h('span', 'muted', '—'));
    };
    clipSend.addEventListener('click', async () => {
      const text = clip.value;
      if (!text) return;
      if (encodeURIComponent(text).length > 15000) { toast('Too long to send from the phone', 'warn'); return; }
      if (await post('/system/clipboard?text=' + encodeURIComponent(text), 'On the Mac clipboard')) clip.value = '';
    });
    clipGet.addEventListener('click', async () => {
      haptic();
      try {
        const r = await api('/system/clipboard');
        if (r?.text == null) { toast('The Mac clipboard holds no text', 'warn'); return; }
        clip.value = r.text;
        try { await navigator.clipboard.writeText(r.text); toast('Copied from the Mac', 'ok'); } catch (_) { toast('The Mac clipboard is in the box', 'ok'); }
      } catch (_) { toast(t('panel.pcNoAnswer'), 'warn'); }
    });
    const load = async () => {
      if (busy || Date.now() - at < 3000) return;
      busy = true;
      try {
        [list, audio, sys] = await Promise.all([api('/system/apps').catch(() => list), api('/system/audio').catch(() => audio), api('/system/controls').catch(() => sys)]);
        at = Date.now();
      } finally { busy = false; }
      drawApps(); drawPower(); drawOuts();
    };
    drawPower();
    return { update: load };
  },
};

// ---------------------------------------------------------------- Remote desktop
// The Mac's screen, live, and touch control: tap = click, double tap = double click, long press = right click,
// one-finger drag = drag (pans instead while zoomed in), two-finger drag = scroll, pinch = zoom the view.
// Frames come from GET /api/remote/frame (newest JPEG, ?since= waits for a new one); input goes to POST /api/remote/input.
W.remote = {
  needs: ['state'],
  create(box, { o }) {
    box.classList.add('rm');
    const stage = h('div', 'rm-stage');
    const img = document.createElement('img');
    img.className = 'rm-screen'; img.alt = '';
    const msg = h('div', 'rm-msg');
    const ring = h('i', 'rm-tap');
    stage.append(img, ring, msg);
    const kbd = document.createElement('input');
    kbd.className = 'rm-kbd'; kbd.autocapitalize = 'off'; kbd.autocomplete = 'off'; kbd.spellcheck = false; kbd.setAttribute('aria-label', 'Type on the Mac');
    const screens = h('div', 'rm-screens');
    const btn = (glyph, label, fn, cls = '') => { const b = h('button', 'rm-btn ' + cls, icon(glyph)); b.type = 'button'; b.setAttribute('aria-label', label); b.title = label; b.addEventListener('click', (e) => { e.stopPropagation(); haptic(); fn(b); }); return b; };
    const fps = h('span', 'rm-fps');
    const keysRow = h('div', 'rm-keys');
    const KEYS = [['Esc', 'esc'], ['Tab', 'tab'], ['⌘Tab', 'tab', 'cmd'], ['←', 'left'], ['↑', 'up'], ['↓', 'down'], ['→', 'right'], ['⌫', 'backspace'], ['↵', 'enter'],
      ['⌘C', 'c', 'cmd'], ['⌘V', 'v', 'cmd'], ['⌘Z', 'z', 'cmd'], ['⌘A', 'a', 'cmd'], ['⌘Space', 'space', 'cmd']];
    keysRow.append(...KEYS.map(([label, key, mods]) => {
      const b = h('button', 'rm-key', label); b.type = 'button';
      b.addEventListener('click', (e) => { e.stopPropagation(); haptic(); input({ type: 'key', key, mods: mods || '' }); });
      return b;
    }));
    keysRow.hidden = true;
    let hd = o.quality !== 'sd';
    const bar = h('div', 'rm-bar', screens,
      btn('keyboard', 'Keyboard', () => { kbd.focus(); }),
      btn('command', 'Special keys', (b) => { keysRow.hidden = !keysRow.hidden; b.classList.toggle('on', !keysRow.hidden); }),
      btn('hd', 'Quality', (b) => { hd = !hd; b.classList.toggle('on', hd); since = 0; }, 'on'),
      btn('expand', 'Full screen', (b) => { box.classList.toggle('rm-full'); document.body.classList.toggle('rm-fullscreen', box.classList.contains('rm-full')); b.classList.toggle('on'); fit(); }),
      fps);
    box.append(stage, keysRow, bar, kbd);

    // ---- state
    let info = null, display = null, since = 0, running = false, visible = false, frames = 0, fpsAt = performance.now(), lastUrl = '';
    let zoom = 1, panX = 0, panY = 0;
    const say = (text, hint = '') => { msg.replaceChildren(...(text ? [h('b', null, text), hint ? h('span', null, hint) : ''] : [])); msg.hidden = !text; };
    const post = (q) => fetch(`${API}/remote/input?${q}`, { method: 'POST', headers: PANEL_HEADERS }).then(async (r) => {
      if (r.status === 403) { const j = await r.json().catch(() => ({})); toast(j.error || 'Not allowed', 'warn'); }
    }).catch(() => {});
    const input = (p) => post(new URLSearchParams({ display: display?.id || '', ...Object.fromEntries(Object.entries(p).map(([k, v]) => [k, String(v)])) }).toString());

    // ---- frames
    const loop = async () => {
      if (running) return;
      running = true;
      while (visible && display) {
        const w = Math.min(2560, Math.round(stage.clientWidth * (window.devicePixelRatio || 1) * (hd ? 1 : 0.6)));
        try {
          const r = await fetch(`${API}/remote/frame?display=${encodeURIComponent(display.id)}&w=${Math.max(320, w)}&q=${hd ? 70 : 45}&since=${since}`, { headers: PANEL_HEADERS, cache: 'no-store' });
          if (r.status === 200) {
            since = +(r.headers.get('X-Frame-Seq') || since + 1);
            const url = URL.createObjectURL(await r.blob());
            const old = lastUrl; lastUrl = url;
            img.onload = () => { if (old) URL.revokeObjectURL(old); fit(); say(''); };
            img.src = url;
            frames++;
          } else if (r.status === 204 || r.status === 304) { /* nothing new on the screen */ }
          else {
            const j = await r.json().catch(() => ({}));
            say(r.status === 403 ? 'Remote desktop is off' : r.status === 409 ? 'PhoneBox may not see the screen yet' : 'No picture',
              r.status === 403 ? 'Turn it on in the computer’s PhoneBox Settings.' : r.status === 409 ? 'Allow PhoneBoxAgent in System Settings › Privacy & Security › Screen Recording, then restart PhoneBox.' : (j.error || ''));
            await new Promise((ok) => setTimeout(ok, 1500));
          }
        } catch (_) { say('Connecting…'); await new Promise((ok) => setTimeout(ok, 1000)); }
        const now = performance.now();
        // a still screen sends no new frames: say "Live" then rather than a worrying "0 fps"
        if (now - fpsAt > 1000) { const n = Math.round((frames * 1000) / (now - fpsAt)); fps.textContent = n ? `${n} fps` : (lastUrl ? 'Live' : ''); frames = 0; fpsAt = now; }
      }
      running = false;
    };
    new IntersectionObserver(([e]) => { visible = e.isIntersecting && e.intersectionRatio > 0.6; if (visible) loop(); }, { threshold: [0, 0.6, 1] }).observe(box);

    // ---- where the picture sits (contain, then the zoom and pan)
    const rect = () => {
      const sw = stage.clientWidth, sh = stage.clientHeight, iw = img.naturalWidth || 16, ih = img.naturalHeight || 9;
      const s = Math.min(sw / iw, sh / ih), w = iw * s * zoom, hgt = ih * s * zoom;
      return { x: (sw - w) / 2 + panX, y: (sh - hgt) / 2 + panY, w, h: hgt };
    };
    const fit = () => {
      const r = rect();
      img.style.width = r.w + 'px'; img.style.height = r.h + 'px'; img.style.transform = `translate(${r.x}px, ${r.y}px)`;
    };
    const clampPan = () => {
      const sw = stage.clientWidth, sh = stage.clientHeight, r0 = (() => { const z = zoom; zoom = 1; const p = [panX, panY]; panX = panY = 0; const r = rect(); zoom = z; [panX, panY] = p; return r; })();
      const maxX = Math.max(0, (r0.w * zoom - sw) / 2), maxY = Math.max(0, (r0.h * zoom - sh) / 2);
      panX = Math.max(-maxX, Math.min(maxX, panX)); panY = Math.max(-maxY, Math.min(maxY, panY));
    };
    const norm = (cx, cy) => {
      const b = stage.getBoundingClientRect(), r = rect();
      const x = (cx - b.left - r.x) / r.w, y = (cy - b.top - r.y) / r.h;
      return x < 0 || y < 0 || x > 1 || y > 1 ? null : { x: +x.toFixed(4), y: +y.toFixed(4) };
    };
    const showTap = (cx, cy, cls) => {
      const b = stage.getBoundingClientRect();
      ring.style.left = cx - b.left + 'px'; ring.style.top = cy - b.top + 'px';
      ring.className = 'rm-tap'; void ring.offsetWidth; ring.className = 'rm-tap go ' + (cls || '');
    };
    new ResizeObserver(fit).observe(stage);

    // ---- gestures
    const pts = new Map();
    let mode = null, start = null, pressTimer = 0, lastTap = null, tapTimer = 0, moveAt = 0, pinch0 = null, scrollY0 = 0, scrollX0 = 0;
    stage.addEventListener('pointerdown', (e) => {
      stage.setPointerCapture(e.pointerId);
      pts.set(e.pointerId, { x: e.clientX, y: e.clientY });
      if (pts.size === 1) {
        mode = 'pending'; start = { x: e.clientX, y: e.clientY, t: e.timeStamp };
        clearTimeout(pressTimer);
        pressTimer = setTimeout(() => {
          if (mode !== 'pending') return;
          mode = 'done';
          const p = norm(start.x, start.y);
          if (p) { haptic(); showTap(start.x, start.y, 'right'); input({ type: 'rightclick', ...p }); }
        }, 520);
      } else if (pts.size === 2) {
        clearTimeout(pressTimer);
        if (mode === 'drag') { const p = norm(start.x, start.y); input({ type: 'up', ...(p || {}) }); }
        const [a, b] = [...pts.values()];
        pinch0 = { d: Math.hypot(a.x - b.x, a.y - b.y), zoom, cx: (a.x + b.x) / 2, cy: (a.y + b.y) / 2, panX, panY };
        scrollY0 = (a.y + b.y) / 2; scrollX0 = (a.x + b.x) / 2;
        mode = 'two';
      }
    });
    stage.addEventListener('pointermove', (e) => {
      if (!pts.has(e.pointerId)) return;
      pts.set(e.pointerId, { x: e.clientX, y: e.clientY });
      if (mode === 'pending' && Math.hypot(e.clientX - start.x, e.clientY - start.y) > 10) {
        clearTimeout(pressTimer);
        if (zoom > 1.01) mode = 'pan';
        else { mode = 'drag'; const p = norm(start.x, start.y); if (p) input({ type: 'down', ...p }); }
        start.px = panX; start.py = panY;
      }
      if (mode === 'pan') { panX = start.px + (e.clientX - start.x); panY = start.py + (e.clientY - start.y); clampPan(); fit(); }
      else if (mode === 'drag' && e.timeStamp - moveAt > 33) { moveAt = e.timeStamp; const p = norm(e.clientX, e.clientY); if (p) input({ type: 'move', ...p }); }
      else if (mode === 'two' && pts.size === 2) {
        const [a, b] = [...pts.values()];
        const d = Math.hypot(a.x - b.x, a.y - b.y), cx = (a.x + b.x) / 2, cy = (a.y + b.y) / 2;
        if (Math.abs(d / pinch0.d - 1) > 0.08 || pinch0.pinching) {
          // pinch: zoom the view around the fingers
          pinch0.pinching = true;
          zoom = Math.max(1, Math.min(4, pinch0.zoom * d / pinch0.d));
          panX = pinch0.panX + (cx - pinch0.cx); panY = pinch0.panY + (cy - pinch0.cy);
          clampPan(); fit();
        } else if (e.timeStamp - moveAt > 50) {
          // two fingers moving together: scroll the Mac
          moveAt = e.timeStamp;
          // like a touchscreen: fingers moving up scroll the content down (the agent takes dy > 0 as down)
          const dy = Math.round((scrollY0 - cy) * 2), dx = Math.round((scrollX0 - cx) * 2);
          scrollY0 = cy; scrollX0 = cx;
          if (dx || dy) { const p = norm(cx, cy); input({ type: 'scroll', dx, dy, ...(p || {}) }); }
        }
      }
    });
    const end = (e) => {
      if (!pts.has(e.pointerId)) return;
      pts.delete(e.pointerId);
      clearTimeout(pressTimer);
      if (mode === 'pending' && pts.size === 0) {
        const p = norm(start.x, start.y);
        if (p) {
          if (lastTap && e.timeStamp - lastTap.t < 300 && Math.hypot(start.x - lastTap.x, start.y - lastTap.y) < 30) {
            clearTimeout(tapTimer); lastTap = null; showTap(start.x, start.y, 'double'); input({ type: 'dblclick', ...p });
          } else {
            // a single click waits a moment for a possible second tap (a double click)
            lastTap = { x: start.x, y: start.y, t: e.timeStamp };
            const sp = { ...p }, sx = start.x, sy = start.y;
            tapTimer = setTimeout(() => { lastTap = null; showTap(sx, sy); input({ type: 'click', ...sp }); }, 230);
          }
        }
      } else if (mode === 'drag' && pts.size === 0) {
        const p = norm(e.clientX, e.clientY);
        input({ type: 'up', ...(p || {}) });
      }
      if (pts.size === 0) mode = null;
    };
    stage.addEventListener('pointerup', end);
    stage.addEventListener('pointercancel', end);
    stage.addEventListener('dblclick', (e) => e.preventDefault());

    // ---- keyboard: what is typed goes to the Mac as text; Enter / Backspace as keys
    kbd.addEventListener('input', () => { const v = kbd.value; kbd.value = ''; if (v) input({ type: 'text', text: v }); });
    kbd.addEventListener('keydown', (e) => {
      const k = { Enter: 'enter', Backspace: 'backspace', Tab: 'tab', Escape: 'esc', ArrowUp: 'up', ArrowDown: 'down', ArrowLeft: 'left', ArrowRight: 'right' }[e.key];
      if (k) { e.preventDefault(); input({ type: 'key', key: k, mods: '' }); }
    });

    // ---- displays and permissions
    const drawScreens = () => {
      const list = info?.displays || [];
      screens.replaceChildren(...list.map((d) => {
        const b = h('button', 'rm-scr' + (display?.id === d.id ? ' on' : ''), icon(d.main ? 'laptop' : 'monitor'), h('span', null, d.name));
        b.type = 'button';
        b.addEventListener('click', (e) => { e.stopPropagation(); haptic(); display = d; since = 0; zoom = 1; panX = panY = 0; drawScreens(); });
        return b;
      }));
    };
    const loadInfo = async () => {
      try {
        info = await api('/remote/info');
        if (!display || !info.displays?.some((d) => d.id === display.id)) display = info.displays?.find((d) => d.main) || info.displays?.[0] || null;
        drawScreens();
        if (!info.enabled) say('Remote desktop is off', 'Turn on “Let the phone view and control this computer” in the computer’s PhoneBox Settings.');
        else if (!info.screenPermission) say('PhoneBox may not see the screen yet', 'Allow PhoneBoxAgent in System Settings › Privacy & Security › Screen Recording, then restart PhoneBox.');
        else if (!lastUrl) say('Connecting…');
        box.classList.toggle('rm-viewonly', info.enabled && !info.inputPermission);
        if (visible) loop();
      } catch (_) { say('The computer is not reachable'); }
    };
    say('Connecting…');
    loadInfo();
    let infoAt = Date.now();
    return {
      update() { if (Date.now() - infoAt > 5000) { infoAt = Date.now(); loadInfo(); } },
      destroy() { visible = false; if (lastUrl) URL.revokeObjectURL(lastUrl); document.body.classList.remove('rm-fullscreen'); },
    };
  },
};
