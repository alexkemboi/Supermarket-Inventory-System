# Evidence Index — Rapid Inventory Release 1.1.0 (proposed)

Companion to `Rapid_Tech_Insights_Release_Note_and_Test_Evidence.docx` / `.pdf` (Section 5).

- **Where captured:** all artifacts were captured on 29 Oct 2026 (UTC times below; local time is EAT, UTC+3), on the local Docker Compose stack (api, web, db; SQL Server 2022 16.0.4305.5), Windows 11 host.
- **Screenshots:** taken from the running application in two isolated headless Chrome contexts through the Chrome DevTools Protocol, and not edited.
- **Logs:** verbatim command and test output.
- **Baseline runs:** "Baseline" means the original application code (commit `fc3c873`), run to reproduce the defects.

Issues: INV-001 overselling · INV-002 slow inventory list · INV-003 SignalR sync · INV-004 controller refactor · INV-005 low-stock alerts.

## A — Startup

| ID | File | Issue / tests | Observation | Status |
|---|---|---|---|---|
| EV-A1 | `Release_Evidence/logs/EV-A1_docker_compose_first_start_excerpt.log` | Install | `docker compose up --build`: images pulled/built, db Healthy, migrations applied, 250k-order seed, API + web started | PASS |
| EV-A2 | `Release_Evidence/logs/EV-A2_services_health_and_seed_state.txt` | Install; INV-001 · TC-11 | `/health` healthy, web 200, 2,000 products / 20 categories, both migrations, CHECK constraint, 0 negative-stock rows, 0 orphan orders (03:19 UTC) | PASS |

## B — Inventory concurrency (INV-001)

| ID | File | Tests | Observation | Status |
|---|---|---|---|---|
| EV-B0 | `logs/EV-B0_stress_orders_script_BASELINE.txt` | TC-14 (baseline) | Original API (:5082): stock 30 → 13 while 40 units were ordered (defect reproduced) | PASS (repro) |
| EV-B1 | `logs/EV-B1_backend_tests_BASELINE_before_fixes.log` | TC-04, TC-05, TC-23, TC-31, TC-40…44 | Original code: 40 tests, 31 pass / 9 fail. Last-unit race `OK, OK`; 40 OK from stock 10; 202 SQL commands; contract tests pass | PASS (repro) |
| EV-B2 | `logs/EV-G3_backend_tests_FINAL_42_passed.log`, `logs/EV-G3a_backend_test_case_results.txt` | TC-01…TC-12 | Fixed: last-unit `OK, Conflict`; 10 OK / 30 Conflict, stock 0; 12/12 concurrency tests pass | PASS |
| EV-B3 | `logs/EV-B3_two_api_instances_load_test.txt` | TC-13 | Two real API processes: 41 + 39 accepted = stock 80, stock 0, consistent | PASS |
| EV-B4 | `logs/EV-B4_stress_orders_script_after_fix.txt` | TC-14 | Fixed API: stock 29 → 0, exactly 29 units ordered, 0 negative rows | PASS |
| EV-B5 | `logs/EV-B5_order_SQL_BEFORE_baseline_api.log` | INV-001 root cause | Captured SQL: `SELECT` then absolute `SET Stock = @__newStock_1` | — |
| EV-B6 | `logs/EV-B6_order_SQL_AFTER.log` | INV-001 fix | Captured SQL: `SET Stock = Stock - @q WHERE Id = @id AND Stock >= @q` | — |

## C — Inventory performance (INV-002)

| ID | File | Tests | Observation | Status |
|---|---|---|---|---|
| EV-C1 | `logs/EV-C1_inventory_list_SQL_BEFORE_202_commands.log` | TC-22 | 202 commands per page request (N+1 category lookups) | PASS |
| EV-C2 | `logs/EV-C2_inventory_list_SQL_AFTER_2_commands.log` | TC-22, TC-23 | 2 commands; single join, DTO columns only | PASS |
| EV-C3 | `logs/EV-C3_inventory_list_latency_BEFORE.txt` | TC-20 | Median 224.9 ms, p95 261.0 ms (pageSize=200) | PASS |
| EV-C4 | `logs/EV-C4_inventory_list_latency_AFTER.txt` | TC-21 | Median 11.9 ms, p95 15.5 ms; payload unchanged | PASS |
| EV-C5 | `logs/EV-C5_statistics_io_time.txt` | TC-24 | Products 6 logical reads, Categories 400; search scan 41 reads | PASS |
| EV-C6 | `logs/EV-C6_execution_plan_after.txt` | TC-24 | Ordered PK scan + TOP + nested-loop PK seek; no sort | PASS |
| EV-C7 | `code/EV-C7_ProductsController_list_fix.diff` | INV-002 fix | Code change | — |

## D — SignalR synchronisation (INV-003)

