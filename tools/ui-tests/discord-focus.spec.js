const { test, expect } = require('@playwright/test');

// The Discord widget's "Side by side" layout (layout: focus). The demo answers /api/discord with you in "Game night"
// on "Friends", Ada speaking every 1.5 s, one notification, and (when they are on) a chat and a few apps to share.

const widget = (options = {}, size = { w: 12, h: 6 }) => ({ id: 'd', type: 'discord', x: 0, y: 0, ...size, options: { control: true, layout: 'focus', ...options } });

async function show(page, { options, size, discord = { screenShare: true, chat: true }, viewport } = {}) {
  if (viewport) await page.setViewportSize(viewport);
  await page.goto('/panel/');
  await page.evaluate(({ pages, discord }) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, rules: [], shortcuts: [], discord, pages }));
  }, { pages: [{ id: 'dc', title: 'Discord', enabled: true, widgets: [widget(options, size)] }], discord });
  await page.reload();
}

test('a widget without a saved layout keeps the list layout', async ({ page }) => {
  await page.goto('/panel/');
  await page.evaluate(() => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({ night: { enabled: false }, rules: [], shortcuts: [],
      pages: [{ id: 'dc', title: 'Discord', enabled: true, widgets: [{ id: 'd', type: 'discord', x: 0, y: 0, w: 6, h: 5, options: { control: true } }] }] }));
  });
  await page.reload();
  await expect(page.locator('.dc-member').first()).toBeVisible();
  await expect(page.locator('.w-discord.dcf')).toHaveCount(0);
});

test('side by side shows the channel, who is there, the call buttons and the channels', async ({ page }) => {
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await page.setViewportSize({ width: 1334, height: 800 });
  await show(page);
  const box = page.locator('.w-discord.dcf');
  await expect(box.locator('.dcf-name')).toHaveText('Game night');
  await expect(box.locator('.dcf-chip', { hasText: 'Ada' })).toHaveClass(/speaking/, { timeout: 5000 });
  for (const kind of ['mute', 'deafen', 'share', 'chat', 'leave']) await expect(box.locator(`.dcf-call.${kind}`)).toHaveAttribute('aria-label', /.+/);
  await expect(box.locator('.dcf-chan.cur')).toHaveText('Game night');
  await expect(box.locator('.dcf-chan', { hasText: 'Lounge' })).toBeVisible();
  // the notification shows under the channels, as in the list layout
  await expect(box.locator('.dcf-ntf', { hasText: 'are you joining?' })).toBeVisible();
  expect(errors).toEqual([]);
});

test('notifications stay hidden with the Notifications option off', async ({ page }) => {
  await show(page, { options: { notifications: false } });
  await expect(page.locator('.dcf-name')).toHaveText('Game night');
  await expect(page.locator('.dcf-ntf')).toHaveCount(0);
});

test('mic and sound toggle, and the mic shows off while deafened', async ({ page }) => {
  await show(page);
  const mic = page.locator('.dcf-call.mute'), sound = page.locator('.dcf-call.deafen');
  await mic.click();
  await expect(mic).toHaveClass(/\bon\b/);
  await mic.click();
  await expect(mic).not.toHaveClass(/\bon\b/);
  await sound.click();
  await expect(sound).toHaveClass(/\bon\b/);
  await expect(mic).toHaveClass(/\bon\b/);
});

test('the chat tab shows the voice channel chat and clears the dot', async ({ page }) => {
  await show(page);
  await expect(page.locator('.dcf-call.chat .dcf-dot')).toBeVisible();
  await page.locator('.dcf-tab', { hasText: /chat/i }).click();
  await expect(page.locator('.dcf-msgs')).toContainText('hi from the call');
  await expect(page.locator('.dcf-call.chat .dcf-dot')).toHaveCount(0);
});

test('screen share starts from the list of apps and stops; the name stays in the share tab', async ({ page }) => {
  await show(page);
  await page.locator('.dcf-call.share').click();
  await expect(page.locator('.dcf-pickpc')).toBeVisible();
  await page.locator('.dcf-app', { hasText: 'Space Racers' }).click();
  await expect(page.locator('.dcf-livebox')).toContainText('Space Racers');
  await expect(page.locator('.dcf-call.share')).toHaveClass(/live/);
  // back on the channels the state is polled without the app list: live, but no name (PRIVACY.md)
  await page.locator('.dcf-tab').first().click();
  await expect(page.locator('.dcf-label .dcf-badge')).toBeVisible({ timeout: 6000 });
  await expect(page.locator('.dcf-left')).not.toContainText('Space Racers', { timeout: 6000 });
  await page.locator('.dcf-call.share').click();
  await page.locator('.dcf-stop').click();
  await expect(page.locator('.dcf-pickpc')).toBeVisible();
});

