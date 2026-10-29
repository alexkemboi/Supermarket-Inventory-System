# Root Cause Analysis — Rapid Inventory

Scope: three reported symptoms (overselling, slow inventory list, stale live stock board) plus the requested
order-placement refactor and low-stock alerts. Every claim below is backed by a command that was executed in
this repository; measurements are marked as measured, anything else as hypothesis.

---

## A. Executive Summary

| Symptom | Root cause (proven) | Fix | Status |
|---|---|---|---|
| Customers can order more than is in stock | Order placement read stock, checked it in memory, then wrote an **absolute** value (`SET Stock = @newStock`) in a separate auto-committed statement. Concurrent requests overwrote each other's deductions (lost update). Stock never went negative — the oversell shows up as more units in order lines than were ever deducted. No transaction spanned deduction and order insert, so a failing later line left earlier deductions committed. | Single conditional `UPDATE … SET Stock = Stock - @q WHERE Id = @id AND Stock >= @q` inside a transaction that also inserts the order; `CHECK (Stock >= 0)` constraint; post-commit, best-effort notifications. | Verified (integration tests on SQL Server, two live API processes) |
| Inventory list is slow | N+1 query: the list loaded full tracked `Product` entities and then ran **one `Categories` query per row**. The page requests 200 rows → **202 SQL commands** per page load. The 250k seeded orders are not touched by this endpoint. | Project to `ProductListDto` with a join: 2 commands (count + page). | Verified: median 225 ms → 11.9 ms (measured) |
| Live stock board does not update for other users | `SignalRStockNotifier.StockUpdatedAsync` sent to `Clients.Client(requesterConnectionId)` whenever a connection id was supplied. The inventory page **always** sends its connection id, so only the ordering user received the update. | Always broadcast to the `stock-watchers` group. | Verified (two isolated headless Chrome contexts + hub integration tests) |
| Business logic in controller | `OrdersController` owned validation, stock checks, deduction, persistence and SignalR; an unused, outdated `OrderService` duplicated it. | `OrderService` owns place/cancel/restock; controller maps `ServiceResult` → HTTP. | Verified: 13 contract tests that passed on the original controller pass unchanged |
| Low-stock alerts not visible | Backend already emitted `LowStockAlert` on the threshold transition, but only the *Live board* page rendered it (not the page where orders are placed) and it keyed alerts by `productId`, so repeat alerts for a product collided. | Global, dismissable toast in the app shell fed by the existing `StockRealtimeService.alerts`; per-alert ids. | Verified (unit, integration and live two-browser checks) |

Additional defect found and fixed during the refactor: concurrent cancellation of the same order restored its
stock repeatedly (**6 of 10** concurrent cancels succeeded on the pre-refactor code).

Main residual risks (section K): SignalR has no backplane, so with several API instances a client only receives
events from the instance it is connected to; notifications are at-most-once; there is no idempotency key for
client retries; a single hot product serialises all orders on one row lock. The walkthrough video has **not**
been recorded.

---

## B. Environment and Reproduction

| Component | Version (observed) |
|---|---|
| ASP.NET Core / runtime in API container | .NET SDK 9.0.320, Microsoft.AspNetCore.App 9.0.22 |
| EF Core | Microsoft.EntityFrameworkCore.SqlServer 9.0.10 |
| SQL Server | `mcr.microsoft.com/mssql/server:2022-latest` → ProductVersion 16.0.4305.5 |
| Angular / SignalR client / TypeScript | @angular/core 20.3.34, @microsoft/signalr 10.0.11, TypeScript 5.9.3 |
| Host | Windows 11, Docker 29.8.2 / Compose v5.6.0, .NET SDK 10.0.401 (+ 9.0.22 runtime), Node 24.21.0, Chrome 156 |

**Startup:** `docker compose up --build` (README). Seed observed after start: 2,000 products, 250,000 orders,
20 categories. Health: `GET http://localhost:5080/health` → `{"status":"healthy"}`; web app on :4200.

**Setup issues that affected verification**

1. *No Git history was present* (`fatal: not a git repository`). A baseline commit of the untouched project
   (`fc3c873`) was created first so every change is reviewable as a diff.
2. *Port 1433 conflict on the host.* A local `MSSQL$SQLEXPRESS` service listens on `0.0.0.0:1433`; Docker
   publishes the container on 1433 too, so `localhost,1433`, `127.0.0.1,1433` and `[::1],1433` from the host all
   reached SQL Express (`Login failed for user 'sa'`). The containers themselves are unaffected (the API uses
   `db:1433`). Workaround without touching the repo or the user's service: an **uncommitted** compose override
   publishing the db container additionally on host port 14333:
   ```bash
   # compose.local.yml (not in the repo):  services: { db: { ports: ["14333:1433"] } }
   docker compose -f docker-compose.yml -f compose.local.yml up -d db
   ```
3. *Slow image download* (~40 min for the .NET SDK image); SQL Server started first so baseline tests could run.
4. `dotnet watch` hot-reloads code but **does not run migrations**; the API container was restarted
   (`docker compose restart api`) after the migration was added.
