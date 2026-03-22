# CargoWise Foresight — What-If Engine (Simulation & Decision AI)

> **Read-only advisory simulation layer** that predicts outcomes before changes are committed in CargoWise.

## Overview

The What-If Engine provides a sandboxed simulation environment that answers:

> *"If I do X, what is likely to happen to cost, ETA, SLA risk, compliance risk, and downstream workflow impacts?"*

Users supply a **baseline state** + a **proposed change**. The engine runs Monte Carlo simulations and returns:

- **Predicted outcome distributions** (p50, p80, p95 + histograms — not just point estimates)
- **Risk flags** with probabilities and severity levels
- **Explainable narrative** from **Cassandra**, the AI advisor persona — tailored for operators, managers, or customers
- **Recommended mitigations** and alternative options with expected deltas

The simulation kernel is pure Monte Carlo math (deterministic, seeded). Only the narrative explanation uses an LLM (Ollama). If Ollama is unavailable, Cassandra falls back to a structured template — the engine always works.

**Non-goal:** This tool does NOT mutate data, execute changes, or act autonomously. Advisory only.

---

## Web GUI

A self-contained web GUI is served at the API root (`http://localhost:5248`). It provides:

- **Scenario builder** — select origin, destination, carrier, mode, and change type
- **Visual results** — distribution charts, risk gauges, and tabbed detail views
- **Cassandra narrative** — AI-generated explanation with visual confidence graphs
- **Status indicators** — real-time health for API, data source (Mock/ODYSSEY), and Ollama

No build tools required — vanilla HTML/CSS/JS served via ASP.NET static files.

---

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    REST API (ASP.NET 8)                      │
│   POST /simulations/run  · POST /simulations/explain         │
│   POST /scenarios        · GET /health · GET /metrics        │
│                                                              │
│   Web GUI (wwwroot/index.html) — served at /                 │
└──────────────┬──────────────────────┬───────────────────────┘
               │                      │
     ┌─────────▼──────────┐  ┌───────▼────────────┐
     │   Simulation Core  │  │ Explanation Service │
     │  (Monte Carlo, N   │  │  "Cassandra" AI     │
     │   runs, seeded)    │  │  advisor persona    │
     └─────────┬──────────┘  └───────┬────────────┘
               │                      │
     ┌─────────▼──────────┐  ┌───────▼────────────┐
     │   IDataAdapter     │  │   ILlmClient        │
     │  MockDataAdapter   │  │  OllamaLlmClient    │
     │  OdysseyDataAdapter│  │  (phi3:mini)         │
     └────────────────────┘  └────────────────────┘
               │
     ┌─────────▼──────────┐
     │   ODYSSEY Database  │
     │  (CargoWise SQL)    │
     └────────────────────┘
```

### Projects

| Project | Purpose |
|---------|---------|
| `CargoWise.Foresight.Api` | REST API, controllers, middleware, web GUI, observability |
| `CargoWise.Foresight.Core` | Domain models, simulation kernel, Cassandra explanation service, interfaces |
| `CargoWise.Foresight.Llm.Ollama` | Ollama HTTP client with retry + circuit breaker |
| `CargoWise.Foresight.Data.Mock` | Mock data adapter with deterministic sample priors |
| `CargoWise.Foresight.Data.Odyssey` | ODYSSEY (CargoWise) database adapter — real reference data + historical shipment queries |
| `CargoWise.Foresight.Tests` | Unit tests, contract validation, injection defense tests |

---

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- (Optional) [Ollama](https://ollama.com/) for LLM-powered Cassandra narratives
- (Optional) [Docker](https://www.docker.com/) for containerized deployment
- (Optional) SQL Server with a CargoWise **ODYSSEY** database for real data

---

## Quick Start

### 1. Build & Run

```bash
cd "CargoWise Foresight"
dotnet restore
dotnet build
dotnet run --project src/CargoWise.Foresight.Api
```

The API starts at **http://localhost:5248**.

| URL | Description |
|-----|-------------|
| http://localhost:5248 | Web GUI |
| http://localhost:5248/swagger | Swagger API docs |
| http://localhost:5248/health | Health & status |

Without Ollama, simulations still return full numeric results — Cassandra uses a template-based narrative fallback.

### 2. Run Tests

```bash
dotnet test
```

### 3. Run a Sample Simulation (curl)

```bash
curl -X POST http://localhost:5248/simulations/run \
  -H "Content-Type: application/json" \
  -d @samples/scenarios/carrier-swap-scenario.json