test('outside a channel it offers the way back, keeping only the channel id on the phone', async ({ page }) => {
  await show(page);
  await expect(page.locator('.dcf-name')).toHaveText('Game night');
  await page.locator('.dcf-call.leave').click();
  await expect(page.locator('.dcf-empty')).toBeVisible();
  await expect(page.locator('.dcf-back')).toContainText('Game night');
  expect(await page.evaluate(() => JSON.parse(localStorage.getItem('pb-dc-last') || localStorage.getItem('dc-last') || 'null'))).toEqual({ id: 'c1' });
  await page.locator('.dcf-back').click();
  await expect(page.locator('.dcf-name')).toHaveText('Game night');
});

test('with the Channels option off there are no servers, channel keys or way back', async ({ page }) => {
  await show(page, { options: { channels: false } });
  await expect(page.locator('.dcf-name')).toHaveText('Game night');
  await expect(page.locator('.dcf-chan')).toHaveCount(0);
  // as in the list layout: no server switcher, no recent servers, no "Channels · …" caption
  await expect(page.locator('.dcf-switch')).toHaveCount(0);
  await expect(page.locator('.dcf-recent')).toHaveCount(0);
  await expect(page.locator('.dcf-right .dcf-cap')).toHaveCount(0);
  await page.locator('.dcf-call.leave').click();
  await expect(page.locator('.dcf-empty')).toBeVisible();
  await expect(page.locator('.dcf-back')).toHaveCount(0);
});

test('a phone-sized widget stacks the two sides and keeps the buttons named', async ({ page }) => {
  await show(page, { size: { w: 6, h: 6 }, viewport: { width: 780, height: 360 } });
  const left = page.locator('.dcf-left'), right = page.locator('.dcf-right');
  await expect(left).toBeVisible();
  const [l, r] = [await left.boundingBox(), await right.boundingBox()];
  expect(r.y).toBeGreaterThan(l.y + l.height - 2); // stacked, not side by side
  await expect(page.locator('.dcf-call.mute .dcf-cl')).toBeHidden();
  await expect(page.locator('.dcf-call.mute')).toHaveAttribute('aria-label', /.+/);
});

test('a redraw keeps the scrolled place on the right side', async ({ page }) => {
  // a short widget, so the channel keys and the notification scroll
  await show(page, { size: { w: 12, h: 2 }, viewport: { width: 780, height: 360 } });
  const right = page.locator('.dcf-right');
  await expect(page.locator('.dcf-chan').first()).toBeVisible();
  const max = await right.evaluate((el) => el.scrollHeight - el.clientHeight);
  expect(max).toBeGreaterThan(10);
  await right.evaluate((el) => { el.scrollTop = el.scrollHeight; });
  const before = await right.evaluate((el) => el.scrollTop);
  // a change that redraws the widget (the mic): the place stays
  await page.locator('.dcf-call.mute').click();
  await expect(page.locator('.dcf-call.mute')).toHaveClass(/\bon\b/);
  expect(await page.locator('.dcf-right').evaluate((el) => el.scrollTop)).toBe(before);
});

test('leaving the share tab for the server list drops the shared app name too', async ({ page }) => {
  await show(page);
  await page.locator('.dcf-call.share').click();
  await page.locator('.dcf-app', { hasText: 'Space Racers' }).click();
  await expect(page.locator('.dcf-livebox')).toContainText('Space Racers');
  await page.locator('.dcf-switch').click();
  await expect(page.locator('.dcf-srv').first()).toBeVisible();
  await expect(page.locator('.dcf-left')).not.toContainText('Space Racers');
  // and stays gone through the next state polls
  await page.waitForTimeout(4500);
  await expect(page.locator('.dcf-left')).not.toContainText('Space Racers');
});

test('screen share switched off on the computer drops the shared app name at once', async ({ page }) => {
  await show(page);
  await page.locator('.dcf-call.share').click();
  await page.locator('.dcf-app', { hasText: 'Space Racers' }).click();
  await expect(page.locator('.dcf-livebox')).toContainText('Space Racers');
  // Settings › General › Discord › screen share off, saved on the computer
  await page.evaluate(() => {
    const c = JSON.parse(localStorage.getItem('pb-demo-config'));
    c.discord = { ...c.discord, screenShare: false };
    localStorage.setItem('pb-demo-config', JSON.stringify(c));
  });
  await expect(page.locator('.dcf-call.share')).toHaveCount(0, { timeout: 6000 });
  await expect(page.locator('.w-discord')).not.toContainText('Space Racers');
  await expect(page.locator('.dcf-badge')).toHaveCount(0);
});
