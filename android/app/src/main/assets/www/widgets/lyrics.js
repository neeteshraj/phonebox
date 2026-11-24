// Lyrics for what the computer is playing, from lrclib.net (free, no account), looked up by the phone itself.
// Synced lyrics ([mm:ss.xx] lines) when lrclib has them, else plain text; one lookup per song, cached.

const cache = new Map(); // "artist|title" -> Promise<{ synced, plain, instrumental } | null>

const tidy = (s) => String(s || '')
  .replace(/\s*[([](official|lyric|lyrics|audio|video|visualizer|hd|4k|mv|m\/v)[^)\]]*[)\]]/gi, '')
  .replace(/\s+/g, ' ').trim();
const fold = (s) => tidy(s).toLowerCase().replace(/[^\p{L}\p{N}]+/gu, '');

/** "[01:02.34] text" lines into [{ at: ms, text }], sorted, empty lines kept as breaks out. */
export function parseSynced(lrc) {
  const out = [];
  for (const line of String(lrc || '').split('\n')) {
    const stamps = [...line.matchAll(/\[(\d+):(\d+(?:\.\d+)?)\]/g)];
    if (!stamps.length) continue;
    const text = line.replace(/\[[^\]]*\]/g, '').trim();
    for (const m of stamps) out.push({ at: Math.round((+m[1] * 60 + +m[2]) * 1000), text });
  }
  return out.sort((a, b) => a.at - b.at);
}

function shape(j) {
  if (!j) return null;
  return { synced: j.syncedLyrics ? parseSynced(j.syncedLyrics) : null, plain: j.plainLyrics || null, instrumental: !!j.instrumental };
}

async function lookup(title, artist, durMs) {
  const t = tidy(title), a = tidy(artist);
  // 1) the exact record (title + artist + length)
  try {
    const qs = new URLSearchParams({ track_name: t, artist_name: a });
    if (durMs > 0) qs.set('duration', String(Math.round(durMs / 1000)));
    const r = await fetch('https://lrclib.net/api/get?' + qs, { cache: 'force-cache' });
    if (r.ok) { const s = shape(await r.json()); if (s && (s.synced?.length || s.plain || s.instrumental)) return s; }
  } catch (_) {}
  // 2) a search: the same artist, the closest length, synced first
  try {
    const r = await fetch('https://lrclib.net/api/search?' + new URLSearchParams({ q: `${t} ${a}`.trim() }), { cache: 'force-cache' });
    const list = r.ok ? await r.json() : [];
    const want = fold(a);
    const best = (Array.isArray(list) ? list : [])
      .filter((x) => !want || fold(x.artistName).includes(want) || want.includes(fold(x.artistName)))
      .sort((x, y) => (!!y.syncedLyrics - !!x.syncedLyrics) || (durMs > 0 ? Math.abs(x.duration * 1000 - durMs) - Math.abs(y.duration * 1000 - durMs) : 0))[0];
    return shape(best || null);
  } catch (_) { return null; }
}

/** The lyrics of a track, or null when lrclib knows none. */
export function lyricsFor(m) {
  if (!m?.title) return Promise.resolve(null);
  const key = `${fold(m.artist)}|${fold(m.title)}`;
  if (!cache.has(key)) {
    cache.set(key, lookup(m.title, m.artist, m.durMs).catch(() => null));
    if (cache.size > 60) cache.delete(cache.keys().next().value);
  }
  return cache.get(key);
}

/** Index of the line being sung at pos (ms), -1 before the first. */
export function lineAt(lines, pos) {
  let i = -1;
  for (let k = 0; k < lines.length && lines[k].at <= pos + 250; k++) i = k;
  return i;
}
