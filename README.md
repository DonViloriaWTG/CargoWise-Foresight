# CargoWise Foresight — What-If Engine (Simulation & Decision AI)

> **Read-only advisory simulation layer** that predicts outcomes before changes are committed in CargoWise.

## Overview

The What-If Engine provides a sandboxed simulation environment that answers:

> *"If I do X, what is likely to happen to cost, ETA, SLA risk, compliance risk, and downstream workflow impacts?"*

Users supply a **baseline state** + a **proposed change**. The engine runs Monte Carlo simulations and returns:

- **Predicted outcome distributions** (p50, p80, p95 + histograms — not just point estimates)
- **Risk flags** with probabilities and severity levels
- **Explainable narrative** suitable for operators, managers, or customers
- **Recommended mitigations** and alternative options with expected deltas

**Non-goal:** This tool does NOT mutate data, execute changes, or act autonomously. Advisory only.

---

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    REST API (ASP.NET 8)                      │
│   POST /simulations/run  · POST /simulations/explain         │
│   POST /scenarios        · GET /health · GET /metrics        │
├─────────────────────────────────────────────────────────────┤
│                  Web GUI (wwwroot/index.html)                │
│   Scenario builder · Visual results · Cassandra AI Advisor   │
└──────────────┬──────────────────────┬───────────────────────┘
               │                      │
     ┌─────────▼──────────┐  ┌───────▼────────────┐
     │   Simulation Core  │  │ Explanation Service │
     │  (Monte Carlo, N   │  │  (LLM or template   │
     │   runs, seeded)    │  │   fallback)         │
     └─────────┬──────────┘  └───────┬────────────┘
               │                      │
     ┌─────────▼──────────┐  ┌───────▼────────────┐
     │   IDataAdapter     │  │   ILlmClient        │
     │  (MockDataAdapter) │  │  (OllamaLlmClient)  │
     └────────────────────┘  └────────────────────┘
```

### Projects

| Project | Purpose |
|---------|---------|
| `CargoWise.Foresight.Api` | REST API, controllers, middleware, static file serving (GUI), observability |
| `CargoWise.Foresight.Core` | Domain models, simulation kernel, explanation service (Cassandra), interfaces |
| `CargoWise.Foresight.Llm.Ollama` | Ollama HTTP client with retry + circuit breaker + model detection |
| `CargoWise.Foresight.Data.Mock` | Mock data adapter with deterministic sample priors |
| `CargoWise.Foresight.Tests` | Unit tests, contract validation, injection defense tests |

---

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- (Optional) [Ollama](https://ollama.com/) for LLM-powered explanations
- (Optional) [Docker](https://www.docker.com/) for containerized deployment

---

## Quick Start

### 1. Build & Run (without Ollama)

```bash
cd "CargoWise Foresight"
dotnet restore
dotnet build
dotnet run --project src/CargoWise.Foresight.Api
```

The API starts at **http://localhost:5248**.

- **Web GUI:** http://localhost:5248 — interactive scenario builder and results viewer
- **Swagger UI:** http://localhost:5248/swagger — API documentation

Without Ollama running, the engine still works — simulations return full numeric results, and explanations use a template-based fallback (no LLM narrative).

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

## Web GUI

The application includes a built-in web interface at **http://localhost:5248** for non-technical users to run simulations without using the API directly.

### Features

- **Scenario Builder** — Pick from 3 pre-loaded sample scenarios or configure your own (origin, destination, mode, carrier, hazmat, value, change type, simulation parameters)
- **One-Click Simulation** — Click "Run Simulation" to execute what-if analysis
- **Risk Gauge** — Color-coded overall risk score (green/amber/red/critical)
- **Key Metrics** — ETA median, cost median, SLA breach rate, risks flagged
- **Interactive Histograms** — ETA and cost distributions with hover tooltips
- **Risk Cards** — Severity-coded risk flags with probabilities, rationale, and mitigations
- **Recommendations** — Actionable options with expected deltas (cost/time impact)
- **Cassandra AI Advisor** — LLM-generated or template-based narrative explanations with visual charts (risk gauge, percentile bars, risk probability chart)
- **Live Status Indicators** — Header shows API health and Ollama LLM status with model name
- **Raw JSON** — Full simulation response in a code viewer for developers

No build tools, npm, or frameworks required — the GUI is a single self-contained HTML file served from `wwwroot/`.

---

## With Ollama (Local LLM)

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
| `Ollama:Model` | `phi3:mini` | Model to use for explanations |
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

Health check endpoint. Returns API status and Ollama LLM status including:
- Whether Ollama is running
- The configured model name
- Available models on the Ollama instance
- Whether the configured model is ready to use

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

### Explanation Response

When Ollama is available and the configured model is installed, explanations are LLM-generated narratives authored by **Cassandra** — the CargoWise Foresight AI advisor persona — tailored to the audience (operator/manager/customer). When unavailable, a structured template fallback is used. The `generatedByLlm` field indicates which mode was used.

The GUI header displays real-time Ollama status:
- **Green** "Ollama: Ready" — LLM explanations active
- **Amber** "Ollama: Model Missing" — Ollama running but configured model not pulled
- **Red** "Ollama: Offline" — Ollama not running, template fallback in use

The currently selected model is displayed separately in the header as `model: <configuredModel>`.
---

## Safety & Security

- **Read-only:** No mutations to any system. Advisory only.
- **Deterministic:** Same seed → same numeric results, every time.
- **Prompt injection defense:** System prompts include strict rules; user inputs sanitized before LLM.
- **Data redaction:** Internal traces/state are stripped before sending to LLM.
- **Circuit breaker:** Ollama client trips after repeated failures; auto-resets.
- **Model detection:** Health endpoint verifies the configured model is actually available, preventing misleading "online" status.
- **Safe mode:** If LLM is down, numeric simulation still works; explanations fall back to templates.
- **No auto-acting:** LLM cannot call tools, execute code, or mutate anything.

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
│   ├── CargoWise.Foresight.Api/          # REST API + Web GUI
│   │   └── wwwroot/index.html            # Single-page GUI
│   ├── CargoWise.Foresight.Core/         # Domain + simulation
│   ├── CargoWise.Foresight.Llm.Ollama/   # Ollama client
│   └── CargoWise.Foresight.Data.Mock/    # Mock data
├── tests/
│   └── CargoWise.Foresight.Tests/        # All tests
└── samples/
    └── scenarios/                        # Sample JSON scenarios
```

### Adding a New Data Adapter

Implement `IDataAdapter` and register it in DI:

```csharp
builder.Services.AddSingleton<IDataAdapter, MyRealDataAdapter>();
```

### Adding a New LLM Provider

Implement `ILlmClient` and register:

```csharp
builder.Services.AddSingleton<ILlmClient, MyAzureOpenAIClient>();
```

---

## License

Internal use — WiseTech Global. Not for distribution.
