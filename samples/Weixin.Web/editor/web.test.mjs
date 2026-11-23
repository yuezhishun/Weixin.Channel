import assert from 'node:assert/strict';
import { after, before, test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const origin = 'http://weixin-web.test';
const audioFixture = fileURLToPath(new URL('../../../tests/fixtures/live/acceptance-audio.wav', import.meta.url));
const staticFiles = new Map([
  ['/', 'index.html'],
  ['/app.css', 'app.css'],
  ['/app.js', 'app.js'],
  ['/markdown.css', 'markdown.css'],
  ['/markdown.js', 'markdown.js']
]);
const account = { accountId: 'bot-one', ownerId: 'owner-one', enabled: true, running: true, allowlist: [] };
const historyPath = '/api/accounts/bot-one/messages';
const timestamp = '2026-11-24T00:00:00.000Z';
const editor = '#message-editor .ProseMirror';
const fileInput = '#message-file';
const draft = '需要保留的正文';
const file = { name: 'fixture.txt', mimeType: 'text/plain', buffer: Buffer.from('合成附件') };
let browser;

before(async () => { browser = await chromium.launch(); });
after(async () => { await browser?.close(); });

async function openPage(t, api) {
  const page = await browser.newPage();
  t.after(() => page.close());
  await page.clock.install();
  await page.route('**/*', async route => {
    const url = new URL(route.request().url());
    if (url.origin !== origin) return route.abort();
    const asset = staticFiles.get(url.pathname);
    if (asset) return route.fulfill({ path: fileURLToPath(new URL(`../wwwroot/${asset}`, import.meta.url)) });
    if (!url.pathname.startsWith('/api/') || route.request().headers()['x-weixin-request'] !== '1') return route.abort();
    return api(route, url.pathname);
  });
  await page.goto(origin);
  return page;
}

function inbound(id, text, attachments = []) {
  return { id, direction: 'inbound', at: timestamp, text, attachments, status: 'pending' };
}

function conversation(items, ownerId = account.ownerId, hasSendContext = true) {
  return { ownerId, items, nextBefore: null, hasSendContext };
}

async function playAudio(page) {
  await page.getByRole('button', { name: '打开语音' }).click();
  const audio = await page.locator('#message-list audio').elementHandle();
  await audio.evaluate(async player => {
    player.muted = true;
    player.loop = true;
    await player.play();
  });
  await page.waitForFunction(player => !player.paused && player.currentTime > 0.05, audio);
  return audio;
}

async function draftWithFile(page) {
  await page.locator(editor).fill(draft);
  await page.locator(fileInput).setInputFiles(file);
}

async function assertDraftAndFile(page) {
  assert.equal(await page.locator(editor).textContent(), draft);
  assert.equal(await page.locator(fileInput).evaluate(input => input.files[0]?.name), file.name);
}

test('an active voice player survives eviction by polling and releases its source after pause or account switch', async t => {
  const oldest = inbound('oldest-voice', '最旧的语音', [{ id: '0', name: '语音.wav', kind: 'voice' }]);
  const initial = [...Array.from({ length: 49 }, (_, index) => inbound(`newer-${index}`, `记录 ${index}`)), oldest];
  let rows = initial;
  const page = await openPage(t, (route, path) => {
    if (path === '/api/accounts') return route.fulfill({ json: [account, { ...account, accountId: 'bot-two', ownerId: 'owner-two' }] });
    if (path === historyPath) return route.fulfill({ json: conversation(rows) });
    if (path === '/api/accounts/bot-two/messages') return route.fulfill({ json: conversation([], 'owner-two', false) });
    if (path === `${historyPath}/inbound/oldest-voice/attachments/0`) {
      return route.fulfill({ path: audioFixture, contentType: 'audio/wav' });
    }
    return route.abort();
  });

  const first = await playAudio(page);
  const firstUrl = await first.evaluate(player => player.src);
  assert.equal(await page.evaluate(url => fetch(url).then(response => response.ok), firstUrl), true);
  rows = [inbound('new-arrival', '轮询期间的新消息'), ...initial.slice(0, -1)];
  await page.clock.runFor(2500);
  await page.getByText('轮询期间的新消息', { exact: true }).waitFor();
  assert.equal(await first.evaluate(player => player.isConnected && !player.paused && !player.ended), true);
  assert.equal(await first.evaluate(player => player.src), firstUrl);

  await first.evaluate(player => player.pause());
  await page.getByText('最旧的语音', { exact: true }).waitFor({ state: 'detached' });
  assert.equal(await first.getAttribute('src'), null);
  assert.equal(await page.evaluate(url => fetch(url).then(() => false, () => true), firstUrl), true);

  rows = initial;
  await page.clock.runFor(2500);
  const second = await playAudio(page);
  const secondUrl = await second.evaluate(player => player.src);
  await page.getByRole('button', { name: /owner-two/ }).click();
  assert.equal(await second.evaluate(player => !player.isConnected && player.paused && !player.hasAttribute('src')), true);
  assert.equal(await page.evaluate(url => fetch(url).then(() => false, () => true), secondUrl), true);
});

test('allowlist changes recheck history authorization while preserving the current draft and file', async t => {
  const visible = inbound('authorized', '权限变化前的记录');
  let binding = account;
  let authorized = true;
  let allowRestrictedHistory;
  const restrictedHistoryGate = new Promise(resolve => { allowRestrictedHistory = resolve; });
  t.after(allowRestrictedHistory);
  const page = await openPage(t, async (route, path) => {
    if (path === '/api/accounts') return route.fulfill({ json: [binding] });
    if (path === historyPath) {
      if (authorized) return route.fulfill({ json: conversation([visible]) });
      await restrictedHistoryGate;
      return route.fulfill({ json: conversation([], account.ownerId, false) });
    }
    return route.abort();
  });

  const existing = page.getByText(visible.text, { exact: true });
  await existing.waitFor();
  await draftWithFile(page);

  binding = { ...account, allowlist: [account.ownerId, 'other-owner'], running: false };
  await page.clock.runFor(2500);
  await page.getByRole('button', { name: '开始接收' }).waitFor();
  assert.equal(await existing.isVisible(), true);
  await assertDraftAndFile(page);

  authorized = false;
  binding = { ...account, allowlist: ['other-owner'] };
  await page.clock.runFor(2500);
  await existing.waitFor({ state: 'detached' });
  assert.equal(await page.locator('#send').isDisabled(), true);
  await assertDraftAndFile(page);
  allowRestrictedHistory();
  await page.getByText('暂无消息', { exact: true }).waitFor();
  await assertDraftAndFile(page);
});

test('an accepted send without a message ID retains the draft and warns in both composer and history', async t => {
  const incoming = inbound('first', '可用的会话上下文');
  const sent = { id: 'sent-client', direction: 'outbound', at: timestamp, text: draft, status: 'success', awaitingDeliveryConfirmation: true };
  let posts = 0;
  const page = await openPage(t, (route, path) => {
    if (path === '/api/accounts') return route.fulfill({ json: [account] });
    if (path === historyPath) return route.fulfill({ json: conversation(posts ? [sent, incoming] : [incoming]) });
    if (path === `${historyPath}/media` && route.request().method() === 'POST') {
      posts++;
      return route.fulfill({ json: { status: 'success', segments: [
        { clientId: sent.id, status: 'success', messageId: null },
        { clientId: 'attachment-client', status: 'success', messageId: 'attachment-id' }
      ] } });
    }
    return route.abort();
  });

  await page.getByText(incoming.text, { exact: true }).waitFor();
  await draftWithFile(page);
  await page.getByRole('button', { name: '发送消息' }).click();
  const notice = '微信已受理，手机送达待核对';
  await page.locator('#send-status').getByText(notice, { exact: false }).waitFor();
  assert.equal(await page.locator('#send-status').getAttribute('data-tone'), 'warning');
  await page.locator('#message-list').getByText(notice, { exact: false }).waitFor();
  await assertDraftAndFile(page);
  await page.clock.runFor(2500);
  assert.equal(posts, 1);
});

test('a binding remark survives reload and clearing restores the owner ID', async t => {
  let other = { ...account, accountId: 'bot-two', ownerId: 'owner-two' };
  const otherPath = '/api/accounts/bot-two';
  const label = '<b>办公室微信</b>';
  const writes = [];
  const page = await openPage(t, (route, path) => {
    if (path === '/api/accounts') return route.fulfill({ json: [account, other] });
    if (path === historyPath) return route.fulfill({ json: conversation([]) });
    if (path === `${otherPath}/messages`) return route.fulfill({ json: conversation([], other.ownerId) });
    if (path === otherPath && route.request().method() === 'PUT') {
      const payload = route.request().postDataJSON();
      writes.push(payload);
      other = { ...other, remark: payload.remark || null };
      return route.fulfill({ status: 204 });
    }
    return route.abort();
  });
  const otherRow = page.locator('#account-list [data-account-id="bot-two"]');
  const otherName = otherRow.locator('.account-name');
  const recipient = page.locator('#recipient');
  const edit = page.getByRole('button', { name: '编辑备注' });
  const input = page.getByLabel('备注名称');
  const save = page.getByRole('button', { name: '保存', exact: true });

  await otherRow.click();
  await edit.click();
  await input.fill(label);
  await save.click();
  await otherName.filter({ hasText: label }).waitFor();
  await recipient.filter({ hasText: label }).waitFor();
  assert.equal(await otherName.textContent(), label);
  assert.equal(await otherName.locator('b').count(), 0);
  assert.equal(await page.locator('#account-list [data-account-id="bot-one"] .account-name').textContent(), account.ownerId);

  await page.reload();
  await otherName.filter({ hasText: label }).waitFor();
  await otherRow.click();
  await recipient.filter({ hasText: label }).waitFor();
  await edit.click();
  assert.equal(await input.inputValue(), label);
  await input.fill('');
  await save.click();
  await otherName.filter({ hasText: other.ownerId }).waitFor();
  await recipient.filter({ hasText: other.ownerId }).waitFor();
  assert.deepEqual(writes, [
    { remark: label, expectedOwnerId: other.ownerId },
    { remark: '', expectedOwnerId: other.ownerId }
  ]);
});
