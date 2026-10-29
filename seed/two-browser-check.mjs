// Two-browser SignalR check for the inventory app.
//
// Starts headless Chrome, opens the Angular app in two isolated browser contexts ("browser A" and
// "browser B", separate storage and SignalR connections), places an order by clicking "Order" in
// browser A and reports whether browser B's page shows the new stock without a reload. It then
// places an order that must fail and checks that browser B is not updated by it.
//
// Usage: node seed/two-browser-check.mjs [productSku]
//   APP_URL (default http://localhost:4200), API_URL (default http://localhost:5080),
//   CHROME (path to chrome.exe / google-chrome).
import { spawn } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const APP_URL = process.env.APP_URL ?? 'http://localhost:4200';
const API_URL = process.env.API_URL ?? 'http://localhost:5080';
const SKU = process.argv[2] ?? 'FPR-000021';
const CHROME = process.env.CHROME ?? (process.platform === 'win32'
  ? 'C:/Program Files/Google/Chrome/Application/chrome.exe'
  : 'google-chrome');
const PORT = 9333;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const log = (...args) => console.log(new Date().toISOString().slice(11, 23), ...args);

const profile = mkdtempSync(join(tmpdir(), 'two-browser-'));
const chrome = spawn(CHROME, [
  '--headless=new', `--remote-debugging-port=${PORT}`, `--user-data-dir=${profile}`,
  '--no-first-run', '--no-default-browser-check', 'about:blank',
], { stdio: 'ignore' });

let ws;
let nextId = 0;
const pending = new Map();
const send = (method, params = {}, sessionId) => new Promise((resolve, reject) => {
  const id = ++nextId;
  pending.set(id, { resolve, reject });
  ws.send(JSON.stringify({ id, method, params, sessionId }));
});

async function connect() {
  for (let i = 0; i < 50; i++) {
    try {
      const version = await (await fetch(`http://127.0.0.1:${PORT}/json/version`)).json();
      ws = new WebSocket(version.webSocketDebuggerUrl);
      await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = reject; });
      ws.onmessage = ({ data }) => {
        const message = JSON.parse(data);
        const entry = pending.get(message.id);
        if (!entry) return;
        pending.delete(message.id);
        message.error ? entry.reject(new Error(message.error.message)) : entry.resolve(message.result);
      };
      return;
    } catch { await sleep(200); }
  }
  throw new Error('Could not connect to Chrome');
}

async function openBrowser(name, path) {
  const { browserContextId } = await send('Target.createBrowserContext');
  const { targetId } = await send('Target.createTarget', { url: 'about:blank', browserContextId });
  const { sessionId } = await send('Target.attachToTarget', { targetId, flatten: true });
  await send('Page.enable', {}, sessionId);
  await send('Runtime.enable', {}, sessionId);
  await send('Page.navigate', { url: `${APP_URL}${path}` }, sessionId);
  const page = {
    name,
    eval: async (expression) => {
      const result = await send('Runtime.evaluate',
        { expression, awaitPromise: true, returnByValue: true }, sessionId);
      if (result.exceptionDetails) throw new Error(result.exceptionDetails.text);
      return result.result.value;
    },
  };
  return page;
}

// Reads the stock shown on the inventory ledger row for the SKU.
const rowStock = (sku) => `(() => {
  const row = [...document.querySelectorAll('tbody tr')].find(r => r.querySelector('.sku')?.textContent.trim() === '${sku}');
  return row ? Number(row.querySelector('.stock').textContent.trim()) : null;
})()`;
const connectionState = `document.querySelector('.status')?.textContent.trim()`;

async function waitFor(page, expression, predicate, timeoutMs, label) {
  const deadline = Date.now() + timeoutMs;
  let value;
  while (Date.now() < deadline) {
    value = await page.eval(expression).catch(() => undefined);
    if (predicate(value)) return value;
    await sleep(100);
  }
  throw new Error(`${page.name}: timed out waiting for ${label} (last value ${JSON.stringify(value)})`);
}

async function setQuantity(page, quantity) {
  await page.eval(`(() => {
    const input = document.querySelector('.quantity input');
    input.value = '${quantity}';
    input.dispatchEvent(new Event('input', { bubbles: true }));
  })()`);
}

async function clickOrder(page, sku) {
  await page.eval(`(() => {
    const row = [...document.querySelectorAll('tbody tr')].find(r => r.querySelector('.sku')?.textContent.trim() === '${sku}');
    row.querySelector('.actions button').click();
  })()`);
}

let exitCode = 0;
try {
  await connect();
  const product = (await (await fetch(`${API_URL}/api/products?search=${SKU}`)).json()).items[0];
  log(`Product ${product.sku} "${product.name}" stock ${product.stock}, threshold ${product.reorderThreshold}`);

  const a = await openBrowser('Browser A', '/inventory');
  const b = await openBrowser('Browser B', '/inventory');
  for (const page of [a, b]) {
    await waitFor(page, connectionState, (s) => s === 'live', 15000, 'SignalR live');
    const stock = await waitFor(page, rowStock(SKU), (v) => typeof v === 'number', 15000, 'product row');
    log(`${page.name}: connection live, shows stock ${stock}`);
  }

  // 1. Successful order placed through browser A's UI.
  await setQuantity(a, 1);
  await clickOrder(a, SKU);
  const expected = product.stock - 1;
  log(`Browser A: clicked Order (qty 1), expecting stock ${expected}`);
  const shownA = await waitFor(a, rowStock(SKU), (v) => v === expected, 5000, `stock ${expected}`);
  log(`Browser A: shows stock ${shownA}`);
  try {
    const shownB = await waitFor(b, rowStock(SKU), (v) => v === expected, 5000, `stock ${expected}`);
    log(`Browser B: shows stock ${shownB} without reload  -> PASS`);
  } catch (error) {
    log(`Browser B: ${error.message}  -> FAIL (not updated)`);
    exitCode = 1;
  }

  // 2. An order that must fail (more than available) must not change browser B.
  const before = await b.eval(rowStock(SKU));
  await setQuantity(a, 100000);
  await clickOrder(a, SKU);
  await waitFor(a, `document.querySelector('.error')?.textContent.trim()`, (t) => !!t, 5000, 'error message');
  log(`Browser A: failed order shows "${await a.eval(`document.querySelector('.error').textContent.trim()`)}"`);
  await sleep(1500);
  const after = await b.eval(rowStock(SKU));
  const dbStock = (await (await fetch(`${API_URL}/api/products/${product.id}`)).json()).stock;
  const unchanged = after === before && dbStock === before;
  log(`Browser B after failed order: ${after} (before ${before}, database ${dbStock})  -> ${unchanged ? 'PASS' : 'FAIL'}`);
  if (!unchanged) exitCode = 1;
} catch (error) {
  log(`ERROR: ${error.message}`);
  exitCode = 2;
} finally {
  ws?.close();
  chrome.kill();
  await sleep(500);
  try { rmSync(profile, { recursive: true, force: true }); } catch { /* Chrome may still hold files */ }
}
process.exit(exitCode);
