export function validToken(token) {
  return typeof token === 'string' && /^[A-Fa-f0-9]{48}$/.test(token);
}

export async function readPairingToken(storage, installationToken) {
  const saved = await storage.get(['token', 'pairingSource']);
  // A deliberate manual pairing takes precedence. Installer-owned pairing follows a refreshed desktop key.
  if (validToken(saved.token) && saved.pairingSource !== 'installer') return saved.token;
  if (validToken(installationToken)) {
    if (saved.token !== installationToken || saved.pairingSource !== 'installer')
      await storage.set({ token: installationToken, pairingSource: 'installer' });
    return installationToken;
  }
  return validToken(saved.token) ? saved.token : null;
}
