'use strict';

const byId = id => document.getElementById(id);
const list = byId('account-list');
const historyPanel = byId('history');
const messages = byId('message-list');
const form = byId('composer');
const messageEditor = byId('message-editor');
const formatTools = byId('format-tools');
const formatButtons = [...formatTools.querySelectorAll('[data-format]')];
const linkDialog = byId('link-dialog');
const linkUrl = byId('link-url');
const remarkDialog = byId('remark-dialog');
const remarkInput = byId('account-remark');
const remarkError = byId('remark-error');
const remarkSave = byId('remark-save');
const messageFile = byId('message-file');
const dialog = byId('login-dialog');
const code = byId('verification-code');
const qr = byId('qr-image');
const clock = new Intl.DateTimeFormat('zh-CN', { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' });

let accounts = [];
let latestAccountsRequest = 0;
let accountListKey;
let current = null;
let login = null;
const pendingActions = new Set();
const pendingSends = new Set();
const renderedMessages = new Map();
const maxAttachmentBytes = 100 * 1024 * 1024;
let markdownEditor = null;
let linkTarget = null;
let remarkTarget = null;
let draftText = '';

async function request(path, method = 'GET', data, signal) {
  const headers = { 'x-weixin-request': '1' };
  const multipart = data instanceof FormData;
  if (data !== undefined && !multipart) headers['Content-Type'] = 'application/json';
  const response = await fetch(path, { method, headers, body: multipart ? data : JSON.stringify(data), cache: 'no-store', signal });
  if (!response.ok) {
    const problem = await response.json().catch(() => null);
    const error = new Error(problem?.title ?? `请求未完成（${response.status}）`);
    error.status = response.status;
    throw error;
  }
  return response;
}

function node(tag, className, value = '') {
  const element = document.createElement(tag);
  element.className = className;
  element.textContent = value;
  return element;
}

function setText(element, value) {
  if (element.textContent !== value) element.textContent = value;
}

function feedback(element, value, tone = '') {
  element.dataset.tone = tone;
  setText(element, value);
}

function weixinLink(value) {
  try {
    const url = new URL(value ?? '');
    return ['https:', 'http:'].includes(url.protocol) && !url.username && !url.password ? url : null;
  } catch { return null; }
}

function updateDraft() {
  draftText = markdownEditor?.getMarkdown() ?? '';
  updateControls();
}

function clearDraft() {
  markdownEditor?.clear();
  draftText = '';
}

function focusEditor() {
  markdownEditor?.focus();
}

function formatMessage(command) {
  if (command === 'link') {
    linkTarget = current;
    linkUrl.value = markdownEditor.selectionLink() ?? '';
    linkUrl.setCustomValidity('');
    linkDialog.showModal();
    return;
  }
  markdownEditor.run(command);
  updateDraft();
}

function markdownBody(text) {
  const element = node('div', 'message-markdown');
  element.innerHTML = weixinMarkdown.render(text);
  return element;
}

function accountState(account) {
  if (!account.enabled) return ['已停用', 'warning'];
  if (Date.parse(account.pausedUntil) > Date.now()) return ['登录已暂停', 'warning'];
  return account.running ? ['接收中', 'ready'] : ['未接收', 'idle'];
}

function accountPath(account) {
  return `/api/accounts/${encodeURIComponent(account.accountId)}`;
}

function accountName(account) {
  return account.remark || account.ownerId || account.accountId;
}

function renderAccounts() {
  const rows = accounts.map(account => [account.accountId, account.ownerId, accountName(account), ...accountState(account)]);
  const key = JSON.stringify([rows, current?.account.accountId]);
  if (key === accountListKey) return;
  accountListKey = key;
  const focusedId = list.contains(document.activeElement) ? document.activeElement.dataset.accountId : null;
  let focusTarget;
  list.replaceChildren(...rows.map(([id, owner, name, label, state]) => {
    const row = node('li', '');
    const button = node('button', 'account-item');
    button.type = 'button';
    button.dataset.accountId = id;
    if (id === current?.account.accountId) button.setAttribute('aria-current', 'true');
    if (focusedId === id) focusTarget = button;
    const avatar = node('span', 'avatar', '微');
    avatar.setAttribute('aria-hidden', 'true');
    const identity = node('span', '');
    const labelElement = node('span', 'account-name', name);
    labelElement.title = owner && name !== owner ? `${name} · ${owner}` : name;
    const bot = node('span', 'account-bot', `Bot · ${id}`);
    bot.title = id;
    const status = node('span', 'account-state', label);
    status.dataset.state = state;
    identity.append(labelElement, bot, status);
    button.append(avatar, identity);
    row.append(button);
    return row;
  }));
  focusTarget?.focus({ preventScroll: true });
  setText(byId('account-count'), String(accounts.length));
  byId('no-accounts').hidden = accounts.length > 0;
}

function selectAccount(account) {
  current?.loading?.abort();
  current = account ? { account, items: [], ready: false } : null;
  if (linkDialog.open) linkDialog.close();
  if (remarkDialog.open) remarkDialog.close();
  clearDraft();
  messageFile.value = '';
  feedback(byId('send-status'), '');
  renderAccounts();
  renderHistory(true);
  updateControls();
  if (current) void loadHistory(current);
}

async function refreshAccounts(preferredId) {
  const version = ++latestAccountsRequest;
  const service = byId('service-status');
  try {
    const updated = await (await request('/api/accounts')).json();
    if (version !== latestAccountsRequest) return;
    accounts = updated;
    const id = preferredId ?? current?.account.accountId;
    const account = accounts.find(item => item.accountId === id) ?? accounts[0];
    const empty = byId('no-accounts');
    setText(empty, '暂无已连接账号');
    service.dataset.state = 'ready';
    setText(service, '本机服务在线');
    if (current?.account.accountId !== account?.accountId || current?.account.ownerId !== account?.ownerId) {
      selectAccount(account);
    } else {
      if (current) {
        const allowsOwner = binding => !binding.allowlist?.length || binding.allowlist.includes(binding.ownerId);
        const permissionChanged = allowsOwner(current.account) !== allowsOwner(account);
        current.account = account;
        if (permissionChanged) {
          current.loading?.abort();
          current.loading = null;
          resetHistory(current);
          void loadHistory(current);
        }
      }
      renderAccounts();
      updateControls();
    }
  } catch {
    if (version !== latestAccountsRequest) return;
    service.dataset.state = 'error';
    setText(service, '本机服务不可用');
    setText(byId('no-accounts'), '暂时无法读取微信账号');
    updateControls();
  }
}

function updateControls() {
  byId('account-controls').hidden = !current;
  form.hidden = !current;
  const recipient = byId('recipient');
  const notice = byId('account-notice');
  if (!current) {
    setText(recipient, '');
    notice.hidden = true;
    return;
  }
  const { account } = current;
  const [label, state] = accountState(account);
  const unavailable = byId('service-status').dataset.state === 'error';
  const destination = `${accountName(account)} · ${label}`;
  setText(recipient, destination);
  recipient.title = account.remark && account.ownerId ? `${destination} · ${account.ownerId}` : destination;
  byId('edit-remark').disabled = unavailable;
  const toggle = byId('toggle-account');
  setText(toggle, account.running ? '停止接收' : '开始接收');
  toggle.disabled = unavailable || !account.enabled || pendingActions.has(account.accountId) || (!account.running && state === 'warning');
  const refresh = byId('refresh');
  setText(refresh, current.before ? '返回最新' : '刷新');
  refresh.disabled = !!current.loading;
  byId('older').disabled = !!current.loading;
  const historyError = current.items.length && current.error;
  const needsContext = current.ready && account.ownerId && !current.hasSendContext;
  const contextHint = needsContext && (account.running ? '请用绑定的微信先给机器人发一条消息。' : '点击「开始接收」，再用绑定的微信给机器人发一条消息。');
  const text = current.notice || historyError || (state === 'warning' ? label : '') || account.lastError || contextHint || '';
  notice.hidden = !text;
  feedback(notice, text, current.notice ? current.noticeTone : historyError ? 'error' : state === 'warning' || account.lastError ? 'warning' : '');
  const disabled = unavailable || !current.ready || !account.ownerId || state === 'warning';
  const file = messageFile.files[0];
  const fileTooLarge = file?.size > maxAttachmentBytes;
  markdownEditor?.setEditable(!disabled);
  byId('choose-file').disabled = disabled;
  for (const button of formatButtons) {
    const command = button.dataset.format;
    button.disabled = disabled || !markdownEditor?.canRun(command);
    const active = markdownEditor?.isActive(command);
    if (active !== undefined) button.setAttribute('aria-pressed', String(active));
  }
  byId('remove-file').hidden = !file;
  setText(byId('file-name'), fileTooLarge ? '附件不能超过 100 MiB' : file?.name ?? '');
  byId('reply-draft').hidden = !current.replyTo;
  byId('send').disabled = disabled || !markdownEditor || !current.hasSendContext || fileTooLarge ||
    (!draftText.trim() && !file) || pendingSends.has(account.accountId);
}

const sendLabels = { success: '已发送', failed: '发送失败', partial: '部分发送', unknown: '送达未知', sending: '发送中', 'retry-approved': '允许重试' };
const awaitingDeliveryLabel = '微信已受理，手机送达待核对';
const attachmentLabels = { image: '图片', video: '视频', voice: '语音', audio: '音频', file: '文件' };
const mediaExtensions = {
  'image/png': '.png', 'image/jpeg': '.jpg', 'image/gif': '.gif', 'image/webp': '.webp', 'image/bmp': '.bmp',
  'audio/wav': '.wav', 'audio/mpeg': '.mp3', 'audio/ogg': '.ogg', 'video/mp4': '.mp4', 'video/webm': '.webm'
};

function messageSummary(item) {
  const attachment = item.attachments?.[0] ?? item.quote?.attachment;
  const text = item.text ? markdownBody(item.text).textContent.trim() : '';
  return text || attachment?.name || attachmentLabels[attachment?.kind] || '消息';
}

function attachmentFileName(name, disposition, fallback) {
  const basename = value => value?.split(/[/\\]/).pop().replace(/[\u0000-\u001f]/g, '').trim();
  const safeName = basename(name);
  if (safeName && safeName !== '.' && safeName !== '..') return safeName;
  if (!disposition) return fallback;
  const encoded = /;\s*filename\*=UTF-8''([^;]+)/i.exec(disposition)?.[1];
  if (encoded) {
    try {
      const decoded = basename(decodeURIComponent(encoded.trim()));
      if (decoded && decoded !== '.' && decoded !== '..') return decoded;
    } catch { /* Use the plain filename when the encoded alternative is invalid. */ }
  }
  const plain = /;\s*filename=(?:"([^"]*)"|([^;]+))/i.exec(disposition);
  const fromHeader = basename(plain?.[1] ?? plain?.[2]);
  return fromHeader && fromHeader !== '.' && fromHeader !== '..' ? fromHeader : fallback;
}

function renderAttachment(account, item, attachment, signal) {
  const { kind, name, id } = attachment;
  const label = attachmentLabels[kind] ?? '附件';
  const card = node('div', 'message-attachment');
  card.append(node('span', 'attachment-title', name ? label + ' · ' + name : label));
  if (id == null) {
    card.append(node('span', 'attachment-feedback', '附件不可用'));
    return card;
  }

  const openLabel = kind === 'image' ? '预览图片' : kind === 'video' ? '打开视频' : kind === 'file' ? '下载文件' : '打开' + label;
  const button = node('button', 'button secondary', openLabel);
  button.type = 'button';
  if (kind !== 'file') button.setAttribute('aria-expanded', 'false');
  const preview = node('div', 'attachment-preview');
  preview.hidden = true;
  const status = node('span', 'attachment-feedback');
  status.setAttribute('role', 'status');
  card.append(button, status, preview);
  let url;

  function closePreview() {
    const player = preview.querySelector('audio, video');
    if (player) {
      player.pause();
      player.removeAttribute('src');
      player.load();
    }
    preview.replaceChildren();
    preview.hidden = true;
    if (url) URL.revokeObjectURL(url);
    url = undefined;
    setText(status, '');
    setText(button, openLabel);
    if (kind !== 'file') button.setAttribute('aria-expanded', 'false');
  }

  signal.addEventListener('abort', closePreview, { once: true });
  button.addEventListener('click', async () => {
    if (url) { closePreview(); return; }
    button.disabled = true;
    setText(status, '正在下载…');
    try {
      const path = [item.direction, item.id].map(encodeURIComponent).join('/');
      const response = await request(accountPath(account) + '/messages/' + path + '/attachments/' + encodeURIComponent(id), 'GET', undefined, signal);
      const blob = await response.blob();
      if (signal.aborted) return;
      const objectUrl = URL.createObjectURL(blob);
      const mime = blob.type.split(';')[0];
      const inline = Object.hasOwn(mediaExtensions, mime);
      const extension = inline ? mediaExtensions[mime] : '.bin';
      const download = node('a', 'button secondary', '下载附件');
      download.href = objectUrl;
      download.download = attachmentFileName(name, response.headers.get('Content-Disposition'), label + extension);
      if (kind === 'file') {
        download.click();
        setTimeout(() => URL.revokeObjectURL(objectUrl), 1000);
      } else {
        url = objectUrl;
        if (inline) {
          const tag = mime.startsWith('image/') ? 'img' : mime.startsWith('audio/') ? 'audio' : 'video';
          const media = node(tag, 'attachment-media');
          if (tag === 'img') {
            media.alt = name || label;
          } else {
            media.controls = true;
            media.preload = 'metadata';
            media.setAttribute('aria-label', name || label);
          }
          media.addEventListener('error', () => {
            if (!media.isConnected) return;
            media.hidden = true;
            setText(status, '浏览器无法预览，可下载附件。');
          });
          preview.append(media);
          media.src = url;
        }
        preview.append(download);
        preview.hidden = false;
        button.setAttribute('aria-expanded', 'true');
        setText(button, '收起' + label);
      }
      setText(status, kind !== 'file' && !inline ? '浏览器无法预览，可下载附件。' : '');
    } catch (error) {
      if (!signal.aborted) setText(status, error.status === 404 ? '附件不可用' : '下载失败，可重试。');
    } finally {
      button.disabled = false;
    }
  });
  return card;
}

function renderMessage(item, bodyKey) {
  const controller = new AbortController();
  const target = current;
  const row = node('li', 'message');
  const refreshAfterPlayback = () => { if (row.isConnected && !isPlaying(row)) renderHistory(); };
  row.addEventListener('pause', refreshAfterPlayback, true);
  row.addEventListener('ended', refreshAfterPlayback, true);
  row.dataset.direction = item.direction;
  const meta = node('span', 'message-meta');
  const bubble = node('div', 'message-body');
  const attachment = asset => renderAttachment(target.account, item, asset, controller.signal);
  if (item.quote) {
    const quote = node('blockquote', 'message-quote');
    quote.append(node('span', 'quote-label', '引用消息'));
    if (item.quote.text) quote.append(markdownBody(item.quote.text));
    else if (item.quote.missing) quote.append(node('p', '', '引用的消息不可用'));
    if (item.quote.attachment) quote.append(attachment(item.quote.attachment));
    bubble.append(quote);
  }
  const parts = item.parts?.length ? item.parts : [{ text: item.text }, ...(item.attachments ?? []).map(asset => ({ attachment: asset }))];
  const textParts = [];
  for (const part of parts) {
    if (part.text) textParts.push(part.text);
    if (!part.attachment) continue;
    if (textParts.length) bubble.append(markdownBody(textParts.splice(0).join('\n')));
    bubble.append(attachment(part.attachment));
  }
  if (textParts.length) bubble.append(markdownBody(textParts.join('\n')));
  if (!bubble.childElementCount) bubble.append(node('p', '', '无文本内容'));
  row.append(meta, bubble);
  if (item.direction === 'inbound' && item.canReply !== false) {
    const reply = node('button', 'button secondary message-reply', '回复此消息');
    reply.type = 'button';
    reply.addEventListener('click', () => {
      target.replyTo = item;
      setText(byId('reply-preview'), '回复 ' + messageSummary(item));
      updateControls();
      focusEditor();
    });
    row.append(reply);
  }
  return { row, meta, bodyKey, controller };
}

function isPlaying(row) {
  return Array.from(row.querySelectorAll('audio, video')).some(player => !player.paused && !player.ended);
}

function renderHistory(resetScroll = false, older = false) {
  const gap = historyPanel.scrollHeight - historyPanel.scrollTop - historyPanel.clientHeight;
  byId('welcome').hidden = !!current;
  const items = current?.items ?? [];
  const empty = byId('history-empty');
  empty.hidden = !current || items.length > 0;
  if (!empty.hidden) {
    setText(byId('history-empty-heading'), current.error ? '暂时无法读取消息' : current.ready ? '暂无消息' : '正在读取消息…');
    setText(byId('history-empty-detail'), current.error || (current.ready && !current.account.ownerId ? '这个账号未返回已绑定的微信对端。' : ''));
  }
  byId('older-wrap').hidden = !current?.nextBefore;
  messages.hidden = !items.length;
  const keys = items.map(item => item.direction + ':' + item.id);
  const keepPlayback = !resetScroll && items.length > 0;
  let retainedRows = 0;
  for (const [key, entry] of renderedMessages) {
    if (keys.includes(key)) continue;
    if (keepPlayback && isPlaying(entry.row)) {
      retainedRows++;
      continue;
    }
    entry.controller.abort();
    entry.row.remove();
    renderedMessages.delete(key);
  }
  // Keep the playing element attached when only new rows or receipts arrive.
  items.forEach((item, index) => {
    const key = keys[index];
    const bodyKey = JSON.stringify([item.text, item.attachments, item.parts, item.quote, item.canReply]);
    let entry = renderedMessages.get(key);
    if (entry?.bodyKey !== bodyKey && !(keepPlayback && entry && isPlaying(entry.row))) {
      entry?.controller.abort();
      entry?.row.remove();
      entry = renderMessage(item, bodyKey);
      renderedMessages.set(key, entry);
    }
    const date = new Date(item.at);
    const time = Number.isNaN(date.valueOf()) ? '' : clock.format(date);
    const direction = item.direction === 'outbound'
      ? item.awaitingDeliveryConfirmation ? awaitingDeliveryLabel : sendLabels[item.status] ?? '发出'
      : '收到';
    setText(entry.meta, [direction, time].filter(Boolean).join(' · '));
    const nextRow = messages.children[index + retainedRows];
    if (nextRow !== entry.row) messages.insertBefore(entry.row, nextRow ?? null);
  });
  if (older) historyPanel.scrollTop = 0;
  else if (resetScroll || gap < 32) historyPanel.scrollTop = historyPanel.scrollHeight;
}

function resetHistory(target) {
  target.items = [];
  target.ready = false;
  target.hasSendContext = false;
  target.nextBefore = null;
  target.replyTo = null;
  target.error = '';
  renderHistory(true);
}

async function loadHistory(target, before = null) {
  if (!target || target.loading) return;
  const loading = new AbortController();
  target.loading = loading;
  updateControls();
  try {
    const suffix = before ? `?before=${encodeURIComponent(before)}` : '';
    const page = await (await request(`${accountPath(target.account)}/messages${suffix}`, 'GET', undefined, loading.signal)).json();
    if (target !== current || loading.signal.aborted) return;
    if (page.ownerId !== target.account.ownerId) {
      resetHistory(target);
      void refreshAccounts();
      return;
    }
    const items = page.items.slice().reverse();
    const resetScroll = !target.ready || target.before !== before;
    const changed = resetScroll || target.error || target.nextBefore !== page.nextBefore ||
      JSON.stringify(items) !== JSON.stringify(target.items);
    if (target.notice === target.error) target.notice = '';
    target.nextBefore = page.nextBefore;
    target.before = before;
    target.hasSendContext = page.hasSendContext === true;
    target.ready = true;
    target.items = items;
    target.error = '';
    if (changed) renderHistory(resetScroll, resetScroll && !!before);
  } catch (error) {
    if (target !== current || loading.signal.aborted) return;
    target.error = error.status ? error.message : '请检查本机服务，然后刷新。';
    if (!target.items.length) renderHistory();
  } finally {
    if (target.loading === loading) {
      target.loading = null;
      if (target === current) updateControls();
    }
  }
}

list.addEventListener('click', event => {
  const id = event.target.closest('button')?.dataset.accountId;
  if (id && id !== current?.account.accountId) selectAccount(accounts.find(account => account.accountId === id));
});
byId('refresh').addEventListener('click', async () => {
  if (current) current.notice = '';
  await Promise.all([refreshAccounts(), loadHistory(current)]);
});
byId('older').addEventListener('click', () => {
  if (current?.nextBefore) void loadHistory(current, current.nextBefore);
});
byId('edit-remark').addEventListener('click', () => {
  if (!current) return;
  remarkTarget = { account: current.account };
  remarkInput.value = current.account.remark ?? '';
  remarkError.hidden = true;
  remarkSave.disabled = false;
  remarkDialog.showModal();
});
byId('remark-cancel').addEventListener('click', () => remarkDialog.close());
remarkDialog.addEventListener('close', () => { if (!remarkDialog.open) remarkTarget = null; });
byId('remark-form').addEventListener('submit', async event => {
  event.preventDefault();
  const target = remarkTarget;
  if (!target || !remarkDialog.open || remarkSave.disabled) return;
  remarkSave.disabled = true;
  remarkError.hidden = true;
  try {
    await request(accountPath(target.account), 'PUT', { remark: remarkInput.value, expectedOwnerId: target.account.ownerId });
    if (target === remarkTarget) remarkDialog.close();
    await refreshAccounts();
  } catch (error) {
    if (target !== remarkTarget) return;
    const message = error.status === 409 ? '绑定已变更，请刷新账号后重试。' : '备注未能保存，请重试。';
    setText(remarkError, message);
    remarkError.hidden = false;
  } finally {
    if (target === remarkTarget) remarkSave.disabled = false;
  }
});
byId('toggle-account').addEventListener('click', async () => {
  if (!current) return;
  const target = current;
  const { account } = target;
  pendingActions.add(account.accountId);
  target.notice = '';
  updateControls();
  try {
    await request(`${accountPath(account)}/${account.running ? 'stop' : 'start'}`, 'POST');
  } catch (error) {
    target.notice = error.status ? error.message : '接收状态未能确认，请刷新。';
    target.noticeTone = 'error';
  } finally {
    await refreshAccounts();
    pendingActions.delete(account.accountId);
    renderHistory();
    updateControls();
  }
});

formatTools.addEventListener('mousedown', event => {
  if (event.target.closest('[data-format]')) event.preventDefault();
});
formatTools.addEventListener('click', event => {
  const button = event.target.closest('[data-format]');
  if (button && !button.disabled) formatMessage(button.dataset.format);
});
byId('link-cancel').addEventListener('click', () => linkDialog.close());
linkUrl.addEventListener('input', () => linkUrl.setCustomValidity(''));
linkDialog.addEventListener('close', () => {
  if (current && current === linkTarget) focusEditor();
  linkTarget = null;
});
byId('link-form').addEventListener('submit', event => {
  event.preventDefault();
  const url = weixinLink(linkUrl.value.trim());
  if (!url) {
    linkUrl.setCustomValidity('请输入 HTTP 或 HTTPS 链接。');
    linkUrl.reportValidity();
    return;
  }
  const target = linkTarget;
  linkDialog.close();
  if (current !== target) return;
  markdownEditor.setLink(url.href);
  updateDraft();
});
messageFile.addEventListener('change', updateControls);
byId('choose-file').addEventListener('click', () => messageFile.click());
byId('remove-file').addEventListener('click', () => {
  messageFile.value = '';
  updateControls();
  byId('choose-file').focus();
});
byId('remove-reply').addEventListener('click', () => {
  if (current) current.replyTo = null;
  updateControls();
  focusEditor();
});
form.addEventListener('submit', async event => {
  event.preventDefault();
  if (!current || byId('send').disabled) return;
  const target = current;
  const { account } = target;
  const body = markdownEditor.getMarkdown();
  const file = messageFile.files[0];
  const replyTo = target.replyTo;
  const payload = { ownerId: account.ownerId, text: body, replyDeliveryId: replyTo?.id };
  const status = byId('send-status');
  pendingSends.add(account.accountId);
  feedback(status, '正在发送…');
  updateControls();
  try {
    let data = payload;
    if (file) {
      data = new FormData();
      for (const [key, value] of Object.entries(payload)) {
        if (value) data.set(key, value);
      }
      data.set('file', file);
    }
    const path = accountPath(account) + '/messages' + (file ? '/media' : '');
    const result = await (await request(path, 'POST', data)).json();
    if (target !== current) return;
    const unknown = result.status === 'unknown';
    const success = result.status === 'success';
    const partial = result.status === 'partial';
    const awaitingDelivery = success && result.segments.some(segment => !segment.messageId?.trim());
    const label = awaitingDelivery ? `${awaitingDeliveryLabel}。` : sendLabels[result.status] ?? '消息未发送';
    feedback(status, unknown || partial ? `${label}，请在微信中核对后再操作。` : label,
      unknown || partial || awaitingDelivery ? 'warning' : success ? '' : 'error');
    if (success && !awaitingDelivery) {
      if (draftText === body) clearDraft();
      if (messageFile.files[0] === file) messageFile.value = '';
      if (target.replyTo === replyTo) target.replyTo = null;
    }
  } catch (error) {
    if (target !== current) return;
    const unknown = !error.status || error.status >= 500;
    feedback(status, unknown ? '送达结果未知，请在微信中核对后再操作。' : error.message, unknown ? 'warning' : 'error');
    if (error.status === 409) {
      target.hasSendContext = false;
      void refreshAccounts();
    }
  } finally {
    pendingSends.delete(account.accountId);
    updateControls();
    if (target === current) await loadHistory(target, target.before);
  }
});

const loginLabels = {
  starting: '正在生成二维码…', wait: '请使用微信扫一扫。', scaned: '已扫码，请在微信中确认。',
  scaned_but_redirect: '已扫码，请在微信中继续。', need_verifycode: '请输入微信中显示的验证码。',
  need_verifycode_retry: '验证码未通过，请再次输入。', verify_code_blocked: '正在刷新二维码…',
  binded_redirect: '该微信已连接，可以继续使用现有账号。', confirmed: '微信已连接。',
  cancelled: '扫码已取消。', expired: '二维码已过期。', failed: '微信登录失败。'
};

function clearQr() {
  if (qr.src.startsWith('blob:')) URL.revokeObjectURL(qr.src);
  qr.removeAttribute('src');
  qr.hidden = true;
  byId('qr-placeholder').hidden = false;
}

function loginError(message) {
  const field = byId('login-error');
  setText(field, message);
  field.hidden = !message;
}

function closeLogin() {
  const session = login;
  login = null;
  dialog.close();
  clearQr();
  if (!session) return;
  clearTimeout(session.timer);
  if (session.url && !session.finished) void request(session.url, 'DELETE').catch(() => {});
}

async function openLogin() {
  if (login) return;
  const session = {};
  login = session;
  clearQr();
  byId('qr-slot').hidden = false;
  byId('retry-login').hidden = true;
  byId('verification').hidden = true;
  byId('submit-code').disabled = false;
  code.value = '';
  loginError('');
  setText(byId('login-status'), loginLabels.starting);
  dialog.showModal();
  try {
    const started = await (await request('/api/logins', 'POST')).json();
    if (login !== session) {
      void request(started.url, 'DELETE').catch(() => {});
      return;
    }
    session.url = started.url;
    session.qrUrl = started.qrUrl;
    void pollLogin(session);
  } catch (error) {
    if (login !== session) return;
    byId('qr-slot').hidden = true;
    byId('retry-login').hidden = false;
    setText(byId('login-status'), error.status ? error.message : '暂时无法创建登录会话。');
  }
}

async function pollLogin(session) {
  try {
    const snapshot = await (await request(session.url)).json();
    if (login !== session) return;
    setText(byId('login-status'), loginLabels[snapshot.status] ?? '正在等待微信确认…');
    byId('verification').hidden = !snapshot.needsVerificationCode;
    if (snapshot.completed) {
      session.finished = true;
      clearQr();
      byId('qr-slot').hidden = true;
      loginError('');
      if (snapshot.result?.account) {
        closeLogin();
        await refreshAccounts(snapshot.result.account.accountId);
      } else if (snapshot.result?.alreadyConnected) {
        await refreshAccounts();
      } else byId('retry-login').hidden = false;
      return;
    }
    if (snapshot.qrAvailable && session.refreshCount !== snapshot.refreshCount) {
      const response = await request(session.qrUrl);
      const blob = await response.blob();
      if (login !== session) return;
      clearQr();
      qr.src = URL.createObjectURL(blob);
      qr.hidden = false;
      byId('qr-placeholder').hidden = true;
      session.refreshCount = snapshot.refreshCount;
    }
  } catch (error) {
    if (login !== session) return;
    if (error.status === 404) {
      session.finished = true;
      clearQr();
      byId('qr-slot').hidden = true;
      byId('verification').hidden = true;
      byId('retry-login').hidden = false;
      setText(byId('login-status'), '登录会话已失效。');
      return;
    }
    if (error.status !== 409) setText(byId('login-status'), '暂时无法读取登录状态，正在重试…');
  }
  if (login === session) session.timer = setTimeout(() => void pollLogin(session), 1000);
}

byId('add-account').addEventListener('click', () => void openLogin());
byId('welcome-login').addEventListener('click', () => void openLogin());
byId('close-login').addEventListener('click', closeLogin);
byId('retry-login').addEventListener('click', () => { closeLogin(); void openLogin(); });
dialog.addEventListener('cancel', event => { event.preventDefault(); closeLogin(); });
byId('verification').addEventListener('submit', async event => {
  event.preventDefault();
  const session = login;
  if (!session?.url) return;
  byId('submit-code').disabled = true;
  loginError('');
  try {
    await request(`${session.url}/code`, 'POST', { code: code.value });
    if (login === session) code.value = '';
  } catch (error) {
    if (login === session) loginError(error.status ? error.message : '暂时无法提交验证码，请重试。');
  } finally {
    if (login === session) byId('submit-code').disabled = false;
  }
});

void weixinMarkdown.createEditor(messageEditor, updateDraft).then(editor => {
  markdownEditor = editor;
  updateDraft();
}).catch(error => {
  setText(messageEditor, '编辑器加载失败，请刷新页面。');
  console.error(error);
});
void refreshAccounts();
function refreshVisible() {
  if (document.hidden) return;
  void refreshAccounts();
  void loadHistory(current, current?.before);
}
setInterval(refreshVisible, 2500);
document.addEventListener('visibilitychange', refreshVisible);