5. `seed/stress-orders.sh` under Git Bash on Windows under-counts its own status tally (concurrent `>>` appends
   lose lines: it printed `2 200` for 40 requests). Database counts were used as the source of truth and
   `seed/load-test.mjs` was added to report exact counts.

**Test commands** (SQL Server must be reachable; the connection string is taken from the environment, falling
back to `backend/appsettings.json`; each test class creates and drops its own `InventoryTests_<guid>` database):
```bash
export ConnectionStrings__DefaultConnection="Server=tcp:127.0.0.1,14333;Database=Inventory;User Id=sa;Password=<compose password>;TrustServerCertificate=True"
dotnet build Inventory.sln
dotnet test Inventory.sln
CHROME_BIN=".../chrome.exe" npx --prefix frontend ng test --watch=false --browsers=ChromeHeadless   # from frontend/
npm --prefix frontend run build
node seed/two-browser-check.mjs FPR-000021            # live stack, headless Chrome
node seed/load-test.mjs orders --urls http://localhost:5080,http://localhost:5081 --product 5 --requests 300 --concurrency 60
./seed/stress-orders.sh
```

---

## C. Root Cause Analysis

### C1. Inventory overselling under concurrent orders

**1. Reported symptom.** Customers sometimes order more stock than is available.

**2. Technical root cause.** `OrdersController.Place` (baseline `fc3c873`) per line:
```csharp
var product = await db.Products.Where(p => p.Id == line.ProductId)
    .Select(p => new { p.Id, p.Name, p.UnitPrice, p.Stock, p.ReorderThreshold }).SingleOrDefaultAsync(ct);
if (product.Stock < line.Quantity) return Conflict(...);
var newStock = product.Stock - line.Quantity;
await db.Products.Where(p => p.Id == line.ProductId)
    .ExecuteUpdateAsync(update => update.SetProperty(p => p.Stock, newStock), ct);   // absolute value
...
db.Orders.Add(order);
await db.SaveChangesAsync(ct);                                                         // separate transaction
```
SQL captured from the baseline API (run from a worktree of `fc3c873`, EF command log):
```sql
SELECT TOP(2) [p].[Id], [p].[Name], [p].[UnitPrice], [p].[Stock], [p].[ReorderThreshold]
FROM [Products] AS [p] WHERE [p].[Id] = @__line_ProductId_0
UPDATE [p] SET [p].[Stock] = @__newStock_1 FROM [Products] AS [p] WHERE [p].[Id] = @__line_ProductId_0
INSERT INTO [Orders] ([OrderDate], [Status]) OUTPUT INSERTED.[Id] VALUES (@p0, @p1);
INSERT INTO [OrderLines] ([OrderId], [ProductId], [Quantity], [UnitPrice]) OUTPUT INSERTED.[Id] VALUES (...);
```
Under READ COMMITTED (RCSI is off), two requests can both read `Stock = 1`, both pass the check and both write
`0` — a classic lost update. Because the `UPDATE` is unconditional and absolute, stock is never driven negative;
instead the sum of order-line quantities exceeds the stock actually deducted. The `RowVersion` concurrency token
on `Product` is bypassed by `ExecuteUpdate`. The deduction (auto-committed) and the order insert
(`SaveChanges`) are separate transactions, so an early return on line 2 leaves line 1's deduction committed.
A failure in SignalR publishing after `SaveChanges` propagated as HTTP 500 for an order that already existed.

**3. Evidence** (baseline code, `dotnet test` against SQL Server):

| Test | Baseline result |
|---|---|
| `Two_instances_racing_for_the_last_unit_cannot_both_succeed` (deterministic, see D) | `Last-unit race results: OK, OK` — both instances sold the last unit |
| `Many_concurrent_orders_across_two_instances_never_exceed_stock` (stock 10, 40 requests) | `40 OK, 0 Conflict; final stock 6; units in order lines 40` |
| `Concurrent_orders_of_mixed_quantities_…` (stock 25) | 73 units accepted, final stock 5 |
| `Failure_on_a_later_line_rolls_back_earlier_deductions` | stock 3 (expected 5): first line's deduction persisted |
| `Notification_failure_after_commit_…` | `InvalidOperationException` → 500 although the order was committed |
| `Concurrent_orders_crossing_the_threshold_raise_exactly_one_alert` | 10 orders accepted, stock only dropped by 5 |

Live stack, `./seed/stress-orders.sh` (40 × qty 1, product 1, stock 21): **40 order lines / 40 units recorded,
stock 21 → 16**. Overall baseline run: 40 tests, 31 passed, 9 failed (all 9 are the defects above or in C2/C3).

**4. Corrective action** (`fix: prevent concurrent stock overselling`, now in `OrderService.PlaceOrderAsync`):
- One `BeginTransactionAsync` around all deductions and the order insert; any early return or exception
  disposes the transaction without commit → full rollback.
