# Walkthrough Video — Recording Guide

**Status:** not yet recorded. This is the guide for recording it.

**Target:** one continuous recording of 12–14 minutes (the allowed range is 10–15). Do not cut or splice.

**Rule:** run every command live and show its real output. The numbers quoted in this guide are what earlier runs
produced; your run may differ slightly. Say what *your* screen shows.

---

## Part 1 — Prepare (off camera, about 20 minutes)

### 1.1 Recording setup

- **Recorder:** OBS Studio (free) recording the whole display, or Windows Snipping Tool → Record (`Win + Shift + R`) on the full screen.
  - Do **not** use the Game Bar (`Win + Alt + R`): it records only one window, and you will switch between the browser, VS Code and the terminal.
- **Resolution:** 1920 × 1080.
- **Readability:**
  - VS Code zoom: `Ctrl + =` twice.
  - Terminal font size: 16 or more.
  - Browser zoom: 110%.
- **Distractions:** turn on Do Not Disturb and close Slack, email and other notifications.
- **Audio:** do a 20-second test recording and check that your voice is clear.

### 1.2 Start the stack

In a Git Bash terminal (for example, the VS Code terminal):

```bash
cd /e/ALEX/recv/stage-3-assessment/stage-3-assessment
docker compose up -d
docker compose ps          # db must show (healthy)
curl -s http://localhost:5080/health
```

**Port 1433 note (this PC only):** your local SQL Express owns port 1433, so host tools need the container on port 14333. If the
container was recreated since the last session, add the extra port again with a file kept **outside** the repo:

```bash
printf 'services:\n  db:\n    ports:\n      - "14333:1433"\n' > /tmp/compose.local.yml
docker compose -f docker-compose.yml -f /tmp/compose.local.yml up -d db
```

### 1.3 Set the test connection string (off camera, so the password is not recorded)

Run this in **every** terminal you will use on camera:

```bash
export ConnectionStrings__DefaultConnection="Server=tcp:127.0.0.1,14333;Database=Inventory;User Id=sa;Password=<from docker-compose.yml>;TrustServerCertificate=True"
```

### 1.4 Pre-build, so nothing compiles for a minute on camera

```bash
dotnet build Inventory.sln
dotnet test Inventory.sln --no-build            # warm-up run; expect 42 passed
git worktree add ../baseline 6028cbd            # original application code + the tests, for the "before" demo
dotnet build ../baseline/Inventory.sln
```

### 1.5 Second API instance (for the multi-instance demo)

In a **separate** terminal, after doing the `export` from 1.3:

```bash
ASPNETCORE_ENVIRONMENT=Production dotnet run --project backend --no-launch-profile --urls http://localhost:5081
```

Leave it running. Check that it answers: `curl -s http://localhost:5081/health`.

### 1.6 Demo product

```bash
node seed/prepare-demo-product.mjs FPR-000061      # Roma Tomatoes 1kg → stock 18, threshold 15
```

### 1.7 Windows to arrange

| Window | Contents |
|---|---|
| Browser A (normal window), left half of the screen | http://localhost:4200/inventory, search `FPR-000061`, press **Run query**, set **Move qty** to 1 |
| Browser B (InPrivate / Incognito window), right half | Same page and search. This is a separate session with its own SignalR connection. |
| VS Code | Open these tabs: `backend/Services/OrderService.cs`, `backend/Controllers/OrdersController.cs`, `backend/Controllers/ProductsController.cs`, `backend/Services/IStockNotifier.cs`, `frontend/src/app/low-stock-alerts.ts`, `ROOT_CAUSE_ANALYSIS.md`, `SOLUTIONS.md` |
| Terminal 1 | Repo root, `export` from 1.3 done |
| Terminal 2 | Second API instance (from 1.5) |
| Terminal 3 | The `../baseline` folder, `export` done |

Do one full practice run without recording. Then reset the demo product with `node seed/prepare-demo-product.mjs FPR-000061`.

---

## Part 2 — Recording script

The **Say** lines are suggestions; put them in your own words. **Do / Show** lists the exact actions.

### 0:00–1:00 — Introduction

**Do / Show**
- `README.md` in VS Code.
- In the terminal: `git log --oneline`

**Say**

> "This is the Rapid Inventory system: an ASP.NET Core 9 API with EF Core and SQL Server 2022, an Angular 20 front end, and SignalR for live updates, all running in Docker Compose. Users reported three problems: overselling under concurrent orders, a slow inventory page, and a live stock board that doesn't update for other users. I was also asked to refactor order placement and add low-stock alerts.
>
> The project came without Git history, so my first commit is a snapshot of the untouched code. I wrote reproduction tests next, and then made one commit per fix."

### 1:00–3:30 — Concurrent orders (INV-001)

**1. The original code**

**Do / Show:** `git show fc3c873:backend/Controllers/OrdersController.cs`, scrolled to `Place`.

**Say**

> "The original code reads the stock, checks it in memory, then writes an absolute value with `SET Stock = @newStock`, outside any transaction. Two requests can both read 1 and both write 0. Stock never goes negative — the overselling shows up as more units in order lines than were ever deducted."

