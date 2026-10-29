// Brings one product to a target stock level through the public API (orders / restock), for demos.
// Usage: node seed/prepare-demo-product.mjs <SKU> [targetStock]
//   targetStock defaults to the product's reorder threshold + 3, so three single-unit orders show:
//   above threshold -> exactly at threshold (no alert) -> below threshold (LowStockAlert).
const API_URL = process.env.API_URL ?? 'http://localhost:5080';
const [sku, targetArg] = process.argv.slice(2);
if (!sku) {
  console.error('Usage: node seed/prepare-demo-product.mjs <SKU> [targetStock]');
  process.exit(1);
}

const find = async () => {
  const page = await (await fetch(`${API_URL}/api/products?search=${encodeURIComponent(sku)}`)).json();
  const product = page.items.find((p) => p.sku === sku);
  if (!product) throw new Error(`Product ${sku} not found`);
  return product;
};
const post = async (path, body) => {
  const response = await fetch(`${API_URL}${path}`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
  });
  if (!response.ok) throw new Error(`${path} returned ${response.status}`);
};

let product = await find();
const target = targetArg === undefined ? product.reorderThreshold + 3 : Number(targetArg);
if (product.stock > target) await post('/api/orders', { lines: [{ productId: product.id, quantity: product.stock - target }] });
if (product.stock < target) await post(`/api/products/${product.id}/restock`, { quantity: target - product.stock });
product = await find();
console.log(`${product.sku} "${product.name}" (id ${product.id}): stock ${product.stock}, reorder threshold ${product.reorderThreshold}`);
