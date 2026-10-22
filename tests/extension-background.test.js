import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, copyFile, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve, sep } from 'node:path';
import { pathToFileURL } from 'node:url';
import { readPairingToken } from '../extension/pairing.js';
import { installationToken } from '../extension/install-config.js';

const keyA = 'A'.repeat(48), keyB = 'B'.repeat(48);

function storage(initial = {}, changed = () => {}) {
  const values = { ...initial };
  return {
    values,
    async get() { return { ...values }; },
    async set(update) {
      const changes = {};
      for (const [key, value] of Object.entries(update)) {
        if (values[key] !== value) changes[key] = { oldValue: values[key], newValue: value };
        values[key] = value;
      }
      if (Object.keys(changes).length) changed(changes, 'local');
    },
  };
}

test('release extension contains no machine pairing credential', () => assert.equal(installationToken, ''));
test('installer pairing initializes an empty profile and follows a refreshed desktop key', async () => {
  const saved = storage();
  assert.equal(await readPairingToken(saved, keyA), keyA);
  assert.equal(saved.values.pairingSource, 'installer');
  assert.equal(await readPairingToken(saved, keyB), keyB);
  assert.equal(saved.values.token, keyB);
});
test('manual and legacy manual pairing override installer data', async () => {
  for (const pairingSource of [undefined, 'manual']) {
    const saved = storage({ token: keyB, pairingSource });
    assert.equal(await readPairingToken(saved, keyA), keyB);
    assert.equal(saved.values.token, keyB);
  }
});
test('invalid installation data never becomes an authentication credential', async () => {
  const saved = storage({ token: 'bad', pairingSource: 'installer' });
  assert.equal(await readPairingToken(saved, "'; fetch('https://example.com')"), null);
  assert.equal(saved.values.token, 'bad');
});

async function runWorker(preparedKey, savedValues, action) {
  const directory = await mkdtemp(join(tmpdir(), 'dglab-extension-worker-'));
  const savedGlobals = Object.fromEntries(['chrome', 'WebSocket', 'navigator', 'setTimeout', 'clearTimeout', 'setInterval', 'clearInterval'].map(name => [name, Object.getOwnPropertyDescriptor(globalThis, name)]));
  const listeners = {}, sockets = [], pendingTimers = new Set();
  const event = name => ({ addListener(callback) { listeners[name] = callback; } });
  const local = storage(savedValues, (changes, area) => listeners.storage?.(changes, area));
  const session = storage();
  class Socket {
    static OPEN = 1; static CONNECTING = 0;
    readyState = 0; sent = [];
    constructor(url) { assert.equal(url, 'ws://127.0.0.1:17839/browser'); sockets.push(this); }
    send(text) { this.sent.push(JSON.parse(text)); }
    open() { this.readyState = 1; this.onopen?.(); }
    ready() { this.onmessage?.({ data: '{"type":"ready"}' }); }
    close() { this.readyState = 3; this.onclose?.(); }
  }
  try {
    for (const name of ['background.js', 'report.js', 'pairing.js']) await copyFile(new URL('../extension/' + name, import.meta.url), join(directory, name));
    await writeFile(join(directory, 'package.json'), '{"type":"module"}');
    await writeFile(join(directory, 'install-config.js'), `export const installationToken = ${JSON.stringify(preparedKey)};`);
    Object.defineProperty(globalThis, 'navigator', { configurable: true, value: { userAgent: 'Chrome/140' } });
    globalThis.chrome = {
      storage: { local, session, onChanged: event('storage') },
      action: { async setBadgeText() {}, async setBadgeBackgroundColor() {} },
      tabs: { onActivated: event('activated'), onUpdated: event('updated'), async query() { return [{ active: true, url: 'https://learn.example.com/lesson' }]; } },
      windows: { onFocusChanged: event('focus'), async getLastFocused() { return { id: 1, focused: true }; } },
      alarms: { onAlarm: event('alarm'), async create() {} },
      runtime: { id: 'a'.repeat(32), onMessage: event('message'), onStartup: event('startup'), onInstalled: event('installed') },
    };
    globalThis.WebSocket = Socket;
    globalThis.setTimeout = globalThis.setInterval = callback => { const timer = { callback }; pendingTimers.add(timer); return timer; };
    globalThis.clearTimeout = globalThis.clearInterval = timer => pendingTimers.delete(timer);
    await import(pathToFileURL(join(directory, 'background.js')));
    const flush = async () => { for (let i = 0; i < 8; i++) await new Promise(resolve => setImmediate(resolve)); };
    await flush();
    await action({ local, session, sockets, listeners, flush });
  } finally {
    for (const [name, descriptor] of Object.entries(savedGlobals)) {
      if (descriptor) Object.defineProperty(globalThis, name, descriptor); else delete globalThis[name];
    }
    if (!resolve(directory).startsWith(resolve(tmpdir()) + sep + 'dglab-extension-worker-')) throw new Error('Unsafe fixture cleanup path');
    await rm(directory, { recursive: true, force: true });
  }
}

test('prepared worker automatically authenticates and reports a page only after bridge ready', async () => {
  await runWorker(keyA, {}, async ({ sockets, session, local, flush }) => {
    assert.equal(sockets.length, 1);
    sockets[0].open();
    assert.deepEqual(sockets[0].sent, [{ type: 'auth', token: keyA, browser: 'Chrome' }]);
    assert.equal(local.values.pairingSource, 'installer');
    assert.ok(!session.values.status?.includes('已连接'));
    sockets[0].ready(); await flush();
    assert.ok(session.values.status.includes('已连接'));
    assert.equal(sockets[0].sent[1].url, 'https://learn.example.com/lesson');
  });
});
test('worker without installed or saved pairing asks for setup instead of opening an unauthenticated socket', async () => {
  await runWorker('', {}, async ({ sockets, session }) => {
    assert.equal(sockets.length, 0);
    assert.match(session.values.status, /配对码/);
  });
});
test('refreshing installer-owned pairing does not create a reconnect loop', async () => {
  await runWorker(keyB, { token: keyA, pairingSource: 'installer' }, async ({ sockets, local }) => {
    assert.equal(sockets.length, 1); sockets[0].open();
    assert.equal(sockets[0].sent[0].token, keyB);
    assert.equal(local.values.token, keyB);
  });
});
test('manual reconnect works for an unchanged key and ignores another extension sender', async () => {
  await runWorker(keyA, { token: keyB, pairingSource: 'manual' }, async ({ sockets, listeners, flush, session }) => {
    sockets[0].open(); sockets[0].ready(); await flush();
    listeners.message({ type: 'reconnect' }, { id: 'other-extension' }, () => {});
    assert.equal(sockets.length, 1);
    listeners.message({ type: 'reconnect' }, { id: 'a'.repeat(32) }, () => {}); await flush();
    assert.equal(sockets.length, 2); sockets[1].open();
    assert.equal(sockets[1].sent[0].token, keyB);
    sockets[1].close(); await flush();
    assert.match(session.values.status, /未连接/);
  });
});