```

On Windows PowerShell:
```powershell
$body = Get-Content .\samples\scenarios\carrier-swap-scenario.json -Raw
Invoke-RestMethod -Uri http://localhost:5248/simulations/run -Method Post -ContentType "application/json" -Body $body | ConvertTo-Json -Depth 10
```

---

## Data Source Configuration

The engine supports two data sources, controlled by the `DataSource` setting in `appsettings.json`:

### Mock (default for development)

```json
{ "DataSource": "Mock" }
```

Uses hardcoded deterministic priors — no database required. Good for development, demos, and testing.

### ODYSSEY (CargoWise database)

```json
{
  "DataSource": "Odyssey",
  "Odyssey": {
    "ConnectionString": "Server=YOUR_SERVER;Database=ODYSSEY;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=10"
  }
}
```

Connects to a live CargoWise ODYSSEY database and loads:

**Reference Data (always available):**
- **Ports** — UN location codes from `RefUNLOCO` (100K+ locations)
- **Carriers** — Shipping providers from `OrgHeader` (where `OH_IsShippingProvider = 1`)
- **Countries** — From `RefCountry` (with `RN_IsSanctioned` flag for risk analysis)
- **Geographic transit estimation** — Haversine distance-based transit time calculation

**Historical Shipment Data (when transactional records exist):**
- **Carrier delay stats** — Mean delay, stddev, reliability score per carrier/mode from `JobActualTransportRouting`
- **Route transit stats** — Mean transit time, congestion probability per port-pair from `JobActualTransportRouting`
- **Customs hold rates** — Hold probability and mean hold duration per country from `CusEntryHeader` + `JobDeclaration`
- **Cost per shipment** — Average cost by transport mode from `JobCharge` + `JobHeader`

All historical queries require a minimum sample size of 10 records. When no shipment history exists (e.g., fresh ODYSSEY install), the adapter automatically falls back to geographic estimates — the engine always works.

The `/health` endpoint reports the data source status, including whether historical data is available:

```json
{
  "dataSource": "Odyssey",
  "odyssey": {
    "connected": true,
    "ports": 106417,
    "carriers": 137,
    "countries": 250,
    "hasShipmentHistory": false,
    "historicalStats": {
      "carrierRoutes": 0,
      "portPairs": 0,
      "customsCountries": 0
    }
  }
}
```

---

## With Ollama (Local LLM — Cassandra AI Advisor)

Ollama powers **Cassandra**, the AI advisor persona that generates narrative explanations of simulation results. Cassandra adapts tone for different audiences (operator, manager, customer) and includes visual confidence graphs.

### Option A: Run Ollama Directly

```bash
# Install Ollama (see https://ollama.com/download)
ollama pull phi3:mini

# Verify it's running:
curl http://localhost:11434/api/tags

# Start the API (defaults point to localhost:11434)
dotnet run --project src/CargoWise.Foresight.Api
```

### Option B: Docker Compose (API + Ollama)

```bash
docker compose up -d

# Pull a model inside the Ollama container:
docker compose exec ollama ollama pull phi3:mini
```

### Configuration

Set via `appsettings.json` or environment variables:

| Setting | Default | Description |
|---------|---------|-------------|
| `Ollama:BaseUrl` | `http://localhost:11434` | Ollama API endpoint |
| `Ollama:Model` | `phi3:mini` | Model to use for Cassandra narratives |
| `Ollama:TimeoutSeconds` | `120` | HTTP timeout |
| `Ollama:MaxRetries` | `2` | Retry attempts |
| `Ollama:CircuitBreakerThreshold` | `3` | Failures before circuit opens |

---

## API Reference

### `POST /simulations/run`

Runs a what-if simulation. Returns distributions, risks, and recommendations.