**2. Reproduce it on the original code**

**Do / Show:** in Terminal 3 (`../baseline`):

```bash
dotnet test tests/Inventory.Api.Tests --no-build --filter "FullyQualifiedName~Two_instances|FullyQualifiedName~Many_concurrent" --logger "console;verbosity=detailed" | grep -E "Last-unit|requests:|Passed |Failed "
```

**Expect:** `Last-unit race results: OK, OK`, plus a line showing 40 orders accepted for a stock of 10.

**Say**

> "This test runs two independent API hosts against the same database. It holds a lock on the product row until SQL Server reports that both requests are blocked, then releases it — so both compete at the same instant, on every run. On the original code both instances sold the last unit."

**3. The fix**

**Do / Show:** `OrderService.cs`, `PlaceOrderAsync`.

**Say**

> "The fix is one conditional statement: `UPDATE … SET Stock = Stock - @qty WHERE Id = @id AND Stock >= @qty`, inside a transaction that also inserts the order. Zero affected rows means 409, and the whole transaction rolls back. There's also a `CHECK (Stock >= 0)` constraint as a final guard."

**4. Same tests on the fixed code**

**Do / Show:** in Terminal 1:

```bash
dotnet test tests/Inventory.Api.Tests --no-build --filter "FullyQualifiedName~OrderConcurrencyTests" --logger "console;verbosity=detailed" | grep -E "Last-unit|requests:|Passed |Failed |Total"
```

**Expect:** `OK, Conflict`; then `10 OK, 30 Conflict … final stock 0`; 12 passed.

**5. Live: two real API processes**

**Do / Show**

```bash
node seed/prepare-demo-product.mjs FPR-000062 80
node seed/load-test.mjs orders --urls http://localhost:5080,http://localhost:5081 --product 62 --requests 300 --concurrency 60
```

**Expect:** some 200s via each of 5080 and 5081 that add up to 80, `after=0`, `consistent=true`.

### 3:30–5:30 — Inventory list performance (INV-002)

**1. The original query**

**Do / Show:** `git show 2f076ee -- backend/Controllers/ProductsController.cs`

**Say**

> "The page asks for 200 rows. The original code loaded full entities, then ran one Categories query per row."

**2. SQL before the fix**

**Do / Show:** open `release/Release_Evidence/logs/EV-C1_inventory_list_SQL_BEFORE_202_commands.log` and scroll.

**Say**

> "This is the SQL EF Core actually logged before the fix: 202 commands for one page."

**3. SQL after the fix, captured live**

**Do / Show**

```bash
curl -s -o /dev/null "http://localhost:5080/api/products?pageSize=200"
docker compose logs api --since 20s --no-log-prefix | grep -A9 "Executed DbCommand"
```

**Say**

> "Now there are two commands: a count, and one query that joins Categories and selects only the columns the page needs."

**4. Automated proof**

**Do / Show**

```bash
dotnet test tests/Inventory.Api.Tests --no-build --filter "FullyQualifiedName~ProductListTests" --logger "console;verbosity=detailed" | grep -E "SQL commands|Passed |Failed "
```

**5. Measurements and the index decision**

**Do / Show:** Section E of `ROOT_CAUSE_ANALYSIS.md` (the measurement table and the execution plan).

**Say**

> "On the same seeded data, the median went from about 225 to about 12 milliseconds, with an identical payload. I didn't add an index: the plan reads the primary key in order and seeks into Categories, and search is a `LIKE '%term%'` that no index can seek. The 250,000 orders were never part of this query."

### 5:30–7:30 — SignalR live updates (INV-003)

**1. Same stock in both browsers**

**Do / Show:** both browser windows side by side, showing the same stock (18).

**2. Order in A; B updates by itself**

**Do / Show:** in Browser A, click **Order**. A shows 17, and **B changes to 17 on its own**.

**Say**

> "I didn't touch Browser B. That update arrived over SignalR."

**3. A failed order changes nothing**

**Do / Show:** in A, set the quantity to `100000` and click **Order**. A shows an error; B doesn't change. Then set the quantity back to `1`.

**4. Root cause and fix**

**Do / Show:** `git show d8329cc -- backend/Services/IStockNotifier.cs`

**Say**

> "The hub, the URLs and the event names were all correct. The bug was targeting: when a connection id was supplied, the update went only to the requester — and the inventory page always supplies one. Now it always goes to the stock-watchers group. Updates are sent only after the transaction commits, so a failed order publishes nothing.
>
> One limitation: with more than one API instance, SignalR needs a backplane such as Redis. Without one, users only hear about changes made on the instance they're connected to."

### 7:30–9:00 — Order placement refactoring (INV-004)

**1. Before and after**

**Do / Show:** `git show fc3c873:backend/Controllers/OrdersController.cs` next to the current `OrdersController.cs`. Then `OrderService.cs`.

**Say**

> "The controller held validation, stock logic, persistence, transactions and SignalR. An `OrderService` already existed but nothing used it, and it had drifted out of date. Now the service owns place, cancel and restock, and the controller only maps the results to HTTP. I didn't add any new layers or interfaces."

**2. The contract tests**

