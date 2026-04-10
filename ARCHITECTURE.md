# Architecture

## Overview

```
                ┌─────────────────────────────┐
                │       RugPullServer          │
                │                             │
Claude ────────►│  --mcp: stdio JSON-RPC      │──► ML Model (rugpull_model.zip)
                │                             │
Bots/Apps ─────►│  default: HTTP REST API     │──► DexScreener API (live data)
                │                             │
                └─────────────────────────────┘
```

One server binary, two interfaces. The `--mcp` flag switches between them. Both share the same ML model and analyzer code.

## ML pipeline

**Algorithm:** FastTree (gradient-boosted decision trees) via ML.NET

**Training data:** SolRPDS dataset — 116K Solana liquidity pools labeled as Active or Inactive (rug pulled).

**Features (5):**
1. TotalAddedLiquidity
2. TotalRemovedLiquidity
3. NumLiquidityAdds
4. NumLiquidityRemoves
5. AddToRemoveRatio

**Pipeline steps:**
1. Custom mapping: INACTIVITY_STATUS string -> boolean Label
2. Feature concatenation into single vector
3. MinMax normalization
4. FastTree classifier (300 trees, 50 leaves, 0.1 learning rate)

The custom mapping is implemented as a `CustomMappingFactory` in RugPullShared so both training and inference projects can deserialize the model correctly. This is an ML.NET requirement — the mapping must be in a shared assembly referenced by both.

## Heuristic analyzer

Separate from the ML model. Calls DexScreener's public API with a token mint address, extracts the highest-liquidity Solana pool, and scores risk on five signals:

- Liquidity depth (how much USD is in the pool)
- Buy/sell ratio (24h transaction counts)
- Price movement (24h percentage change)
- Token age (pool creation timestamp)
- Volume/liquidity ratio (wash trading detection)

Produces a 0-10 risk score with individual flags explaining each signal.

## Server modes

### HTTP mode (default)

Standard ASP.NET minimal API. Three endpoints:

| Method | Path | Purpose |
|--------|------|---------|
| GET | /health | Liveness check |
| POST | /api/check | ML prediction from raw liquidity numbers |
| GET | /api/analyze/{mint} | Live token lookup + heuristic scoring |

PredictionEngine is not thread-safe, so HTTP mode wraps it in a lock. For higher throughput, this could be swapped to `PredictionEnginePool`.

### MCP mode (--mcp flag)

JSON-RPC 2.0 over stdin/stdout. Implements the MCP handshake (initialize, tools/list) and exposes two tools:

- `check_rug_pull` — same as POST /api/check
- `analyze_token` — same as GET /api/analyze/{mint}

Designed for Claude Code and Claude Desktop. The AI agent discovers available tools at connection time and decides when to invoke them based on conversation context.

## Project dependencies

```
RugPullShared (classlib)
  └── Microsoft.ML

RugPullTrainer (console) ──references──► RugPullShared
  └── Microsoft.ML.FastTree

RugPullServer (web) ──references──► RugPullShared
  └── Microsoft.ML.FastTree
```

RugPullShared exists because ML.NET serializes the custom mapping factory with its assembly name. Both training and inference must reference the same assembly for model loading to work.

## External dependencies

| Dependency | Purpose | Cost |
|-----------|---------|------|
| Microsoft.ML 5.0.0 | Training and inference | Free, MIT |
| Microsoft.ML.FastTree 5.0.0 | FastTree algorithm | Free, MIT |
| DexScreener API | Live token/pool data | Free, ~300 req/min |
| SolRPDS dataset | Training labels | Free, academic |