**Request body:** See [`SimulationRequest`](#data-contracts) below.

### `POST /simulations/explain`

Generates a natural-language explanation for a simulation result.

**Request body:**
```json
{
  "requestId": "explain-001",
  "simulationResult": { ... },
  "audience": "operator",
  "tone": "professional"
}
```

### `POST /scenarios`

Save a scenario for repeatability.

### `GET /scenarios/{id}`

Load a saved scenario.

### `GET /health`

Health check endpoint.

### `GET /metrics`

Prometheus-style metrics snapshot.

---

## Data Contracts

### SimulationRequest

```json
{
  "requestId": "sim-001",
  "seed": 42,
  "baseline": {
    "shipment": {
      "id": "SHP-001",
      "origin": "CNSHA",
      "destination": "USLAX",
      "mode": "Ocean",
      "carrier": "MSC",
      "hazmat": false,
      "value": 100000
    },
    "workflow": {
      "slaTargets": [{ "name": "Delivery", "targetDays": 18 }]
    },
    "finance": {
      "rateLineItems": [{ "description": "Ocean Freight", "amount": 2800 }],
      "currency": "USD"
    },
    "compliance": {
      "commodities": ["electronics"],
      "countriesInvolved": ["CN", "US"]
    }
  },
  "changeSet": {
    "changeType": "CarrierSwap",
    "parameters": { "newCarrier": "COSCO" }
  },
  "simulationRuns": 500
}
```

### SimulationResult

```json
{
  "requestId": "sim-001",
  "summary": {
    "outcome": "Moderate risk. Review flagged items before proceeding.",
    "overallRiskScore": 0.35,
    "simulationRuns": 500,
    "seed": 42,
    "durationMs": 45.2
  },
  "distributions": {
    "etaDays": {
      "p50": 17.2, "p80": 19.8, "p95": 23.5,
      "mean": 17.6, "stdDev": 3.1,
      "histogram": [...]
    },
    "costUsd": {
      "p50": 3635, "p80": 4200, "p95": 5100,
      "mean": 3750, "stdDev": 620,
      "histogram": [...]
    }
  },
  "risks": [
    {
      "type": "SLA_BREACH",
      "probability": 0.23,
      "severity": "Medium",
      "rationaleFacts": "SLA breach probability: 23.0%. Based on 500 simulation runs.",
      "mitigations": ["Consider expedited shipping option", "..."]
    }
  ],
  "recommendations": [
    {
      "option": "ExpressMode",
      "description": "Switch to express/air freight to reduce transit time.",
      "expectedDeltas": { "etaDays": -5.0, "costUsd": 1454 },
      "confidence": 0.75
    }
  ]
}
```

### Supported Change Types

| Change Type | Parameters | Description |
|-------------|-----------|-------------|
| `CarrierSwap` | `newCarrier` | Switch to a different carrier |
| `RouteChange` | `newOrigin`, `newDestination` | Change route origin/destination |
| `DepartureShift` | `shiftDays` | Delay or advance departure |
| `CustomsFilingChange` | — | Customs filing strategy change |
| `PaymentTermChange` | — | Payment terms modification |

---

## Interpreting Results

### Risk Score (0.0 – 1.0)

| Range | Meaning |
|-------|---------|
| 0.0 – 0.2 | Low risk — change appears safe |
| 0.2 – 0.5 | Moderate — review flagged items |
| 0.5 – 0.8 | High — significant adverse probability |
| 0.8 – 1.0 | Critical — strongly reconsider |

### Distributions

- **P50:** Median outcome (50% of simulations below this)
- **P80:** 80th percentile (reasonably pessimistic)
- **P95:** 95th percentile (worst-case planning)
- **Histogram:** Bucketed frequency distribution for visualization

### Explanation Response (Cassandra)

When Ollama is available, **Cassandra** generates LLM-powered narratives tailored to the audience (operator/manager/customer) with visual confidence graphs. When Ollama is unavailable, Cassandra uses a structured template fallback. The `generatedByLlm` field indicates which mode was used.

Both modes are fully data-driven — Cassandra only explains the actual Monte Carlo simulation results (P50/P80/P95 distributions, risks, recommendations). The LLM cannot hallucinate numbers; it receives the real `SimulationResult` as structured data.

---

## Safety & Security

- **Read-only:** No mutations to any system. Advisory only.
- **Deterministic:** Same seed → same numeric results, every time.
- **Prompt injection defense:** System prompts include strict rules; user inputs sanitized before LLM.
- **Data redaction:** Internal traces/state are stripped before sending to LLM.
- **Circuit breaker:** Ollama client trips after repeated failures; auto-resets.
- **Safe mode:** If LLM is down, numeric simulation still works; Cassandra falls back to templates.
- **No auto-acting:** LLM cannot call tools, execute code, or mutate anything.
- **Database read-only:** ODYSSEY adapter only executes SELECT queries — never writes.

---

## Observability

- **Structured logging:** Every request logs requestId, duration, seed, success/failure.
- **Metrics endpoint:** `GET /metrics` returns simulation counts, success rates, average durations.
- **Extensible:** Clean abstractions for future LangFuse/OpenTelemetry integration.

---

## Sample Scenarios

Three sample scenarios are included in `samples/scenarios/`:

1. **carrier-swap-scenario.json** — Swap MSC→COSCO on Shanghai→LA ocean shipment
2. **departure-shift-hazmat.json** — 3-day departure delay on hazmat shipment Hamburg→NYC
3. **route-change-diversion.json** — Divert Shanghai→Sydney shipment to Rotterdam

---

## Development

### Project Structure

```
├── CargoWiseForesight.sln
├── Dockerfile
├── docker-compose.yml
├── src/
│   ├── CargoWise.Foresight.Api/          # REST API + web GUI
│   │   └── wwwroot/index.html            # Self-contained web GUI
│   ├── CargoWise.Foresight.Core/         # Domain + simulation + Cassandra
│   ├── CargoWise.Foresight.Llm.Ollama/   # Ollama client
│   ├── CargoWise.Foresight.Data.Mock/    # Mock data (no DB needed)
│   └── CargoWise.Foresight.Data.Odyssey/ # ODYSSEY database adapter
├── tests/
│   └── CargoWise.Foresight.Tests/        # All tests
└── samples/
    └── scenarios/                   # Sample JSON scenarios
```

### Adding a New Data Adapter

Implement `IDataAdapter` and register it in DI. The engine uses conditional registration based on the `DataSource` config:

```csharp
// In Program.cs
if (dataSource.Equals("MySource", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IDataAdapter, MyDataAdapter>();
}
```

The adapter must implement four methods returning prior distributions:
- `GetCarrierPriorAsync` — carrier reliability and delay stats
- `GetRoutePriorAsync` — transit time and congestion data per port-pair
- `GetCustomsPriorAsync` — customs hold probability per country
- `GetDemurragePriorAsync` — free time and daily demurrage rates

### Adding a New LLM Provider

Implement `ILlmClient` and register:

```csharp
builder.Services.AddSingleton<ILlmClient, MyAzureOpenAIClient>();
```

---

## License

Internal use — WiseTech Global. Not for distribution.
