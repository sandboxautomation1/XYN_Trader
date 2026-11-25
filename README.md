# XYN Trader — XAUUSD Signal Engine

ASP.NET Core 10 and Blazor Server source project for the XAUUSD analytics MVP.

## Run the C# web app

The app reads live ticks and candles from the local MetaTrader 5 terminal through `mt5-bridge`. Start the bridge first from its folder:

```powershell
Set-Location mt5-bridge
uv run mt5-bridge server --host 127.0.0.1 --port 8000
```

`127.0.0.1` is sufficient when the bridge and web app run on the same computer. Configure `MT5Bridge:BaseAddress` and `MT5Bridge:Symbol` in `appsettings.json` if your setup differs.

The economic calendar identifies its BLS requests with the `XYN-Trader/0.1` User-Agent. BLS may block automated requests without owner contact information, so set `EconomicCalendar:BlsContact` in the local, git-ignored `appsettings.json` to a public contact such as `mailto:you@example.com` (or set `EconomicCalendar__BlsContact` as an environment variable). The contact is appended to the BLS User-Agent and is not sent to the other calendar providers.

```powershell
if (-not (Test-Path appsettings.json)) {
    Copy-Item appsettings.example.json appsettings.json
}
# Defaults target mt5-bridge on port 8000 and the AI service on port 8001.
dotnet run --project XYN_Trader.csproj
```

`GET /api/market/snapshot` returns the bridge connection state, source, UTC timestamp, and freshness. It returns HTTP 503 when the MT5 terminal is disconnected and HTTP 502 on bridge/data errors. Select M1, M5, M15, M30, H1, H4, or D1 to request that MT5 timeframe. The latest tick is polled from the bridge every five seconds; candles are loaded when the timeframe is selected or refreshed.

The H1 signal generator receives up to 300 MT5 candles; the dashboard and technical analysis request up to 200 candles on other chart intervals. The default broker symbol is `XAUUSD`; change `MT5Bridge:Symbol` if your broker uses a suffix such as `XAUUSD.a`.

`GET /api/analysis?timeframe=H1` calculates the live technical analysis from MT5 OHLC data. Supported timeframes are M1, M5, M15, M30, H1, H4, and D1. RSI uses Wilder smoothing (14), MACD uses EMA 12/26/9, ATR and ADX use Wilder smoothing (14), Bollinger Bands use SMA 20 with two standard deviations, and the trend comparison uses EMA 20/50. Swing support and resistance zones are derived from the latest 120 candles. New ticks update the active candle and recalculate the dashboard and analysis page every five seconds; unavailable readings are shown when history is too short.

## Run the AI service

The separate `ai-service` uses FastAPI and LightGBM for outcome-probability inference. The live generator first builds entry, stop, and target levels from closed H1 candles and the versioned signal policy. A loaded model is then applied to each candidate only when its policy version and checksum match; it estimates the chance of TP1 before the stop within the model horizon. The model scores candidates but does not invent trade levels or a direction. If no rules-based setup exists, the result is `NEUTRAL` even when a model is active.

To start candidate generation without downloading or training a model, start the Python service in a second terminal:

```powershell
Set-Location ai-service
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
.\.venv\Scripts\python.exe -m uvicorn app:app --host 127.0.0.1 --port 8001
```

Port 8000 is reserved for `mt5-bridge`; the AI service runs on 8001. The C# app calls `POST /api/v1/candidates/generate` at `AIService:BaseAddress` (default `http://127.0.0.1:8001/`). `GET /api/signals` and the dashboard use that generator. It receives H1 candles from the bridge and requires at least 100 closed bars, a fresh MT5 tick (15-minute limit), and a latest closed H1 candle no older than four hours. The Python endpoint repeats the closed-candle age check and returns HTTP 503 for insufficient, future-dated, or stale input. If a service or the MT5 terminal is disconnected, the UI reports that condition instead of falling back to demo scenarios.

The news panel uses the GDELT DOC 2.0 article-list API and refreshes through a five-minute server cache. It renders cached headlines immediately while a provider refresh runs and persists the last successful result to `data/news-cache.json` for use after app restarts. `GET /api/news` returns provider status, source links, UTC publication times, the existing headline activity score, and a gold-specific sentiment summary. Sentiment weighs directional phrases about gold, rates, yields, the US dollar, flows, and risk; it applies a six-hour half-life to the aggregate and reports article-level themes and confidence. This remains a transparent lexicon heuristic, not a trained NLP model or a market-impact prediction. `NewsFeed:Enabled`, `BaseAddress`, `Query`, `Timespan`, `MaxArticles`, and `RefreshMinutes` can be changed in `appsettings.json`.

The following offline workflow downloads historical candles from the MT5 terminal through `rates_range`; keep the bridge and terminal connected for the whole date range. No Twelve Data key or API credits are used:

```powershell
Set-Location ai-service
python -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
.\.venv\Scripts\python.exe download_candles.py --interval 1h --start 2024-01-01 --end 2026-11-13 --output data\xauusd_h1.csv
.\.venv\Scripts\python.exe generate_candidates.py --candles data\xauusd_h1.csv --output data\signal_candidates.csv --policy signal_policy.json --interval 1h
.\.venv\Scripts\python.exe prepare_dataset.py --candles data\xauusd_h1.csv --candidates data\signal_candidates.csv --output data\train_48h.csv --horizon 48h --interval 1h
.\.venv\Scripts\python.exe train.py --input data\train_48h.csv --output models\candidates\lightgbm_48h_candidate.joblib --horizon 48h
$env:XYN_MODEL_DIR = "models"
.\.venv\Scripts\python.exe -m uvicorn app:app --host 127.0.0.1 --port 8001
```

`generate_candidates.py` creates pending limit-entry scenarios from confirmed swing zones and a directional EMA20/EMA50 trend, filtered by ADX. Each candidate includes a target entry price at the zone midpoint and the full entry range. The active v2 policy uses entry zones 0.25–2.0 ATR away, one confirmed zone touch, ADX of at least 18, stops 0.35 ATR beyond the zone, and minimum reward/risk ratios of 1.5, 2.0, and 3.0. It emits no trade candidate when the conditions fail. This policy's checksum matches the currently active model; the stricter v3 configuration is preserved in `ai-service/signal_policy_v3.json` and must be retrained and promoted before it can be used with model scoring. Changing the active policy changes its checksum, and each trained model records the policy version and checksum it learned. The signals page checks the latest MT5 bid/ask every five seconds, marks a pending entry when the executable side of the quote enters its range, and shows an in-page alert once per signal while that page is open. Pending signals expire after their configured horizon. This is a notification only; the app does not place the order. Historical statuses CREATED, TRIGGERED, CLOSED_TP, CLOSED_SL, and EXPIRED continue to be evaluated from closed H1 candles; same-bar stop/target ambiguity resolves conservatively to the stop. Both live signal generation and optional historical training downloads use MT5 candles through the bridge.

The active policy is designed for a 48-hour outcome horizon, so the offline example trains on 48h labels. A horizon cannot train until its matured dataset contains both positive and negative outcomes; the current 1h dataset contains no positives. The trainer enforces finite features, a minimum of 30 rows, both classes in the purged training split, and a later chronological holdout. It purges outcomes that overlap the holdout and generates walk-forward, out-of-fold probabilities for calibration. Platt sigmoid calibration is used when at least 20 out-of-fold rows contain both classes; isotonic calibration is used at 1,000 or more rows. Otherwise the candidate stays explicitly uncalibrated. Reports include raw and calibrated holdout Brier scores, expected calibration error, AUC, and a training-prevalence baseline. Candidates should be saved under `models/candidates/`; they are not loaded by the service. `promote_model.py` requires explicit reviewer identity and chosen holdout AUC/Brier thresholds, archives an existing active model before replacement, and records the promotion decision. The service loads active horizon files from `XYN_MODEL_DIR`, or one explicitly configured file using `XYN_MODEL_PATH`. The feature contract is versioned; older model files are rejected.

### Automatic learning cycle

When the .NET app starts, `SignalLearningCollectorService` checks the learning store. If it is empty, it seeds it once from up to 365 days of closed H1 bars through the MT5 `rates_range` endpoint; `AILearning:BackfillDays` controls that window. It then refreshes from the latest MT5 candles immediately and at `AILearning:CollectionIntervalMinutes` (default 15). Candles go to `POST /api/v1/learning/ingest`; Python upserts them into `ai-service/data/learning.sqlite3` and retains at most the latest 5,000 bars by default. Each changed candle schedules a batch learning cycle. The cycle generates historical candidates using the versioned policy, evaluates only matured 1h, 24h, and 48h outcomes, and retrains when the dataset changes and passes the existing holdout and data-quality requirements.

Automatically trained artifacts and review reports are written under `ai-service/models/candidates/`. They are candidates only: the active prediction model and candidate probabilities are unchanged until a reviewer promotes a candidate. The `/signals` page checks all saved candidates against the promotion gate and current policy checksum, ranks passing candidates by Brier skill with AUC as a tie-breaker, and offers an explicit activation button with an approver name or review reference. `POST /api/v1/learning/activate-best` rechecks the gate and policy compatibility at activation time, archives an existing horizon model, reloads the promoted model immediately, and records the decision. The Python status endpoint is `GET /api/v1/learning/status`, proxied by `GET /api/learning/status`. This is supervised batch retraining from observed outcomes, not a self-modifying strategy or reinforcement-learning system.