- Check-and-deduct in one statement: `Where(p => p.Id == id && p.Stock >= q).ExecuteUpdateAsync(Stock - q)`;
  affected rows = 0 → 409 (or 404 if the product does not exist) and rollback.
- Post-deduction values are read inside the same transaction while the row is exclusively locked, so
  low-stock transitions are computed from exact before/after values.
- `CK_Products_Stock_NonNegative` (`[Stock] >= 0`, migration `20261029024124_AddNonNegativeStockConstraint`)
  as a final guard; `ProductsController` Create/Update now return 400 for negative stock instead of hitting it.
- Notifications are published only after commit, with `CancellationToken.None`, inside `try/catch` + warning log:
  a committed order is always reported as success, so clients are not induced to retry and duplicate it.

**5. Verification** (fixed code): all 12 tests in `OrderConcurrencyTests` pass, e.g. `Last-unit race results:
OK, Conflict` and `40 requests: 10 OK, 30 Conflict, other 0; final stock 0; units in order lines 10`.
Live: stress script on product 2 (stock 21) → 21 order lines, stock 0. Two real API processes (Docker :5080 and
host :5081) sharing the database, `seed/load-test.mjs`, 300 requests / 60 concurrent against stock 80:
`200 via 5080: 41, 200 via 5081: 39, 409: 220, stock before=80 after=0 consistent=true`.

**6. Residual risks.** Lines are deducted in request order, so two multi-line orders touching the same products
in opposite order can deadlock; SQL Server aborts one (error 1205 → HTTP 500) and its transaction rolls back
fully — consistent, but not a clean 409 (not observed in tests; the UI only sends single-line orders). No
idempotency key: a client that times out and retries after a successful commit creates a second order. A hot
product serialises all its orders on one row (see J).

### C2. Slow inventory list

**1. Reported symptom.** The inventory page responds slowly.

**2. Technical root cause.** `ProductsController.List` (baseline) materialised entities and looked up each
category separately:
```csharp
var products = await query.OrderBy(p => p.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
foreach (var product in products)
{
    var category = await db.Categories.Where(c => c.Id == product.CategoryId).Select(c => c.Name).SingleAsync(ct);
    items.Add(new ProductListDto(...));
}
```
The Angular page (`ApiService.products`) requests `pageSize=200`, so each page load costs 1 count + 1 page query
+ 200 category queries. The page query also selects unused columns (`CategoryId` for the lookup, `RowVersion`)
and tracks every entity. The 250k-row `Orders` table is not referenced by this endpoint.

**3. Evidence.** EF command log for one `GET /api/products?pageSize=200` on the seeded DB: **202 `Executed
DbCommand` entries** — `SELECT COUNT(*)`, the page query, then 200 × `SELECT TOP(2) [c].[Name] FROM [Categories]
AS [c] WHERE [c].[Id] = @__product_CategoryId_0`. Test `Inventory_page_request_uses_a_constant_number_of_sql_commands`
reported `SQL commands executed: 202` on the baseline.

**4. Corrective action** (`perf: remove N+1 category lookups from inventory list`): projection with a join;
filtering, ordering and paging stay in SQL; projections are not tracked. No index and no cache were added (see E).

**5. Verification.** 2 commands per request (test + log); `ProductListTests` (4) pass, including an exact
comparison with a reference query, search, clamping and paging. Response payload byte-identical (28,978 bytes).
Latency in section E.

**6. Residual risks.** Search is `LIKE '%term%'` (non-sargable): a full scan of `Products`, cheap at 2,000 rows
(41 logical reads) but linear in catalogue size. The separate `COUNT(*)` doubles work for search requests.

### C3. SignalR stock synchronisation failure

**1. Reported symptom.** The live stock board does not update for other users.

**2. Technical root cause.** Hub registration (`/hubs/stock`), Angular URL (`/hubs/stock`, proxied with
`ws: true`), event names (`StockUpdated`, `LowStockAlert`), argument shape `(productId, stock)` and group join
(`JoinStockWatchers`, also re-invoked on reconnect) all matched. The defect was the targeting in
`SignalRStockNotifier`:
```csharp
var clients = requesterConnectionId is null
    ? hub.Clients.Group(StockWatchersGroup)
    : hub.Clients.Client(requesterConnectionId);
```
`Inventory.order()` always passes `this.realtime.connectionId`, so every order's update went only to the user who
placed it (whose page reloads over HTTP anyway). Restock passes no connection id — which is why restocks *did*
reach other users. Secondary: after `withAutomaticReconnect` exhausts its retries (~42 s) `onclose` only set
`offline`; nothing restarted the connection.

**3. Evidence.** Hub integration test `Order_placed_by_one_browser_updates_stock_in_the_other_browser` failed on
the baseline (timeout) while `Restock_updates_stock_in_every_browser` passed. Live two-browser run
(`seed/two-browser-check.mjs`, two isolated headless Chrome contexts on `/inventory`):
`Browser A: shows stock 40` / `Browser B: timed out waiting for stock 40 (last value 41) -> FAIL`.