**Do / Show**

```bash
dotnet test tests/Inventory.Api.Tests --no-build --filter "FullyQualifiedName~OrderContractTests" | tail -3
```

**Say**

> "These 13 contract tests — status codes, error messages and response bodies — passed on the original controller before I changed anything, and they still pass unchanged. The refactor also fixed a race I found: on the old code, 6 of 10 concurrent cancels of the same order each returned the stock."

### 9:00–10:30 — Low-stock alerts (INV-005)

**1. The threshold condition**

**Do / Show:** `IsLowStockTransition` in `OrderService.cs`.

**Say**

> "An alert fires only when an order moves stock from at-or-above the threshold to strictly below it."

**2. Live demo** (stock is 17 at this point, threshold 15)

**Do / Show**
- Click **Order** in A: stock 16, no alert.
- Click **Order** again: stock 15, exactly the threshold. Point out that no alert appears.
- Click **Order** once more: stock 14. **The toast appears in both browsers.**
- Open the **Live board** in B: the alert is listed there too.

**3. The client code**

**Do / Show:** `frontend/src/app/low-stock-alerts.ts`, and the constructor of `stock-realtime.service.ts`.

**Say**

> "The listener is registered once, in a root singleton service, so recreating a component can't duplicate it. Failed orders publish nothing. Delivery is at-most-once — if publishing fails after the commit, the alert is logged but not retried."

### 10:30–12:00 — Tests, builds and Git

**Do / Show**

```bash
dotnet test Inventory.sln --no-build | tail -2              # 42 passed
cd frontend && npx ng test --watch=false --browsers=ChromeHeadless | tail -1 && cd ..   # 17 SUCCESS
git log --oneline
```

Then open `SOLUTIONS.md` and the release report `release/Rapid_Tech_Insights_Release_Note_and_Test_Evidence.pdf` (cover, test summary table, sign-off page).

**Tip:** if the full backend suite is slow, start it in a spare terminal at about 9:00 and show the finished result here.

### 12:00–14:00 — The four questions

**Q1 — "Two orders for the last unit arrive in the same millisecond on two API instances. Why can't your fix oversell?"**

**Show:** `PlaceOrderAsync` in `OrderService.cs`.

> "Both instances send the same conditional UPDATE. SQL Server has to take an update lock on the row before it evaluates `Stock >= 1`, and only one transaction can hold that lock. The winner sets stock to 0 and keeps an exclusive lock until it commits. The other waits, then re-evaluates the condition against the committed row — 0 is not greater than or equal to 1 — so it updates zero rows. My code turns that into a 409 and rolls back. Nothing depends on memory inside one process, so it holds for any number of instances. The test forces exactly this timing and shows OK plus Conflict."

**Q2 — "Show me the SQL before and after."**

**Show:** Section E of the RCA (both SQL blocks).

> "Before: a count, an entity query including RowVersion, and 200 per-row Categories queries. After: a count and a single join that selects only the DTO columns, paging stays in SQL."

**Q3 — "Show a prompt you gave an AI tool. What did it suggest that you rejected or corrected, and how did you catch it?"**

**Show:** Section I of the RCA.

> "I used Claude Code with the full assessment brief as the prompt. The corrections came from evidence:
> - It first framed overselling as negative stock; the data showed lost updates instead.
> - Its race-test helper missed queued lock waiters; the test timed out, and we fixed the helper.
> - It assumed `localhost,1433` reached Docker; login failures showed my local SQL Express was answering.
> - It assumed hot reload would apply the migration; the migration history table proved it hadn't.
>
> Every claim was checked against SQL Server tests, captured SQL and real browsers."

(Only say what is true for you. If you also used other AI tools, mention them.)

**Q4 — "If traffic doubled, what breaks first?"**

**Show:** Section J of the RCA.

> "Measured: doubling concurrent orders on one popular product didn't increase throughput — about 135 to 121 per second — and median latency went from about 140 to 330 milliseconds. Every order for that product waits for the same row lock. That's the cost of correctness, and it shows up first on hot products.
>
> Unconfirmed: the API's CPU was high, but that was in Development mode with SQL logging; I'd re-test a Release build. SignalR fan-out grows with the number of connected boards, and multiple instances need a backplane.
>
> Next I'd measure a Release build, orders spread across many products, SQL wait statistics, and hundreds of SignalR clients."

**Close (about 15 seconds)**

> "Everything is in the repository: the root cause analysis, the solutions map, and a release report with the raw evidence. Remaining risks are listed in section K. Thanks."

---

## Part 3 — After recording

- [ ] The length is between 10 and 15 minutes, and it is one continuous file.
- [ ] No password or connection string is visible at any point. If one is, re-record; don't blur.
- [ ] Every number you said matches what was on screen.
- [ ] Save it, for example as `walkthrough.mp4`, and share it as the brief requires.
- [ ] Update `ROOT_CAUSE_ANALYSIS.md` (K.9) and the release report (Known Issue 9) to say the video is recorded, and where it is.
- [ ] Clean up:
  - `git worktree remove ../baseline`
  - Stop the :5081 API instance (`Ctrl + C`).