| ID | File | Tests | Observation | Status |
|---|---|---|---|---|
| EV-D0 | `logs/EV-D0_two_browser_check_BASELINE_fail.txt` | TC-31 (baseline) | Browser B stayed 41 while DB 40 (defect reproduced) | PASS (repro) |
| EV-D1 | `screenshots/EV-D1_browserA_initial_stock.png` | TC-30 | A shows FPR-000041 stock 17 (03:18:33 UTC) | PASS |
| EV-D2 | `screenshots/EV-D2_browserB_initial_stock.png` | TC-31 | B shows 17 | PASS |
| EV-D3 | `screenshots/EV-D3_browserA_after_order.png` | TC-30 | A shows 16 after Order | PASS |
| EV-D4 | `screenshots/EV-D4_browserB_updated_without_refresh.png` | TC-31 | B shows 16 without reload, 4 ms after A | PASS |
| EV-D5 | `screenshots/EV-D5_browserA_failed_order_error.png` | TC-32, TC-55 | Qty 100000 rejected: "Order could not be placed." | PASS |
| EV-D6 | `screenshots/EV-D6_browserB_unchanged_after_failed_order.png` | TC-32, TC-55 | B unchanged (14), 0 new SignalR frames | PASS |
| EV-D7 | `logs/EV-D7_two_browser_check_after_fix.txt` | TC-31, TC-32 | Automated two-browser check after fix: PASS / PASS | PASS |
| EV-D8 | `code/EV-D8_IStockNotifier_fix.diff` | INV-003 fix | `Clients.Client(requester)` → `Clients.Group(stock-watchers)` | — |
| — | `screenshots/capture-log.txt`, `product-before.json`, `product-after.json` | EV-D/EV-F | Timeline and product state for the screenshot run | — |

## E — Order placement refactoring (INV-004)

| ID | File | Tests | Observation | Status |
|---|---|---|---|---|
| EV-E1 | `code/EV-E1_OrdersController_BEFORE_fc3c873.cs` | INV-004 | Business logic inside controller actions | — |
| EV-E2 | `code/EV-E2_OrdersController_AFTER.cs` | TC-40…44 | Thin controller mapping `ServiceResult` | PASS |
| EV-E3 | `code/EV-E4_OrderService_AFTER.cs`, `logs/EV-E3_backend_tests_after_refactor.log` | TC-40…45 | Service owns use case; 42/42 after refactor; 13 contract tests unchanged | PASS |

## F — Low-stock notifications (INV-005)

| ID | File | Tests | Observation | Status |
|---|---|---|---|---|
| EV-F1 | `screenshots/EV-F1_browserB_at_threshold_no_alert.png` | TC-51 | Stock 15 = threshold 15 → no toast in A or B | PASS |
| EV-F2 | `screenshots/EV-F2_browserA_low_stock_toast.png` | TC-52, TC-54 | Stock 14 → toast "Sukuma Wiki 500g · 14 left · reorder at 15" (cosmetic overlap with heading counter noted) | PASS |
| EV-F3 | `screenshots/EV-F3_browserB_low_stock_toast.png` | TC-53 | Same toast in Browser B | PASS |
| EV-F4 | `screenshots/EV-F4_browserB_live_board_alert.png` | TC-54 | Alert also on Live board; no duplicate | PASS |
| EV-F5 | `screenshots/EV-F5_signalr_frames_browserB.json` | TC-53, TC-31, TC-32 | Frames: StockUpdated 16, 15, 14, then `LowStockAlert {41, "Sukuma Wiki 500g", 14, 15}` | PASS |
| EV-F6 | `logs/EV-F6_two_browser_low_stock_check.txt` | TC-51…58 | 7/7 checks PASS incl. no duplicate after navigation | PASS |

## G — Final validation

| ID | File | Tests | Observation | Status |
|---|---|---|---|---|
| EV-G1 | `logs/EV-G1_backend_build.txt` | TC-70 | Build succeeded, 0 warnings, 0 errors | PASS |
| EV-G2 | `logs/EV-G2_frontend_production_build.log` | TC-71 | Production build complete, initial 388.10 kB | PASS |
| EV-G3 / G3a | `logs/EV-G3_backend_tests_FINAL_42_passed.log`, `logs/EV-G3a_backend_test_case_results.txt` | TC-72 | 42/42 passed (named list in G3a) | PASS |
| EV-G4 | `logs/EV-G4_frontend_karma_tests.log` | TC-73, TC-33, TC-58 | 17/17 Karma specs | PASS |
| EV-G5 | `logs/EV-G5_git_history_and_status.txt` | Traceability | 10 commits on `main`, HEAD = origin/main = 4555d20 | PASS |
| EV-G6 | `logs/EV-G6_release_package_checksums.txt` | Fix release details | `package/rapid-inventory-1.1.0-src-4555d20.zip`, CRC32 CE4AA374, SHA-256 18f69d17… | — |
| EV-G7 | `logs/EV-G7_capacity_doubling_measurements.txt` | TC-76 (partial data) | Development-mode load at concurrency 20 vs 40 (hot-product contention) | — |

## Not run / missing evidence

| Item | Status | Reason |
|---|---|---|
| TC-15 client-retry idempotency | NOT RUN | No idempotency key exists (known limitation) |
| TC-16 multi-line deadlock | NOT RUN | Not executed |
| TC-33 live reconnection after an API outage | PARTIAL | Unit specs only |
| TC-36 SignalR across multiple API instances | NOT RUN | No backplane (known limitation) |
| TC-74 human-operated two-browser test | NOT RUN | An automated headless two-context test was performed instead |
| TC-75 staging / production-like deployment | NOT RUN | Only local Docker Compose available |
| TC-76 Release-build load test | NOT RUN | Only Development-mode measurements |
| Screenshot of the zip CRC (template) | Not captured | Values computed and recorded as text (EV-G6) |
| Walkthrough video | Not recorded | See `docs/WALKTHROUGH_SCRIPT.md` |
| Sign-off / approvals | Pending | No names or approvals supplied |