**4. Corrective action** (`fix: broadcast stock updates to all watching clients`): `StockUpdated` always goes to
the `stock-watchers` group (interface and request contract unchanged; the requester applies the same value
idempotently). Alerts use the existing `LowStockAlertDto`. The Angular service restarts a closed connection with
capped exponential backoff and requests a data refresh when it comes back. Notifications are sent only after
commit (C1), so failed orders publish nothing.

**5. Verification.** `StockHubTests` 5/5 (real hub over TestServer, two clients). Live:
`Browser B: shows stock 39 without reload -> PASS` and `Browser B after failed order: 39 (before 39, database 39)
-> PASS`. Reconnect logic covered by Karma specs (`restarts the connection after it closes…`,
`reports a failed start and keeps retrying…`); a live API-restart test with open browsers was **not** run.

**6. Residual risks.** No SignalR backplane: with more than one API instance, `IHubContext` only reaches clients
connected to the same instance (architectural fact; not exercised). At-most-once delivery; concurrent
publishes are not versioned, so a client could briefly apply an older value if two events arrive out of order
(hypothesis, not observed); the next refresh corrects it.

### C4. Excessive business logic in the order controller

**1. Symptom.** Order rules, persistence, transactions and notifications lived in `OrdersController`
(`Place`, `Cancel`, `Restock` ≈ 90 lines), while `Services/OrderService.cs` held a stale copy that no endpoint
used (registered in DI only) — with the same oversell bug and a duplicate-product-line rule the live endpoint
never enforced.

**2/3. Root cause & evidence.** Duplicated, diverging implementations (diff of `fc3c873`). Cancel used
read-check-write on `Order.Status` without a transaction and published before `SaveChanges`; restock published
a value computed from a pre-read. Test `Concurrent_cancellations_return_stock_only_once` on the pre-refactor
code: `Expected: 1, Actual: 6` successful cancels.

**4. Corrective action** (`refactor: extract order placement into OrderService`): see section F. The live
endpoint's behaviour was kept (duplicate lines remain allowed, as in the original controller).

**5. Verification.** `OrderContractTests` (13 tests: status codes, `{ error }` messages, `{ id }` body, cancel and
restock semantics, recent orders) passed on the original controller and pass unchanged after the refactor; full
suite 42/42; `dotnet build` 0 warnings; live two-browser and stress checks repeated after the refactor.

**6. Residual risks.** `GET /api/orders/recent` remains in the controller (read-only query, not analysed in
depth). `PUT /api/products/{id}` still overwrites stock absolutely without the client sending `RowVersion`; an
admin edit can overwrite concurrent order deductions (out of scope, noted).

### C5. Missing low-stock notifications

**1. Symptom.** Users do not see low-stock alerts when orders deplete stock.

**2/3. Root cause & evidence.** The model already has `Product.ReorderThreshold` (seeded 15) and the backend
already raised `LowStockAlert` when `previous >= threshold && current < threshold`. Rendering existed only in
`board.html`; the inventory page — where orders are placed — showed nothing, and `@for (… track
alert.productId)` collides when the same product alerts twice (e.g. after a restock). On the baseline the
transition was also computed from stale reads, so concurrent orders could skip or duplicate the alert (C1
evidence: stock dropped by 5 for 10 accepted orders).

**4. Corrective action** (`feat: show low stock alerts as global toasts`): `LowStockAlerts` component in the
app shell (`aria-live="polite"`, `role="status"`, dismiss button with accessible label) rendering
`StockRealtimeService.alerts`; each received alert gets a client id (`LowStockNotice`); board tracks by id; hub
connection injected through `STOCK_HUB_CONNECTION` so listener registration is testable. No new threshold field,
no second notification system.

**5. Verification.** `LowStockAlertTests` 8/8, `StockHubTests.Low_stock_alert_reaches_every_connected_browser`,
Karma 17/17, live two-browser run: exactly-at-threshold → no toast in either browser; crossing to 14 → one toast
`Low stock | Hass Avocados 4 Pack | 14 left · reorder at 15` in **both** browsers; failed order → no new toast;
navigating B to the board and back → still one toast.

**6. Residual risks.** Alerts are not persisted: a user who connects after the event never sees it, and a page
reload clears the list. Changing `ReorderThreshold` via `PUT` raises no alert.

---

## D. Concurrency Analysis

