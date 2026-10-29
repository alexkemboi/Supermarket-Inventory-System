# Walkthrough Video — Demonstration Script

> **Status: NOT YET RECORDED.** The agent that produced this branch had no screen-recording tool. This is the
> timed script for a single continuous 12–14 minute recording. Every command below was executed while
> preparing the branch, and the results quoted are the ones observed then. Re-run each command on camera and
> show the real output; do not paste these numbers in.

**Before recording:** `docker compose up --build` is healthy; SQL Server is reachable from the host (see
ROOT_CAUSE_ANALYSIS.md §B if a local SQL Server occupies port 1433); `export ConnectionStrings__DefaultConnection=…`;
`frontend/node_modules` is installed; and one terminal, VS Code and two browser windows (one normal, one private)
are open on http://localhost:4200.

| Time | Show | Say / point out |
|---|---|---|
| 0:00–1:00 | `README.md`, the repo tree, and `git log --oneline` | Stack (ASP.NET Core 9, EF Core 9, SQL Server 2022, Angular 20, SignalR). The three reported symptoms. The baseline commit `fc3c873` (no history was supplied), then one commit per fix. |
| 1:00–3:00 | `git show fc3c873:backend/Controllers/OrdersController.cs` (Place) | Read → check → `SET Stock = @newStock`; no transaction. |
| | `git diff fc3c873 -- backend/Services/OrderService.cs` | Conditional UPDATE + transaction + CHECK constraint. |
| | `dotnet test --filter "FullyQualifiedName~OrderConcurrencyTests" --logger "console;verbosity=detailed"` | `Last-unit race results: OK, Conflict`; `40 requests: 10 OK, 30 Conflict … final stock 0`. Explain how the test holds a row lock until both instances are blocked. |
| | `node seed/load-test.mjs orders --urls http://localhost:5080,http://localhost:5081 --product <id> --requests 300 --concurrency 60` (second instance started with `dotnet run --project backend --urls http://localhost:5081`) | Both instances win some orders; accepted units = stock; `consistent=true`. |
| 3:00–5:00 | `git show 2f076ee` | N+1 loop → projection. |
| | The EF command log for one `GET /api/products?pageSize=200` (`docker compose logs api`) | Before: 202 commands (in RCA §E). After: 2 commands. Read both SQL statements. |
| | `dotnet test --filter "FullyQualifiedName~ProductListTests" --logger "console;verbosity=detailed"` | `SQL commands executed: 2`. |
| | RCA §E table | Median 224.9 → 11.9 ms; why no index (PK range + nested-loop seek; leading-wildcard LIKE). |
| 5:00–7:00 | Browser A and Browser B on *Ledger*, same product row | Same stock in both. |
| | Click **Order** in A | A updates; B updates without a refresh. |
| | Set qty to 100000, click **Order** in A | Error in A; B unchanged. |
| | `git show d8329cc -- backend/Services/IStockNotifier.cs` | `Clients.Client(requester)` was the root cause. |
| | `node seed/two-browser-check.mjs FPR-000021` | The automated version of the same check (all PASS). |
| 7:00–9:00 | `git show 75569d9 --stat`, then the old and new `OrdersController` | Responsibilities moved into `OrderService`; the controller only maps results. |
| | `dotnet test --filter "FullyQualifiedName~OrderContractTests"` | 13/13; the same tests passed on the original controller. Concurrent-cancel test (6 → 1). |
| 9:00–10:30 | `OrderService.IsLowStockTransition` | ≥ threshold → < threshold; exactly at the threshold gives no alert. |
| | In the browser, take a product to threshold + 1 and order 1 (no toast), then order 1 more | A toast appears in both browsers; a failed order adds no toast. |
| | `frontend/src/app/low-stock-alerts.ts`, `stock-realtime.service.ts` | Listeners registered once in the singleton; per-alert ids; dismiss. |
| 10:30–12:00 | `dotnet build Inventory.sln`; `dotnet test Inventory.sln`; `npx ng test --watch=false --browsers=ChromeHeadless`; `npm run build` | 0 warnings; 42/42; 17/17; build exit 0. |
| | `git log --oneline`; open `SOLUTIONS.md` and `ROOT_CAUSE_ANALYSIS.md` | Incremental commits; no rewritten history. |
| 12:00–14:00 | Q1: RCA §D | Walk through the U/X locks, predicate re-evaluation, and the losing request's 0 rows → 409 rollback; why no in-process lock is needed; the deterministic test. |
| | Q2: RCA §E SQL blocks | Before: 202 statements, entity columns including RowVersion. After: a single join with only the DTO columns. |
| | Q3: RCA §I | The prompt (the assessment brief), the corrected approaches and how each was caught. |
| | Q4: RCA §J | Measured hot-row contention (throughput flat, p50 140 → 326 ms when concurrency doubles); hypotheses (API CPU under Development logging, SignalR fan-out); next measurements. |
