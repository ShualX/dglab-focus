import test from 'node:test';
import assert from 'node:assert/strict';
import { browserName, makeReport } from '../extension/report.js';

test('distinguishes Edge from Chrome user agents', () => {
  assert.equal(browserName('Chrome/130 Edg/130'), 'Edge');
  assert.equal(browserName('Chrome/130'), 'Chrome');
});
test('sends current page and window focus separately', () => {
  const report = makeReport({ focused: false }, [{ active: true, url: 'https://example.com/lesson?id=2#topic' }]);
  assert.equal(report.focused, false);
  assert.equal(report.url, 'https://example.com/lesson?id=2#topic');
  assert.equal(report.ambiguous, false);
});
test('abstains when no active tab or more than one active tab is reported', () => {
  for (const tabs of [[], [{ active: true }, { active: true }]]) {
    assert.deepEqual(makeReport({ focused: true }, tabs), { type: 'report', url: '', focused: false, ambiguous: true });
  }
});
test('excludes incognito tab data', () => {
  const report = makeReport({ focused: true, incognito: true }, [{ active: true, incognito: true, url: 'https://private.example' }]);
  assert.equal(report.focused, false);
  assert.equal(report.url, '');
});
test('does not send tab title or page content', () => {
  const report = makeReport({ focused: true }, [{ active: true, url: 'https://example.com/', title: 'secret', content: 'private' }]);
  assert.deepEqual(Object.keys(report).sort(), ['ambiguous', 'focused', 'type', 'url']);
});
test('oversized page address is omitted', () => {
  assert.equal(makeReport({ focused: true }, [{ active: true, url: 'x'.repeat(6001) }]).url, '');
});