**Mechanism.** An atomic conditional update inside a transaction (READ COMMITTED, the SQL Server default),
backed by a `CHECK` constraint. Implementation (`backend/Services/OrderService.cs`):
```csharp
await using var transaction = await db.Database.BeginTransactionAsync(ct);
foreach (var line in request.Lines)
{
    var deducted = await db.Products
        .Where(p => p.Id == line.ProductId && p.Stock >= line.Quantity)
        .ExecuteUpdateAsync(s => s.SetProperty(p => p.Stock, p => p.Stock - line.Quantity), ct);
    if (deducted == 0)
        return await db.Products.AnyAsync(p => p.Id == line.ProductId, ct)
            ? new ServiceResult.Conflict($"Insufficient stock for product {line.ProductId}.")
            : new ServiceResult.NotFound($"Product {line.ProductId} not found.");
    // read post-deduction values (row is X-locked by this transaction) and build the order line …
}
db.Orders.Add(order);
await db.SaveChangesAsync(ct);
await transaction.CommitAsync(ct);
await PublishAsync(changes, …);   // after commit, best effort
```
Captured SQL (fixed code, EF command log):
```sql
UPDATE [p] SET [p].[Stock] = [p].[Stock] - @__line_Quantity_1
FROM [Products] AS [p]
WHERE [p].[Id] = @__line_ProductId_0 AND [p].[Stock] >= @__line_Quantity_1
SELECT TOP(2) [p].[Id], [p].[Name], [p].[UnitPrice], [p].[Stock], [p].[ReorderThreshold] FROM [Products] AS [p] WHERE [p].[Id] = @__line_ProductId_0
INSERT INTO [Orders] ([OrderDate], [Status]) OUTPUT INSERTED.[Id] VALUES (@p0, @p1);
INSERT INTO [OrderLines] ([OrderId], [ProductId], [Quantity], [UnitPrice]) OUTPUT INSERTED.[Id] VALUES (...);
```

**Walk-through: two orders for the last unit arrive in the same millisecond on two API instances.**
Stock = 1. Instance A (transaction T1) and instance B (T2) each open their own connection and transaction.

1. Both send the `UPDATE`. SQL Server seeks `PK_Products` and must take an **update (U) lock** on the row
   before it evaluates the predicate. U locks are mutually exclusive, so exactly one transaction — say T1 —
   gets it; T2 waits in the lock queue. Which one wins is decided by the lock manager, not by the API.
2. T1 evaluates `Stock >= 1` (true), converts U → X, writes `Stock = 0`, affected rows = 1. T1 keeps the
   X lock until commit, inserts the order and line, commits.
3. T2 is granted the lock only after T1 commits and **re-evaluates the predicate against the committed row**:
   `0 >= 1` is false → affected rows = 0. Nothing was changed by T2.
4. Instance B sees `deducted == 0`, checks existence (true) and returns **409 "Insufficient stock"**; its
   transaction is disposed without commit (no order row, no line, no notification).

Outcome: one 200, one 409, stock 0, one order line. Had T2 got the lock first, the roles would simply swap.

**Why no in-process lock is needed.** The serialisation point is the row lock in SQL Server, which every API
instance must go through; the check and the write are the same statement, so there is no window between
"read" and "write" for another request to use. Nothing depends on memory shared between requests or processes.
Even a bug elsewhere cannot persist negative stock because of `CK_Products_Stock_NonNegative`.

**How the test proves it.** `Two_instances_racing_for_the_last_unit_cannot_both_succeed` hosts two independent
API instances (two `WebApplicationFactory` hosts: separate DI containers, DbContexts and connection pools) on
one database. A test transaction first takes an X lock on the product row; one order is sent to each instance;
the test polls `sys.dm_exec_requests` until **both** requests are blocked in the database, then commits — so
both reach the contended statement at the same instant, every run. Baseline: `OK, OK`; fixed: `OK, Conflict`,
stock 0, exactly one order line. The live two-process run in C1 shows the same invariant under load.

**Guarantee and limits.** Guaranteed: successful orders never take more than the stock that existed; stock is
never negative; a failed order leaves no order rows and no deductions; low-stock transitions are computed from
exact values. Not guaranteed: no idempotency for client retries; no ordering of multi-line lock acquisition
(deadlock → rollback + 500); throughput for one product is bounded by serial execution on its row.

---

## E. Query Performance Analysis

