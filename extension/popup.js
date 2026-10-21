const status = document.querySelector('#status');
const input = document.querySelector('#token');
async function refresh() {
  const result = await chrome.storage.session.get('status');
  status.textContent = result.status || '等待连接';
}
document.querySelector('#pair').addEventListener('submit', async event => {
  event.preventDefault();
  const token = input.value.trim();
  if (!/^[A-Fa-f0-9]{48}$/.test(token)) { status.textContent = '配对码应为 48 位。请从软件重新复制。'; return; }
  await chrome.storage.local.set({ token });
  input.value = '';
  status.textContent = '配对码已保存，正在连接…';
});
chrome.storage.onChanged.addListener((changes, area) => { if (area === 'session' && changes.status) void refresh(); });
void refresh();
