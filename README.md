# solana-rugpull-detector

ML-powered rug pull detection for Solana tokens. Trained on the [SolRPDS dataset](https://github.com/DeFiLabX/SolRPDS) (116K liquidity pools), exposed as both a REST API and an MCP server.

## What it does

- **ML prediction** — Binary classifier (FastTree, 300 trees) trained on real Solana liquidity pool data. Feed in pool metrics, get a rug pull probability.
- **Live token analysis** — Pass a token mint address. The server pulls live pool data from DexScreener and scores risk based on liquidity depth, buy/sell pressure, price action, and token age.
- **Dual interface** — Same model, two ways in: HTTP API for bots/scripts/apps, MCP (stdio) for AI agents like Claude.

## Model performance

Trained on 116K pools from SolRPDS (2021-2024), 80/20 split:

| Metric | Score |
|--------|-------|
| Accuracy | 86.6% |
| AUC | 89.5% |
| Precision | 71.7% |
| Recall | 52.0% |

5 features: total added/removed liquidity, number of adds/removes, add-to-remove ratio. Recall is limited by feature count — adding holder concentration, lock status, and token age would improve it.

## Quick start

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

### 1. Clone and get the dataset

```bash
git clone https://github.com/your-username/solana-rugpull-detector.git
cd solana-rugpull-detector
git clone https://github.com/DeFiLabX/SolRPDS.git
```

### 2. Prepare training data

Combine the CSV files from SolRPDS into a single dataset:

```bash
head -1 SolRPDS/dataset/CSV/2021.csv > RugPullTrainer/solrpds_data.csv
tail -n +2 -q SolRPDS/dataset/CSV/*.csv >> RugPullTrainer/solrpds_data.csv
```

### 3. Train the model

```bash
cd RugPullTrainer
dotnet run
```

This trains a FastTree binary classifier and saves `rugpull_model.zip`. Takes about 2 seconds.

### 4. Copy model and run the server

```bash
cp rugpull_model.zip ../RugPullServer/
cd ../RugPullServer
dotnet run
```

Server starts on `http://localhost:5000`.

## API usage

### Analyze a token by mint address

```bash
curl http://localhost:5000/api/analyze/DezXAZ8z7PnrnRJjz3wXBoRgixCa6xjnB7YaB1pPB263
```

```json
{
  "token": { "name": "Bonk", "symbol": "Bonk", "mintAddress": "DezXAZ..." },
  "topPool": {
    "dex": "orca",
    "liquidityUsd": 843139.49,
    "buys24h": 216,
    "sells24h": 111,
    "priceChange24h": 2.62
  },
  "overallRisk": "LOW",
  "riskScore": 0,
  "flags": [
    { "level": "ok", "message": "Healthy liquidity ($843,139)" },
    { "level": "ok", "message": "Healthy trading: 216 buys vs 111 sells (24h)" }
  ]
}
```

### ML prediction with raw pool metrics

```bash
curl -X POST http://localhost:5000/api/check \
  -H "Content-Type: application/json" \
  -d '{
    "totalAddedLiquidity": 50000,
    "totalRemovedLiquidity": 48500,
    "numLiquidityAdds": 3,
    "numLiquidityRemoves": 1,
    "addToRemoveRatio": 3.0
  }'
```

### MCP mode (for Claude Code / Claude Desktop)

```bash
dotnet run -- --mcp
```

Exposes two MCP tools over stdio:
- `check_rug_pull` — ML prediction from raw numbers
- `analyze_token` — live lookup + heuristic scoring by mint address

Register with Claude Code:
```bash
claude mcp add rugpull-detector -- dotnet run --project /path/to/RugPullServer -- --mcp
```

## Heuristic scoring

The `analyze_token` endpoint scores risk on a 0-10 scale:

| Signal | Condition | Points |
|--------|-----------|--------|
| Liquidity | < $1K | +3 |
| Liquidity | < $10K | +2 |
| Liquidity | < $50K | +1 |
| Sell pressure | Sells > 2x buys (24h) | +2 |
| Sell pressure | Sells > buys (24h) | +1 |
| Price crash | > 50% drop (24h) | +2 |
| Price drop | > 20% drop (24h) | +1 |
| Token age | < 24 hours | +2 |
| Token age | < 7 days | +1 |
| Wash trading | Volume > 10x liquidity | +1 |

Risk levels: 0-2 LOW, 3-4 MEDIUM, 5-7 HIGH, 8-10 CRITICAL

## Project structure

```
RugPullShared/       Data classes and label mapping (shared library)
RugPullTrainer/      One-time model training pipeline
RugPullServer/       Dual-mode server (HTTP + MCP)
```

## Dataset

Uses [SolRPDS](https://github.com/DeFiLabX/SolRPDS) — a labeled dataset of Solana liquidity pools covering 2021 through November 2024. The INACTIVITY_STATUS column serves as the label: "Inactive" pools are treated as rug pulls.

Live token analysis uses [DexScreener's public API](https://docs.dexscreener.com) (free, no key required).

## License

MIT