**Before (LINQ):** see C2. **Before (captured SQL, 202 commands):**
```sql
SELECT COUNT(*) FROM [Products] AS [p]
SELECT [p].[Id], [p].[CategoryId], [p].[Name], [p].[ReorderThreshold], [p].[RowVersion], [p].[Sku], [p].[Stock], [p].[UnitPrice]
FROM [Products] AS [p] ORDER BY [p].[Id] OFFSET @__p_0 ROWS FETCH NEXT @__p_1 ROWS ONLY
-- then 200 times:
SELECT TOP(2) [c].[Name] FROM [Categories] AS [c] WHERE [c].[Id] = @__product_CategoryId_0
```
**After (LINQ):**
```csharp
var items = await query
    .OrderBy(p => p.Id).Skip((page - 1) * pageSize).Take(pageSize)
    .Select(p => new ProductListDto(p.Id, p.Name, p.Sku, p.Category.Name, p.UnitPrice, p.Stock, p.ReorderThreshold))
    .ToListAsync(ct);
```
**After (captured SQL, 2 commands):**
```sql
SELECT COUNT(*) FROM [Products] AS [p]
SELECT [p0].[Id], [p0].[Name], [p0].[Sku], [c].[Name], [p0].[UnitPrice], [p0].[Stock], [p0].[ReorderThreshold]
FROM (
    SELECT [p].[Id], [p].[CategoryId], [p].[Name], [p].[ReorderThreshold], [p].[Sku], [p].[Stock], [p].[UnitPrice]
    FROM [Products] AS [p] ORDER BY [p].[Id] OFFSET @__p_0 ROWS FETCH NEXT @__p_1 ROWS ONLY
) AS [p0]
INNER JOIN [Categories] AS [c] ON [p0].[CategoryId] = [c].[Id]
ORDER BY [p0].[Id]
```
**Execution plan (after, `SHOWPLAN_TEXT`):** `Nested Loops (Inner Join)` ← `Top(200)` ← `Clustered Index Scan
(PK_Products), ORDERED FORWARD` (no sort operator), inner `Clustered Index Seek (PK_Categories)`.
`STATISTICS IO`: Products 6 logical reads, Categories 400 logical reads (200 seeks), CPU 4 ms. Search count
(`LIKE '%Milk%'` on Name or Sku): full scan, 41 logical reads, CPU 8 ms. Table sizes: Products 2,000 rows /
328 KB; Orders 250,061 rows (untouched by this endpoint).

**Index decision.** None added: paging uses the clustered PK in order; the join seeks the Categories PK; the
search predicate is a leading-wildcard `LIKE` that no B-tree index can seek.

**Measurements** (measured; same seeded DB, API container restarted before each series, 5 warm-up requests,
then 50 sequential `curl` requests from the host):

| Request | Before median / p95 | After median / p95 |
|---|---|---|
| `GET /api/products?pageSize=200` (inventory page) | 224.9 ms / 261.0 ms | **11.9 ms / 15.5 ms** |
| `…&search=Milk` | 95.4 ms / 114.7 ms | 22.8 ms / 29.9 ms |
| `?page=3&pageSize=50` | 68.6 ms / 88.7 ms | 7.2 ms / 8.7 ms |
| SQL commands per page request | 202 | 2 |
| Payload (pageSize=200) | 28,978 bytes | 28,978 bytes |

Caveat: the API runs `dotnet watch` in Development with EF command logging at Information; absolute numbers
include that logging overhead, but both series ran under identical conditions.

---

## F. Architectural Refactoring

| Responsibility | Before | After |
|---|---|---|
| Input validation (empty order, quantity ≤ 0) | `OrdersController` | `OrderService.ValidateOrder` |
| Stock check + deduction | Controller (read → check → absolute write) | `OrderService` (conditional UPDATE in transaction) |
| Order construction & persistence, transaction | Controller (no transaction) | `OrderService` |
| Cancel / restock | Controller (no transaction; publish before save / stale value) | `OrderService.CancelOrderAsync` (conditional status claim) / `RestockAsync` |
| Low-stock evaluation | Controller (inline) | `OrderService.IsLowStockTransition` |
| SignalR publication | Controller, before/after save inconsistently | `OrderService.PublishAsync` (post-commit, best effort) via `IStockNotifier` |
| HTTP mapping | Mixed with logic | `OrdersController.ToActionResult(ServiceResult)` |

No new interfaces, repositories or layers were introduced: `OrderService`, `ServiceResult` and `IStockNotifier`
already existed. Concurrency protection stays at the database statement / transaction boundary. Behavioural
equivalence is established by `OrderContractTests`, written and run against the original controller first
(13/13 pass) and unchanged afterwards (13/13 pass); the intentional changes (no oversell, rollback of failed
multi-line orders, exactly-once cancel, success despite notification failure) are each covered by a test that
failed before.

---

## G. Low Stock Alert Flow

Actual sequence:

1. `POST /api/orders` → `OrdersController.Place` → `OrderService.PlaceOrderAsync`.
2. Conditional `UPDATE` deducts stock; within the same transaction the post-deduction row is read →
   `previous = stock + quantity`, `current = stock`, `threshold = ReorderThreshold`.
3. Order + lines inserted; **transaction commits**.
4. `PublishAsync`: for each line, `StockUpdated(productId, current)` to group `stock-watchers`; if
   `previous >= threshold && current < threshold`, `LowStockAlert` with
   `LowStockAlertDto { productId, productName, stock, reorderThreshold }` to the same group.
5. Angular `StockRealtimeService` (root singleton, handlers registered once in its constructor) receives
   `LowStockAlert`, prepends a `LowStockNotice` with a client id (max 5).
6. `LowStockAlerts` in the app shell renders a toast on every page; the Live board also lists it.

**Threshold semantics:** an alert is raised only by the order that moves stock from ≥ threshold to strictly
< threshold. Reaching the threshold exactly does not alert; further orders while already below do not alert;
a restock back to ≥ threshold re-arms it. Because each transaction reads its own exact before/after values
under the row lock, concurrent orders produce exactly one alert per crossing (test: 10 concurrent orders
20 → 10 with threshold 15 → one alert at 14). Failed or rolled-back orders publish nothing (tests).

