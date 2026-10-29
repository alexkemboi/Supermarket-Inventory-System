# Rapid Inventory — Stage 3 Practical

A small inventory and order-management system used for the take-home practical assessment. Task instructions and deliverables are in the brief sent to you with this project.

## Requirements

- Docker Desktop with at least 6 GB of memory available
- Ports `1433`, `4200`, and `5080` free

## Start

```bash
docker compose up --build
```

The first start pulls the SQL Server image and seeds 2,000 products plus 250,000 historical orders, so it takes longer than later starts.

- Web app: http://localhost:4200
- API: http://localhost:5080
- Health check: http://localhost:5080/health

## Reset

Reset the database and rebuild all services:

```bash
./seed/reset.sh
```

## Project Map

```text
backend/   ASP.NET Core API, EF Core, SignalR
frontend/  Angular web app
seed/      reset and load-generation scripts
```

The main user flows are:

- Search and inspect product stock
- Place a single-product order
- Restock a product
- Watch live stock changes and low-stock alerts in another browser
- Query recent orders through `GET /api/orders/recent`

## Useful Commands

```bash
# Build the API and its projects
dotnet build Inventory.sln

# Build the Angular app
npm --prefix frontend run build

# Send concurrent orders (defaults to 40 requests for product 1)
./seed/stress-orders.sh
```

Change load-generator inputs with environment variables:

```bash
PRODUCT_ID=10 REQUESTS=20 QUANTITY=2 ./seed/stress-orders.sh
```

## Tests

```bash
# Backend integration tests (need the compose SQL Server; each test class creates and drops its own database).
# The connection string defaults to backend/appsettings.json and can be overridden:
#   export ConnectionStrings__DefaultConnection="Server=tcp:127.0.0.1,1433;...;TrustServerCertificate=True"
dotnet test Inventory.sln

# Angular unit tests
npm --prefix frontend test -- --watch=false --browsers=ChromeHeadless

# Live checks against the running stack
node seed/two-browser-check.mjs FPR-000021      # two headless Chrome users, stock sync + low-stock toasts
node seed/load-test.mjs orders --urls http://localhost:5080 --product 1 --requests 40 --concurrency 20
```

See `ROOT_CAUSE_ANALYSIS.md` and `SOLUTIONS.md` for the investigated issues and their verification.

## Troubleshooting

- If startup hangs, run `docker compose ps` and wait for `db` to report healthy.
- If a port is occupied, stop the process using it before restarting Compose. On Windows, a local SQL Server
  (e.g. `MSSQL$SQLEXPRESS`) on 1433 silently captures `localhost,1433` connections from the host.
- To discard all database state, use `docker compose down -v` and start again.
