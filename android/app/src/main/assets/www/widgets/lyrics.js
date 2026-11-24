// Lyrics for what the computer is playing, from lrclib.net (free, no account), looked up by the phone itself.
// Synced lyrics ([mm:ss.xx] lines) when lrclib has them, else plain text; one lookup per song, cached.

const cache = new Map(); // "artist|title" -> Promise<{ synced, plain, instrumental } | null>

// extras that music channels add to a title: "(Official Video)", "- Audio", "| Movie | Cast", "Lyrical", "Full Song" …
const NOISE = /\s*[([](?:official|lyric|lyrics|lyrical|audio|video|visuali[sz]er|hd|4k|mv|m\/v|full|song|from|with)[^)\]]*[)\]]/gi;
const TAIL = /\s*[-–—:]\s*(?:official\s*)?(?:full\s*)?(?:audio|video|lyrical|lyrics?|lyric video|video song|audio song|full song|song|music video|visuali[sz]er)\b.*$/i;
const tidy = (s) => String(s || '').split(/\s[|｜]\s?|\s?[|｜]\s/)[0].replace(NOISE, '').replace(TAIL, '').replace(/\s+/g, ' ').trim();
const fold = (s) => String(s || '').toLowerCase().replace(/[^\p{L}\p{N}]+/gu, '');
// a channel that is a label, not the singer
const LABEL = /\b(music|records?|recordings|series|vevo|official|entertainment|studios?|films?|media|productions?|label|tv|channel|company)\b/i;

/** The (song, artist) pairs a title could mean, most likely first. */
export function candidates(title, artist) {
  const t = tidy(title), ch = String(artist || '').replace(/\s*-\s*Topic$/i, '').replace(/VEVO$/, '').trim();
  const chArtist = ch && !LABEL.test(ch) ? ch : '';
  const out = [];
  const parts = t.split(/\s+[-–—]\s+/).map((x) => x.trim()).filter(Boolean);
  if (parts.length >= 2) { out.push([parts[1], parts[0]]); if (chArtist) out.push([parts[1], chArtist]); out.push([parts[0], parts[1]]); }
  out.push([parts[0] || t, chArtist]);
  // the same without a featured artist ("Lighters ft. Bruno Mars" -> "Lighters")
  const noFeat = (x) => x.replace(/\s*[([]?\s*(?:ft\.?|feat\.?|featuring)\s.*$/i, '').trim();
  for (const [song, a] of [...out]) if (noFeat(song) !== song) out.push([noFeat(song), a]);
  const seen = new Set();
  return out.filter(([song, a]) => song && !seen.has(fold(song) + '|' + fold(a)) && seen.add(fold(song) + '|' + fold(a)));
}

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

const good = (s) => s && (s.synced?.length || s.plain || s.instrumental);

async function lookup(title, artist, durMs) {
  const tries = candidates(title, artist);
  // 1) exact records for each reading of the title
  for (const [song, a] of tries) {
    if (!a) continue;
    try {
      const qs = new URLSearchParams({ track_name: song, artist_name: a });
      if (durMs > 0) qs.set('duration', String(Math.round(durMs / 1000)));
      const r = await fetch('https://lrclib.net/api/get?' + qs, { cache: 'force-cache' });
      if (r.ok) { const s = shape(await r.json()); if (good(s)) return s; }
    } catch (_) {}
  }
  // 2) a search by song name: the same title, synced first, the closest length (skips sped-up / lofi versions)
  for (const [song, a] of tries) {
    try {
      const r = await fetch('https://lrclib.net/api/search?' + new URLSearchParams({ q: song }), { cache: 'force-cache' });
      const list = r.ok ? await r.json() : [];
      const want = fold(song), wa = fold(a);
      const score = (x) => (fold(x.trackName) === want ? 0 : 20) + (x.syncedLyrics ? 0 : 10)
        + (wa && (fold(x.artistName).includes(wa) || wa.includes(fold(x.artistName))) ? 0 : 3)
        + (durMs > 0 ? Math.min(30, Math.abs(x.duration * 1000 - durMs) / 1000) : 0);
      const best = (Array.isArray(list) ? list : []).filter((x) => fold(x.trackName).includes(want)).sort((x, y) => score(x) - score(y))[0];
      const s = shape(best || null);
      if (good(s)) return s;
    } catch (_) {}
  }
  return null;
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
