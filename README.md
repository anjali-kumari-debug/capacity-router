# Capacity Router

A small **C# / .NET 8** backend service that routes workloads to compute capacities across regions. It's a tiny version of the kind of platform service that powers large cloud data platforms, built to practise C#, Azure, Azure DevOps, KQL and infrastructure-as-code.

## What it does

- **Capacities** (pools of compute units in a region) register with the router and send a **heartbeat** every few seconds.
- **Workloads** ask the router for some units. The router picks a capacity and **reserves** the units.
- A **background health monitor** marks capacities unhealthy when they stop sending heartbeats, so no new work goes to them. When the heartbeat returns, they recover automatically.
- **Telemetry** (requests, logs, custom metrics) goes to **Application Insights**, where you query it with **KQL**.

### Routing rules (`RoutingPolicy.cs`)

1. Only healthy capacities with enough free units are eligible.
2. Stay in the preferred region if it has room; otherwise fall back to any region (availability over locality).
3. Pick the least-loaded capacity (most free units); break ties by id so results are deterministic.

### Why it is safe under load

All state changes happen under one lock in `CapacityRegistry`, so two requests arriving at the same moment can never reserve the same units. A test fires 200 concurrent requests at a 50-unit capacity and checks that exactly 50 succeed.

## Project layout

```
src/CapacityRouter.Api/      ASP.NET Core minimal API
  Program.cs                 endpoints, DI, telemetry setup
  RoutingPolicy.cs           pure routing decision (easy to unit test)
  CapacityRegistry.cs        thread-safe state: capacities + allocations
  HeartbeatMonitor.cs        background service that detects dead capacities
  Telemetry.cs               custom metrics + heartbeat settings
tests/CapacityRouter.Tests/  xUnit unit tests + in-memory API tests
infra/main.bicep             Azure infrastructure as code
azure-pipelines.yml          Azure DevOps CI/CD: build, test, deploy, health check
kql/queries.kql              KQL queries for monitoring and incident investigation
Dockerfile                   container image (non-root)
```

## API

| Method | Path | What it does |
|---|---|---|
| POST | `/capacities` | Register or resize a capacity `{ "id", "region", "totalUnits" }` |
| GET | `/capacities` | List capacities with used/free units and health |
| POST | `/capacities/{id}/heartbeat` | Capacity says "I'm alive" |
| POST | `/route` | Route a workload `{ "workloadId", "units", "preferredRegion" }` → 200, 400 or 503 |
| DELETE | `/allocations/{id}` | Release reserved units |
| GET | `/health` | Health check |

## Run it locally

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

### Everything in one flow

```bash
./run.sh            # build, test, start the API, run the demo, stop
./run.sh --docker   # same, then build the Docker image too
```

The script registers two capacities and routes workloads, including one that falls back to another region. It also sends a bad request (400), one that asks for too many units (503), and releases an allocation. Then it stops one capacity's heartbeats, shows it being marked unhealthy and its work moving to the other region, and brings it back. The demo shortens the heartbeat timeout to 6 seconds so this takes about 10 seconds.

### Step by step

```bash
dotnet test tests/CapacityRouter.Tests      # build and run all tests
dotnet run --project src/CapacityRouter.Api # starts on http://localhost:5xxx (shown in the console)
```

Try it (replace the port):

```bash
curl -X POST localhost:5000/capacities -H "Content-Type: application/json" \
  -d '{"id":"cap-india-1","region":"centralindia","totalUnits":10}'
curl -X POST localhost:5000/capacities -H "Content-Type: application/json" \
  -d '{"id":"cap-eu-1","region":"westeurope","totalUnits":20}'
curl -X POST localhost:5000/route -H "Content-Type: application/json" \
  -d '{"workloadId":"job-1","units":4,"preferredRegion":"centralindia"}'
curl localhost:5000/capacities
```

Wait 40 seconds without sending heartbeats and call `GET /capacities` again: both capacities are now unhealthy and `/route` returns 503. Send `POST /capacities/cap-india-1/heartbeat` and it recovers.

With Docker:

```bash
docker build -t capacity-router .
docker run -p 8080:8080 capacity-router
```

## Deploy to Render (free)

[![Deploy to Render](https://render.com/images/deploy-to-render-button.svg)](https://render.com/deploy?repo=https://github.com/anjali-kumari-debug/capacity-router)

`render.yaml` defines a free Docker web service with `/health` as its health check. Pushes to `main` redeploy it automatically. Free services sleep after 15 minutes without traffic, and state is kept in memory, so registered capacities are lost when the service restarts.

## Deploy to Azure

1. **Create the infrastructure** (needs the [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) and an Azure account; *Azure for Students* gives free credit):
   ```bash
   az login
   az group create -n rg-capacity-router -l centralindia
   az deployment group create -g rg-capacity-router -f infra/main.bicep -p appName=capacityrouter-<yourname>
   ```
2. **Set up Azure DevOps** (free at dev.azure.com): create a project, import this repo, then go to *Project settings > Service connections* and create an *Azure Resource Manager* connection named `capacity-router-sc`.
3. In `azure-pipelines.yml`, set `webAppName` to the same name you used above.
4. Create a pipeline from `azure-pipelines.yml` and run it. New organizations may need to request the free hosted-agent grant first; Azure DevOps shows a link if so.
5. Open Application Insights > **Logs** and run the queries in `kql/queries.kql`.

When you're done, delete everything with `az group delete -n rg-capacity-router`.

## Ideas to extend it

- Persist state in Azure Cosmos DB or Azure SQL so it survives restarts.
- Run several router instances and handle the races between them.
- Add rate limiting per workload, or allocation expiry (leases) so crashed workloads don't hold units forever.
