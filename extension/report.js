export function browserName(userAgent) {
  return userAgent.includes('Edg/') ? 'Edge' : 'Chrome';
}

export function makeReport(window, tabs) {
  const active = tabs.filter(tab => tab.active && !tab.incognito);
  const ambiguous = active.length !== 1;
  const tab = ambiguous ? undefined : active[0];
  const url = typeof tab?.url === 'string' && tab.url.length <= 6000 ? tab.url : '';
  return {
    type: 'report',
    url,
    focused: Boolean(window?.focused && !window.incognito && !ambiguous),
    ambiguous,
  };
}