Successful signal generations are also archived in the same SQLite database (up to 2,000 candidates by default). If the MT5 quote or closed-candle feed is stale or unavailable, the signal page requests `GET /api/v1/candidates/latest` and shows the last saved scenarios with an explicit stale-data notice. Live statuses remain the last ones computed from available candles; scenarios past their configured horizon are shown as expired. This cache does not replace the live feed or invent new prices.

### Local user profile and journal

Personal data is stored separately from candle intake and model-training state in `ai-service/data/user_data.sqlite3` by default. This database creates one local profile and keeps each signal's favorite flag and note without a rolling retention limit. The `/signals` page loads and edits those entries; `GET /api/user/profile` reads the profile and journal, while `PUT /api/user/signals/{signalId}` saves or removes one entry. The Python service exposes the matching `/api/v1/user/...` routes. Set `XYN_USER_DB` to move the profile database; include that file in backups. The current profile is local to this installation and has no authentication or multi-user support.

The prediction endpoint accepts an XAUUSD feature vector, proposed direction, horizon, and matching policy version/checksum. It returns the raw probability, the calibrated probability when an approved artifact contains a calibrator, and calibration metadata. The `/signals` and dashboard pages display the calibrated probability alongside each compatible rules-based candidate. Probabilities describe the historical TP1-before-stop outcome definition and are not a trade recommendation. A deployment without a promoted model returns HTTP 503 for prediction requests; incompatible policy versions are rejected rather than scored.

### Open-position review

The dashboard reads every open terminal position from `GET /positions`, including symbol, side, volume, entry, current price, floating P/L, SL/TP, and broker price precision. It refreshes positions and per-symbol H1 market analysis every 15 seconds. RSI, MACD, ADX, EMA20/EMA50, and ATR14 drive an explainable technical review. A strong opposing H1 score (35/100 or lower against BUY; 65/100 or higher against SELL) suggests considering a close. A profitable move of at least 1.25 ATR can produce a tighter SL suggestion at about 1.5 ATR from price; it is shown only when it improves the existing stop and respects the broker's published stop distance. Restart `mt5-bridge` after updating it so it returns the new precision metadata; the app suppresses SL actions when that metadata is missing.

No trained ML model exists specifically for open-position management, so these are transparent indicator rules rather than model probabilities. GDELT gold sentiment and the next 24 hours of high-impact U.S. calendar events appear as additional context; they do not change the technical score. Stale quotes or insufficient H1 history suppress actionable advice. The dashboard refreshes on its own and also has a manual refresh button.

The dashboard never changes a position on its own. It asks for browser confirmation before sending `POST /modify` or `POST /close`, then fetches the position and market data again and cancels the action if the review changed. MT5 remains responsible for its final trade validation.

### Backtesting

The `/backtesting` page and `GET /api/backtesting?horizon=48h` run the active, versioned signal policy over the H1 candles currently retained in the learning SQLite store. The API also accepts optional UTC `start` and `end` timestamps to select signal creation times; later stored bars still complete the selected signals' outcome horizons. The report includes signal counts, closed trade outcomes in R, win rate, expectancy, profit factor, net R, maximum drawdown, and a compounded equity index at 1% risk per trade. It simulates one open position at a time, fills at the entry-zone midpoint, exits at TP1, stop, or the selected horizon close, and resolves ambiguous OHLC bars against the trade. Spread, commission, and slippage are excluded. The rolling database retains up to 5,000 H1 bars by default, so a longer backtest requires an expanded historical data store.

## Current scope and limitations

- Blazor dashboard requests live ticks and candles from MetaTrader 5 through `mt5-bridge`; source, UTC timestamp, connection status, and a 15-minute freshness classification are shown.
- Signal scenarios are generated from MT5 H1 candles by the Python policy engine; technical indicators, market regime, technical score, and swing zones are calculated from MT5 candles and current ticks.
- The dashboard can request an explicitly confirmed Stop Loss modification or position close through the local MT5 bridge. It does not open positions or perform automatic management.
- The learning database stores a rolling H1 candle window and a bounded signal cache. A separate local user database preserves profile favorites and notes, but this is a single local profile without authentication, account management, or multi-user support. Full historical backfill is not retained in the learning database. There is no shipped trained production model. The U.S. economic calendar uses official BLS, BEA, Census, and Federal Reserve schedules with a persisted stale-data fallback; it is not a guarantee against last-minute agency changes and does not cover every global release. GDELT news activity and sentiment scores are heuristics and may miss or misread market-moving events.
- Tests use a fake HTTP handler for Twelve Data behavior. Python tests use a temporary synthetic dataset only to exercise the training pipeline; no test model is included in the repository.
