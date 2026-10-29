# Solutions

Short map of each fix. Full evidence and reasoning: [ROOT_CAUSE_ANALYSIS.md](ROOT_CAUSE_ANALYSIS.md).

### Fix: Concurrent inventory overselling

* **Root cause:** Stock was read, checked in memory, then written as an absolute value (`SET Stock = @newStock`) in its own auto-committed statement, separate from the order insert. Concurrent orders overwrote each other's deductions (lost update), and a failing later line left earlier deductions committed.
* **Fix:** One conditional `UPDATE … SET Stock = Stock - @q WHERE Id = @id AND Stock >= @q`, with 0 affected rows meaning 409/404, inside a transaction that also inserts the order. Added a `CK_Products_Stock_NonNegative` check constraint. Notifications are sent only after commit, and a publish failure no longer fails a committed order.
* **Verification:** `dotnet test` deterministic two-instance last-unit race: baseline `OK, OK` → fixed `OK, Conflict`, stock 0. 40 concurrent orders on stock 10: `10 OK, 30 Conflict, final stock 0`. Live run across two API processes, 300 requests on stock 80: 41 + 39 accepted, stock 0, consistent. `./seed/stress-orders.sh`: baseline 40 order lines from stock 21; fixed 21 lines, stock 0.
* **References:** `backend/Services/OrderService.cs` (`PlaceOrderAsync`), `backend/Data/AppDbContext.cs`, migration `20261029024124_AddNonNegativeStockConstraint`, `tests/Inventory.Api.Tests/OrderConcurrencyTests.cs`, `seed/load-test.mjs`.

### Fix: Inventory list performance

* **Root cause:** N+1 queries. `ProductsController.List` loaded tracked entities and then ran one `Categories` query per row, which is 202 SQL commands for the page's 200-row request. The 250k orders are not involved.
* **Fix:** Project directly to `ProductListDto` with a join (`p.Category.Name`), so a request costs 2 commands (count + page). No index or cache added; the plan analysis showed none is needed.
* **Verification:** EF command log 202 → 2 commands. `GET /api/products?pageSize=200` median 224.9 ms → 11.9 ms (50 requests each, same seeded DB, identical payload of 28,978 bytes). `ProductListTests` 4/4.
* **References:** `backend/Controllers/ProductsController.cs` (`List`), `tests/Inventory.Api.Tests/ProductListTests.cs`, RCA §E.

### Fix: SignalR stock synchronization

* **Root cause:** `SignalRStockNotifier.StockUpdatedAsync` sent to `Clients.Client(requesterConnectionId)` whenever a connection id was supplied. The inventory page always supplies one, so only the ordering user got the update.
* **Fix:** Always broadcast to the `stock-watchers` group. Alerts use `LowStockAlertDto`. The Angular service now keeps restarting a closed connection with backoff and refreshes its data when it comes back.
* **Verification:** `node seed/two-browser-check.mjs`, which drives two isolated headless Chrome contexts: baseline browser B stayed at 41 (FAIL); fixed browser B shows 39 without reload, and a failed order changes nothing. `StockHubTests` 5/5; Karma reconnect specs pass.
* **References:** `backend/Services/IStockNotifier.cs`, `frontend/src/app/stock-realtime.service.ts`, `tests/Inventory.Api.Tests/StockHubTests.cs`, `seed/two-browser-check.mjs`.

### Fix: Order placement refactoring

* **Root cause:** `OrdersController` held validation, stock logic, persistence, transactions and SignalR publishing, while an unused, outdated `OrderService` duplicated it. Cancel had a read-check-write race: 6 of 10 concurrent cancels succeeded.
* **Fix:** `OrderService` owns place, cancel and restock, with transactional stock changes and post-commit publishing; cancel claims the order with a conditional status `UPDATE`. The controller only maps `ServiceResult` to HTTP.
* **Verification:** `OrderContractTests` 13/13 on the original controller and unchanged after the refactor; full backend suite 42/42; `dotnet build` with 0 warnings; `Concurrent_cancellations_return_stock_only_once` passes (pre-refactor: 6 successes).
* **References:** `backend/Services/OrderService.cs`, `backend/Controllers/OrdersController.cs`, `tests/Inventory.Api.Tests/OrderContractTests.cs`.

### Fix: Low-stock alerts

* **Root cause:** The backend already emitted `LowStockAlert` on the ≥ threshold → < threshold transition, but only the Live board rendered it. It tracked alerts by `productId`, so a second alert for the same product produced a duplicate key. On the baseline, stale reads also made the transition unreliable under concurrency.
* **Fix:** Global, dismissable toast (`LowStockAlerts`, `aria-live`) in the app shell, fed by the existing `StockRealtimeService.alerts`. Each alert gets its own id. Listeners are registered once in the root service, and the hub connection is injectable for tests.
* **Verification:** `LowStockAlertTests` 8/8 (no alert above or exactly at the threshold, one when crossing, none on failure or rollback, exactly one under 10 concurrent orders, re-armed after restock). Karma 17/17. Live two-browser run: one toast `Low stock | Hass Avocados 4 Pack | 14 left · reorder at 15` in both browsers; none at 15; none from a failed order; no duplicates after navigation.
* **References:** `frontend/src/app/low-stock-alerts.ts`, `frontend/src/app/stock-realtime.service.ts`, `frontend/src/app/*.spec.ts`, `tests/Inventory.Api.Tests/LowStockAlertTests.cs`.
