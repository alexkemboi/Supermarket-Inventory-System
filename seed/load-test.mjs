// Small closed-loop load generator (Node 18+, no dependencies).
//
// Orders mode: concurrent single-unit orders spread round-robin over one or more API instances, then
// prints status counts and the product's final stock so oversell can be checked:
//   node seed/load-test.mjs orders --urls http://localhost:5080,http://localhost:5081 --product 5 --requests 200 --concurrency 50
// List mode: repeated inventory-page requests, prints throughput and latency percentiles:
//   node seed/load-test.mjs list --urls http://localhost:5080 --requests 2000 --concurrency 20
const [mode = 'orders', ...rest] = process.argv.slice(2);
const option = (name, fallback) => {
  const index = rest.indexOf(`--${name}`);
  return index >= 0 ? rest[index + 1] : fallback;
};
const urls = option('urls', 'http://localhost:5080').split(',');
const productId = Number(option('product', '1'));
const quantity = Number(option('quantity', '1'));
const requests = Number(option('requests', mode === 'orders' ? '40' : '1000'));
const concurrency = Number(option('concurrency', '20'));
const listPath = option('path', '/api/products?pageSize=200');

const statuses = new Map();
const latencies = [];
let issued = 0;

async function one(i) {
  const base = urls[i % urls.length];
  const started = performance.now();
  let status;
  try {
    const response = mode === 'orders'
      ? await fetch(`${base}/api/orders`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ lines: [{ productId, quantity }] }),
        })
      : await fetch(`${base}${listPath}`);
    await response.arrayBuffer();
    status = `${response.status}`;
  } catch (error) {
    status = `error:${error.cause?.code ?? error.message}`;
  }
  latencies.push(performance.now() - started);
  const key = `${status} via ${new URL(base).port}`;
  statuses.set(key, (statuses.get(key) ?? 0) + 1);
}

async function worker() {
  while (issued < requests) await one(issued++);
}

const percentile = (sorted, p) => sorted[Math.min(sorted.length - 1, Math.floor(sorted.length * p))];

const before = mode === 'orders'
  ? (await (await fetch(`${urls[0]}/api/products/${productId}`)).json()).stock
  : undefined;
const startedAt = performance.now();
await Promise.all(Array.from({ length: concurrency }, worker));
const seconds = (performance.now() - startedAt) / 1000;

const sorted = latencies.sort((a, b) => a - b);
console.log(`mode=${mode} requests=${requests} concurrency=${concurrency} instances=${urls.join(',')}`);
for (const [key, count] of [...statuses].sort()) console.log(`  ${key}: ${count}`);
console.log(`  throughput=${(requests / seconds).toFixed(1)} req/s  p50=${percentile(sorted, 0.5).toFixed(1)}ms ` +
  `p95=${percentile(sorted, 0.95).toFixed(1)}ms p99=${percentile(sorted, 0.99).toFixed(1)}ms max=${sorted.at(-1).toFixed(1)}ms`);
if (mode === 'orders') {
  const after = (await (await fetch(`${urls[0]}/api/products/${productId}`)).json()).stock;
  const ok = [...statuses].filter(([k]) => k.startsWith('200')).reduce((sum, [, c]) => sum + c, 0);
  console.log(`  stock before=${before} after=${after} accepted units=${ok * quantity} ` +
    `consistent=${before - after === ok * quantity && after >= 0}`);
}