**Delivery limits:** publication happens after commit and is at-most-once. If the process dies or SignalR fails
between commit and publish, the stock change is durable but the alert is lost (logged as a warning); there is no
outbox. Clients that are disconnected at that moment never receive it. Guaranteed delivery would need an outbox
table plus a dispatcher — not added (no evidence it is required, and it adds infrastructure).

---

## H. Verification Results

| Area | Verification | Actual Result | Status |
|---|---|---|---|
| Inventory concurrency | Deterministic two-instance last-unit test | Baseline `OK, OK`; fixed `OK, Conflict`, stock 0, 1 order line | Pass |
| Inventory integrity | 40 concurrent orders / stock 10; mixed quantities; rollback tests; CHECK constraint; two live processes | `10 OK, 30 Conflict, final 0, 10 units`; live 300 req / stock 80 → 41 + 39 accepted, stock 0, consistent | Pass |
| Query performance | Before/after captured SQL, command count, latency, plan | 202 → 2 commands; median 224.9 → 11.9 ms; same payload | Pass |
| SignalR synchronization | Two isolated headless Chrome contexts (`seed/two-browser-check.mjs`) + `StockHubTests` | Baseline B stayed 41 (FAIL); fixed B shows 39 without reload; failed order no update; hub tests 5/5 | Pass (automated browser; manual human-operated two-browser test not performed) |
| Order refactoring | `OrderContractTests` before and after; full suite | 13/13 before and after; 42/42 total | Pass |
| Low-stock alerts | `LowStockAlertTests`, hub test, Karma specs, live two-browser run | 8/8, 1/1, 17/17 Karma; live: none at 15, one toast in both browsers at 14, none on failure, no duplicates | Pass |
| Backend build | `dotnet build Inventory.sln` | Build succeeded, 0 warnings, 0 errors | Pass |
| Frontend build | `npm run build` (production) | Exit 0, initial bundle 388 kB, no budget warnings | Pass |
| Existing stress script | `./seed/stress-orders.sh` | Baseline: 40 lines from stock 21 (stock 16). Fixed: 21 lines, stock 0 (script's own tally unreliable on Windows) | Pass |
| SignalR reconnection | Karma specs for reconnect/restart | Pass; live outage test not run | Partial |

---

## I. AI-Assisted Development Reflection

**Tool.** The investigation, code and documents in this branch were produced with **Claude Code (Claude Opus
5.5)** acting as an agent in this repository, working from the assessment brief as its prompt. The prompt was
the full task brief ("Supermarket Inventory System — Root Cause Analysis, Bug Fixes, Performance Optimization,
and Refactoring", sections 1–13) plus one follow-up instruction to commit and push to the GitHub repository.
The human reviewer should treat all of this as AI-generated work and verify it independently.

**Approaches that were corrected because the evidence contradicted them** (all happened in this session):

| Initial approach / assumption | What caught it | Correction |
|---|---|---|
| The first hypothesis framed overselling as "stock goes negative". | Baseline data: stock was never negative; 40 orders were recorded while stock only moved 21 → 16. | Documented as a lost update; tests assert on order-line totals vs. deducted stock, not only on negativity. |
| The deterministic race helper counted only requests whose `blocking_session_id` was the test's locking session. | The test timed out: `Only 1 of 2 requests reached the locked row`. SQL Server reports a queued waiter as blocked by the waiter ahead of it. | Count any blocked request in the test database; warm both hosts before taking the lock. |
| Assumed `localhost,1433` reached the Docker SQL Server. | All tests failed with `Login failed for user 'sa'`; `Get-NetTCPConnection` showed `sqlservr` (SQL Express) on `0.0.0.0:1433`; `[::1]` also failed. | Uncommitted override on port 14333; tests take the connection string from the environment. |
| Assumed `dotnet watch` would apply the new migration. | `__EFMigrationsHistory` still showed only `InitialCreate`. | Restarted the API container; verified the constraint in `sys.check_constraints`. |
| First multi-instance load run was taken as proof of cross-instance contention. | All 26 successes came from one instance (the other was cold). | Re-ran warm: 41 / 39 split, still consistent. |
| Container CPU samples taken during the list load test. | The run finished (~3 s) before the sample (4 s). | Those samples are excluded from section J. |
| Spec assertion on toast `textContent`. | Karma failure: Angular strips inter-element whitespace. | Assert per element. |

Accepted design choices (with the reason they were preferred over alternatives): conditional atomic UPDATE over
EF optimistic concurrency (`RowVersion` + retry loop) because it needs no retries, so it cannot duplicate an
order on retry, and it handles the last-unit case in one round-trip; over `UPDLOCK`/`SERIALIZABLE` because the
single statement already holds the only lock that matters. No caching/indexing for the list, because the
measurements showed the N+1 round-trips, not data volume, were the cost.

**Independent verification.** Every defect was reproduced against the untouched baseline before fixing (tests
written first and run on `fc3c873`); tests run against real SQL Server (not the in-memory provider); the race is
forced deterministically through SQL Server's lock manager; two separate API processes were exercised live;
SQL was captured from EF's command log rather than written by hand; browser behaviour was checked in real
Chrome. Remaining gaps are listed in K.

---

## J. Capacity and Scaling Assessment — "If traffic doubled, what breaks first?"

Measured (single API container in Development mode, local Docker on an 8-CPU / 16 GB host, closed-loop
`seed/load-test.mjs`, concurrency doubled from 20 to 40):

| Workload | c = 20 | c = 40 | Observation |
|---|---|---|---|
| `GET /api/products?pageSize=200` (3,000 req) | 877.6 req/s, p50 20.4 ms, p95 38.0 ms | 1,042.9 req/s, p50 34.4 ms, p95 63.1 ms | +19% throughput for 2× load; latency rising — approaching saturation |
| Orders on **one** product (800 req) | 134.8 req/s, p50 140 ms, p95 183 ms | 120.7 req/s, p50 326 ms, p95 459 ms | Throughput flat/declining, latency more than doubles; stock consistent in both runs |
| Container CPU during the order runs | api 85%, db 63% | api 67%, db 49% | single sample each |
| Separate run: 60 concurrent orders on one product across two instances | p95 1,072 ms | | |

**Assessment.** For order traffic, the first thing to degrade is **row-lock contention on popular products**:
every order for a product holds that row's exclusive lock for the whole transaction (UPDATE, SELECT, two INSERTs,
commit ≈ 7.4 ms per order measured via Little's law: 20 / 134.8 req/s ≈ 148 ms ≈ p50). Doubling demand on a hot
product doubles queueing latency rather than throughput; under a sale spike requests would start hitting the
30 s command timeout and the 100-connection default pool. This is the price of correctness, and it is measured.

Hypotheses not yet measured:
- **API CPU** was the busiest container during order load (85%). Likely contributor: Development configuration
  (`dotnet watch`, EF logging every command at Information to the console). A Release build with Warning-level
  logging should be measured before drawing conclusions.
- **SignalR fan-out:** every order broadcasts to all watchers; with N connected boards the message count is
  orders × N. Not load-tested; with more than one instance a backplane is required anyway.
- **Database:** CPU 49–63% at ~130 orders/s on one product; disk/log I/O not measured. The list query is cheap
  (≈ 3 ms CPU) and is not the first bottleneck after the fix.

**Next measurements:** Release build behind the production Dockerfile stage with reduced logging; orders spread
across many products (to separate lock contention from raw capacity); SignalR with hundreds of connected
clients; SQL Server wait statistics (`sys.dm_os_wait_stats`, LCK_M_U vs. PAGEIOLATCH/WRITELOG) and
`sys.dm_exec_query_stats` under load; connection-pool counters; container resource limits (none are set in
`docker-compose.yml`).

---

## K. Remaining Risks and Recommendations

1. **SignalR scale-out:** add a backplane (Redis or Azure SignalR Service) before running more than one API
   instance; otherwise users miss events from other instances.
2. **Notification durability:** at-most-once after commit; consider a transactional outbox if alerts must not be
   lost. Clients reconcile on reconnect (data refresh).
3. **Idempotency:** add an `Idempotency-Key` (unique index on orders) so client retries after timeouts cannot
   create duplicate orders.
4. **Multi-line deadlocks:** acquire product locks in a consistent order (sort by product id) or map error 1205
   to 409/retry.
5. **Hot products:** if sales spikes are expected, keep transactions minimal (e.g. `OUTPUT` in the UPDATE to save
   the follow-up SELECT) and measure.
6. **`PUT /api/products/{id}`** overwrites stock absolutely without a client-supplied `RowVersion`.
7. **Search:** leading-wildcard `LIKE`; consider full-text search if the catalogue grows.
8. **Not executed:** human-operated two-browser test (an automated two-context headless Chrome test was run
   instead); live API-outage reconnect test; Release-build load test; deadlock scenario.
9. **Walkthrough video:** **not recorded** (no recording tool available to the agent). A timed script is in
   `docs/WALKTHROUGH_SCRIPT.md`.
10. **Host environment:** a local SQL Server on port 1433 conflicts with the compose setup (README requirement);
    the override used is documented in section B.

### Git history (this branch)

```
fc3c873 chore: baseline snapshot of provided project (no prior git history present)
6bae192 test: add SQL Server integration tests reproducing order, list and SignalR issues
6028cbd test: make last-unit race deterministic across queued lock waiters
6157517 fix: prevent concurrent stock overselling
2f076ee perf: remove N+1 category lookups from inventory list
d8329cc fix: broadcast stock updates to all watching clients
75569d9 refactor: extract order placement into OrderService
1bfb08b feat: show low stock alerts as global toasts
280ac2d test: add dependency-free load generator for multi-instance order checks
```
followed by the documentation commit. No history was rewritten or squashed.
