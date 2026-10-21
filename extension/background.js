import { browserName, makeReport } from './report.js';

let socket;
let timer;
let retry;
let paired = false;
let connecting = false;

async function status(text) {
  await chrome.storage.session.set({ status: text });
  await chrome.action.setBadgeText({ text: paired ? 'ON' : '' });
  await chrome.action.setBadgeBackgroundColor({ color: '#67561C' });
}

async function report() {
  if (!paired || socket?.readyState !== WebSocket.OPEN) return;
  try {
    const window = await chrome.windows.getLastFocused({ windowTypes: ['normal'] });
    const tabs = await chrome.tabs.query({ active: true, windowId: window.id });
    if (paired && socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify(makeReport(window, tabs)));
  } catch {
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ type: 'report', url: '', focused: false, ambiguous: true }));
  }
}

async function connect() {
  if (connecting || socket?.readyState === WebSocket.OPEN || socket?.readyState === WebSocket.CONNECTING) return;
  connecting = true;
  try {
    const { token } = await chrome.storage.local.get('token');
    if (typeof token !== 'string' || !/^[A-Fa-f0-9]{48}$/.test(token)) {
      await status('请先粘贴 Windows 软件的本机配对码');
      return;
    }
    const current = new WebSocket('ws://127.0.0.1:17839/browser');
    socket = current;
    current.onopen = () => current.send(JSON.stringify({ type: 'auth', token, browser: browserName(navigator.userAgent) }));
    current.onmessage = event => {
      try {
        if (JSON.parse(event.data).type !== 'ready') return;
        paired = true;
        void status('已连接本机软件 · 仅发送当前页面地址');
        clearInterval(timer);
        // Messages within 30 seconds keep the MV3 service worker alive on Chrome 116+.
        timer = setInterval(() => { void report(); }, 5000);
        void report();
      } catch { /* Ignore unrecognised local messages. */ }
    };
    current.onclose = () => {
      if (socket !== current) return;
      paired = false; socket = undefined;
      clearInterval(timer);
      void status('未连接，请确认软件已运行和配对码正确');
      clearTimeout(retry);
      retry = setTimeout(() => { void connect(); }, 4000);
    };
    current.onerror = () => current.close();
  } finally { connecting = false; }
}

chrome.tabs.onActivated.addListener(() => { void report(); });
chrome.tabs.onUpdated.addListener((_id, change) => { if (change.url || change.status === 'complete') void report(); });
chrome.windows.onFocusChanged.addListener(() => { void report(); });
chrome.storage.onChanged.addListener((changes, area) => {
  if (area !== 'local' || !changes.token) return;
  paired = false;
  clearInterval(timer); clearTimeout(retry);
  const previous = socket; socket = undefined; previous?.close();
  void connect();
});
chrome.alarms.onAlarm.addListener(alarm => { if (alarm.name === 'reconnect') void connect(); });
chrome.runtime.onStartup.addListener(() => { void connect(); });
chrome.runtime.onInstalled.addListener(() => { void chrome.alarms.create('reconnect', { periodInMinutes: 1 }); void connect(); });
void chrome.alarms.create('reconnect', { periodInMinutes: 1 });
void connect();
