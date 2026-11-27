import os
import json
import math
import tempfile
from contextlib import asynccontextmanager
from datetime import datetime, timezone
from pathlib import Path
from typing import Annotated, Literal

import joblib
import pandas as pd
from fastapi import BackgroundTasks, FastAPI, HTTPException, Path as ApiPath
from pydantic import BaseModel, ConfigDict, Field, model_validator
from pydantic.alias_generators import to_camel

FEATURE_COLUMNS = (
    "ema_20_50_spread",
    "rsi_14",
    "macd",
    "atr_14",
    "adx_14",
    "support_distance_atr",
    "resistance_distance_atr",
    "regime_trending",
)
MODEL_SCHEMA_VERSION = "xauusd-lgbm-v3"
MODEL_PATH = Path(
    os.environ.get(
        "XYN_MODEL_PATH",
        os.environ.get("XYN_MODEL_DIR", str(Path(__file__).resolve().parent / "models")),
    )
)
POLICY_PATH = Path(os.environ.get("XYN_SIGNAL_POLICY_PATH", str(Path(__file__).with_name("signal_policy.json"))))
MAXIMUM_CLOSED_CANDLE_AGE = pd.Timedelta(hours=4)

FiniteFloat = Annotated[float, Field(allow_inf_nan=False)]


class FeatureVector(BaseModel):
    model_config = ConfigDict(extra="forbid")

    ema_20_50_spread: FiniteFloat
    rsi_14: FiniteFloat = Field(ge=0, le=100)
    macd: FiniteFloat
    atr_14: FiniteFloat = Field(gt=0)
    adx_14: FiniteFloat = Field(ge=0, le=100)
    support_distance_atr: FiniteFloat
    resistance_distance_atr: FiniteFloat
    regime_trending: FiniteFloat = Field(ge=0, le=1)
    direction: Literal["BUY BELOW", "SELL BELOW"] = "BUY BELOW"

    def model_values(self) -> dict[str, float]:
        """Orient common market features to the proposed trade direction."""
        direction_sign = 1 if self.direction == "BUY BELOW" else -1
        return {
            "ema_20_50_spread": self.ema_20_50_spread / self.atr_14 * direction_sign,
            "rsi_14": self.rsi_14 if direction_sign == 1 else 100 - self.rsi_14,
            "macd": self.macd / self.atr_14 * direction_sign,
            "atr_14": self.atr_14,
            "adx_14": self.adx_14,
            "support_distance_atr": (
                self.support_distance_atr if direction_sign == 1 else self.resistance_distance_atr
            ),
            "resistance_distance_atr": (
                self.resistance_distance_atr if direction_sign == 1 else self.support_distance_atr
            ),
            "regime_trending": self.regime_trending,
        }


class PredictionRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    symbol: str = "XAUUSD"
    horizon: str = "1h"
    features: FeatureVector
    policy_version: str = Field(default="swing-zones-atr-v2", min_length=1)
    policy_sha256: str | None = Field(default=None, pattern=r"^[0-9a-f]{64}$")


class PredictionResponse(BaseModel):
    symbol: str
    direction: Literal["BUY BELOW", "SELL BELOW"]
    horizon: str
    policy_version: str
    probability_positive_outcome: float
    raw_probability_positive_outcome: float
    calibration_method: str
    is_calibrated: bool
    model_version: str
    model_schema_version: str
    policy_sha256: str | None = None
    outcome_definition: str = "TP1 reached before stop loss after entry"
    disclaimer: str = "Model estimate; not a trade recommendation."


class ModelRuntime:
    def __init__(self, path: Path) -> None:
        self.path = path
        self.artifacts: dict[str, dict] = {}

    @property
    def artifact(self) -> dict | None:
        return next(iter(self.artifacts.values())) if len(self.artifacts) == 1 else None

    def load(self) -> None:
        loaded_artifacts: dict[str, dict] = {}
        artifact_paths = (
            [self.path]
            if self.path.is_file()
            else [self.path / f"lightgbm_{horizon}.joblib" for horizon in ("1h", "24h", "48h")]
        )
        for artifact_path in artifact_paths:
            if not artifact_path.is_file():
                continue
            artifact = joblib.load(artifact_path)
            if (
                not isinstance(artifact, dict)
                or artifact.get("schema_version") != MODEL_SCHEMA_VERSION
                or tuple(artifact.get("feature_columns", ())) != FEATURE_COLUMNS
                or not artifact.get("model_version")
                or artifact.get("horizon") not in {"1h", "24h", "48h"}
                or "classifier" not in artifact
                or artifact.get("calibration_method", "none") not in {"none", "platt_sigmoid", "isotonic"}
                or (
                    artifact.get("calibration_method", "none") != "none"
                    and artifact.get("calibrator") is None
                )
            ):
                raise RuntimeError(f"Model artifact has an incompatible schema: {artifact_path.name}")
            horizon = artifact["horizon"]
            if artifact_path.name == f"lightgbm_{horizon}.joblib" and artifact.get("approval_status") != "approved":
                raise RuntimeError(f"Active model is not approved: {artifact_path.name}")
            if horizon in loaded_artifacts:
                raise RuntimeError(f"Multiple model artifacts are configured for {horizon}.")
            loaded_artifacts[horizon] = artifact
        self.artifacts = loaded_artifacts

    def predict(
        self,
        features: FeatureVector,
        horizon: str,
        policy_version: str | None = None,
        policy_sha256: str | None = None,
    ) -> PredictionResponse:
        if not self.artifacts:
            raise ModelUnavailable
        artifact = self.artifacts.get(horizon)
        if artifact is None:
            raise HorizonUnavailable
        model_policy_version = artifact.get("policy_version", "legacy-unspecified")
        if policy_version is not None and model_policy_version not in {"legacy-unspecified", policy_version}:
            raise PolicyVersionUnavailable
        model_policy_sha256 = artifact.get("policy_sha256")
        if model_policy_sha256 is not None and model_policy_sha256 != policy_sha256:
            raise PolicyVersionUnavailable

        values = features.model_values()
        row = pd.DataFrame([[values[column] for column in FEATURE_COLUMNS]], columns=FEATURE_COLUMNS)
        raw_probability = float(artifact["classifier"].predict_proba(row)[0][1])
        calibration_method = str(artifact.get("calibration_method", "none"))
        calibrator = artifact.get("calibrator")
        if calibrator is None or calibration_method == "none":
            probability = raw_probability
            calibration_method = "none"
        elif calibration_method == "isotonic":
            probability = float(calibrator.predict([raw_probability])[0])
        elif calibration_method == "platt_sigmoid":
            clipped = min(max(raw_probability, 1e-6), 1 - 1e-6)
            logit = [[math.log(clipped / (1 - clipped))]]
            probability = float(calibrator.predict_proba(logit)[0][1])
        else:
            raise RuntimeError(f"Unsupported calibration method: {calibration_method}")
        return PredictionResponse(
            symbol="XAUUSD",
            direction=features.direction,
            horizon=horizon,
            policy_version=str(model_policy_version),
            policy_sha256=model_policy_sha256,
            probability_positive_outcome=probability,
            raw_probability_positive_outcome=raw_probability,
            calibration_method=calibration_method,
            is_calibrated=calibrator is not None and calibration_method != "none",
            model_version=artifact["model_version"],
            model_schema_version=MODEL_SCHEMA_VERSION,
            disclaimer=(
                "Calibrated historical outcome probability; not a trade recommendation."
                if calibrator is not None and calibration_method != "none"
                else "Uncalibrated model output; not a trade recommendation."
            ),
        )


class ModelUnavailable(Exception):
    pass


class HorizonUnavailable(Exception):
    pass


class PolicyVersionUnavailable(Exception):
    pass


class CandidateCandle(BaseModel):
    model_config = ConfigDict(extra="forbid", alias_generator=to_camel, populate_by_name=True)

    timestamp_utc: datetime
    open: FiniteFloat = Field(gt=0)
    high: FiniteFloat = Field(gt=0)
    low: FiniteFloat = Field(gt=0)
    close: FiniteFloat = Field(gt=0)

    @model_validator(mode="after")
    def validate_ohlc(self) -> "CandidateCandle":
        if self.timestamp_utc.tzinfo is None:
            raise ValueError("Candle timestamp must include a timezone.")
        if self.low > min(self.open, self.close) or self.high < max(self.open, self.close) or self.low > self.high:
            raise ValueError("Candle OHLC values are inconsistent.")
        return self


class CandidateGenerationRequest(BaseModel):
    model_config = ConfigDict(extra="forbid", alias_generator=to_camel, populate_by_name=True)

    symbol: Literal["XAUUSD"] = "XAUUSD"
    interval: Literal["1h"] = "1h"
    candles: list[CandidateCandle] = Field(min_length=100, max_length=1000)


class LearningIngestRequest(BaseModel):
    model_config = ConfigDict(extra="forbid", alias_generator=to_camel, populate_by_name=True)

    symbol: Literal["XAUUSD"] = "XAUUSD"
    interval: Literal["1h", "5min"] = "1h"
    candles: list[CandidateCandle] = Field(min_length=1, max_length=1000)
    run_learning_cycle: bool = True


class LearningCandidateActivationRequest(BaseModel):
    model_config = ConfigDict(extra="forbid", alias_generator=to_camel, populate_by_name=True)

    approved_by: str = Field(min_length=2, max_length=120)


class UserSignalJournalUpdate(BaseModel):
    model_config = ConfigDict(extra="forbid", alias_generator=to_camel, populate_by_name=True)

    is_favorite: bool = False
    note: str = Field(default="", max_length=4000)


class GeneratedCandidate(BaseModel):
    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True)

    signal_id: str
    symbol: str
    timeframe: str
    timestamp_utc: datetime
    direction: Literal["BUY BELOW", "SELL BELOW"]
    entry_order_type: Literal["BUY LIMIT", "SELL LIMIT"] | None = None
    entry_price: float | None = None
    status: Literal["CREATED", "TRIGGERED", "CLOSED_TP", "CLOSED_SL", "EXPIRED"]
    horizon_hours: int
    entry_low: float
    entry_high: float
    stop_loss: float
    tp1: float
    tp2: float
    tp3: float
    tp1_risk_reward: float
    tp2_risk_reward: float
    tp3_risk_reward: float
    zone_touch_count: int
    adx_14: float
    policy_version: str
    policy_sha256: str
    model_probability_positive_outcome: float | None = None
    model_raw_probability_positive_outcome: float | None = None
    model_outcome_horizon_hours: int | None = None
    model_version: str | None = None
    model_calibration_method: str | None = None
    model_is_calibrated: bool | None = None


class CandidateGenerationResponse(BaseModel):
    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True)

    symbol: str
    timeframe: str
    generated_at_utc: datetime
    as_of_utc: datetime
    horizon_hours: int
    policy_version: str
    policy_sha256: str
    model_status: str
    is_stale: bool = False
    candidates: list[GeneratedCandidate]
    message: str
    model_outcome_horizon_hours: int | None = None


class CandidateFeedUnavailable(ValueError):
    """The request does not contain a fresh, usable H1 market feed."""


def _candidate_status(
    candidate: dict[str, object],
    candles: pd.DataFrame,
    as_of_utc: pd.Timestamp,
    horizon_hours: int,
) -> str:
    direction = str(candidate["direction"])
    decision_time = pd.Timestamp(candidate["timestamp_utc"])
    expires_at = decision_time + pd.Timedelta(hours=horizon_hours)
    future = candles.loc[
        (candles["timestamp_utc"] >= decision_time)
        & (candles["timestamp_utc"] + pd.Timedelta(hours=1) <= min(as_of_utc, expires_at))
    ]
    entered = False
    status = "CREATED"
    for bar in future.itertuples(index=False):
        entry_touched = float(bar.low) <= float(candidate["entry_high"]) and float(bar.high) >= float(candidate["entry_low"])
        stop_hit = float(bar.low) <= float(candidate["stop_loss"]) if direction == "BUY BELOW" else float(bar.high) >= float(candidate["stop_loss"])
        tp_hit = float(bar.high) >= float(candidate["tp1"]) if direction == "BUY BELOW" else float(bar.low) <= float(candidate["tp1"])
        if not entered:
            if entry_touched:
                if stop_hit:
                    return "CLOSED_SL"
                if tp_hit:
                    return "CLOSED_TP"
                entered = True
                status = "TRIGGERED"
            elif stop_hit or tp_hit:
                return "EXPIRED"
            continue
        if entered:
            if stop_hit:
                return "CLOSED_SL"
            if tp_hit:
                return "CLOSED_TP"

    if as_of_utc >= expires_at:
        return "EXPIRED"
    return status


def _candidate_scoring_model(
    policy_version: str,
    policy_sha256: str,
    candidate_horizon_hours: int,
) -> tuple[str | None, str]:
    if not runtime.artifacts:
        return None, "not_trained"

    preferred_horizons = [
        f"{candidate_horizon_hours}h",
        "24h",
        "48h",
        "1h",
    ]
    for horizon in dict.fromkeys(preferred_horizons):
        artifact = runtime.artifacts.get(horizon)
        if (
            artifact is not None
            and artifact.get("policy_version") == policy_version
            and artifact.get("policy_sha256") == policy_sha256
        ):
            return horizon, "loaded"

    return None, "policy_mismatch"


def _generate_candidate_response(request: CandidateGenerationRequest) -> CandidateGenerationResponse:
    from generate_candidates import generate_candidates

    now = pd.Timestamp.now(tz="UTC")
    frame = pd.DataFrame(
        [
            {
                "timestamp_utc": pd.Timestamp(candle.timestamp_utc).tz_convert("UTC"),
                "open": candle.open,
                "high": candle.high,
                "low": candle.low,
                "close": candle.close,
            }
            for candle in request.candles
        ]
    ).sort_values("timestamp_utc").reset_index(drop=True)
    if frame["timestamp_utc"].duplicated().any():
        raise ValueError("Candle timestamps must be unique.")
    frame = frame.loc[frame["timestamp_utc"] + pd.Timedelta(hours=1) <= now].reset_index(drop=True)
    if len(frame) < 100:
        raise CandidateFeedUnavailable("At least 100 fully closed H1 candles are required.")

    as_of_utc = frame["timestamp_utc"].max() + pd.Timedelta(hours=1)
    if as_of_utc > now + pd.Timedelta(minutes=1):
        raise CandidateFeedUnavailable("The latest closed H1 candle is in the future.")
    if now - as_of_utc > MAXIMUM_CLOSED_CANDLE_AGE:
        raise CandidateFeedUnavailable("The latest closed H1 candle is older than four hours.")

    policy = json.loads(POLICY_PATH.read_text(encoding="utf-8"))
    horizon_hours = int(policy["candidate_horizon_hours"])
    with tempfile.TemporaryDirectory(prefix="xyn-candidates-") as temporary_directory:
        temporary_path = Path(temporary_directory)
        candles_path = temporary_path / "candles.csv"
        output_path = temporary_path / "candidates.csv"
        frame.to_csv(candles_path, index=False)
        metadata = generate_candidates(candles_path, output_path, POLICY_PATH, request.interval)
        rows = pd.read_csv(output_path).to_dict(orient="records")

    cutoff = as_of_utc - pd.Timedelta(hours=horizon_hours)
    policy_version = str(metadata["policy_version"])
    policy_sha256 = str(metadata["policy_sha256"])
    scoring_horizon, model_status = _candidate_scoring_model(
        policy_version,
        policy_sha256,
        horizon_hours,
    )
    candidates: list[GeneratedCandidate] = []
    for row in rows:
        candidate_time = pd.Timestamp(row["timestamp_utc"])
        if candidate_time < cutoff or candidate_time > as_of_utc:
            continue
        prediction = None
        if scoring_horizon is not None:
            features = FeatureVector(
                ema_20_50_spread=float(row["model_ema_20_50_spread"]),
                rsi_14=float(row["model_rsi_14"]),
                macd=float(row["model_macd"]),
                atr_14=float(row["atr_14"]),
                adx_14=float(row["adx_14"]),
                support_distance_atr=float(row["support_distance_atr"]),
                resistance_distance_atr=float(row["resistance_distance_atr"]),
                regime_trending=float(row["model_regime_trending"]),
                direction=str(row["direction"]),
            )
            prediction = runtime.predict(
                features,
                scoring_horizon,
                policy_version,
                policy_sha256,
            )
        candidates.append(
            GeneratedCandidate(
                signal_id=str(row["signal_id"]),
                symbol=str(row["symbol"]),
                timeframe=str(row["timeframe"]),
                timestamp_utc=candidate_time.to_pydatetime(),
                direction=str(row["direction"]),
                entry_order_type=str(row["entry_order_type"]),
                entry_price=float(row["entry_price"]),
                status=_candidate_status(row, frame, as_of_utc, horizon_hours),
                horizon_hours=horizon_hours,
                entry_low=float(row["entry_low"]),
                entry_high=float(row["entry_high"]),
                stop_loss=float(row["stop_loss"]),
                tp1=float(row["tp1"]),
                tp2=float(row["tp2"]),
                tp3=float(row["tp3"]),
                tp1_risk_reward=float(row["tp1_risk_reward"]),
                tp2_risk_reward=float(row["tp2_risk_reward"]),
                tp3_risk_reward=float(row["tp3_risk_reward"]),
                zone_touch_count=int(row["zone_touch_count"]),
                adx_14=float(row["adx_14"]),
                policy_version=str(row["policy_version"]),
                policy_sha256=str(row["policy_sha256"]),
                model_probability_positive_outcome=(
                    prediction.probability_positive_outcome if prediction is not None else None
                ),
                model_raw_probability_positive_outcome=(
                    prediction.raw_probability_positive_outcome if prediction is not None else None
                ),
                model_outcome_horizon_hours=(
                    int(scoring_horizon[:-1]) if prediction is not None and scoring_horizon is not None else None
                ),
                model_version=prediction.model_version if prediction is not None else None,
                model_calibration_method=prediction.calibration_method if prediction is not None else None,
                model_is_calibrated=prediction.is_calibrated if prediction is not None else None,
            )
        )
    candidates.sort(key=lambda candidate: candidate.timestamp_utc, reverse=True)
    response = CandidateGenerationResponse(
        symbol="XAUUSD",
        timeframe="H1",
        generated_at_utc=datetime.now(timezone.utc),
        as_of_utc=as_of_utc.to_pydatetime(),
        horizon_hours=horizon_hours,
        policy_version=policy_version,
        policy_sha256=policy_sha256,
        model_status=model_status,
        is_stale=False,
        candidates=candidates,
        model_outcome_horizon_hours=(
            int(scoring_horizon[:-1]) if scoring_horizon is not None else None
        ),
        message=(
            "No candidate matches the active policy; market state is NEUTRAL."
            if not candidates
            else (
                f"Rule-based candidates include calibrated {scoring_horizon} model probabilities."
                if scoring_horizon is not None
                else "Rule-based candidates are available, but the active model does not match this policy."
                if model_status == "policy_mismatch"
                else "Rule-based candidates are available, but no trained model is active."
            )
        ),
    )
    from candidate_store import save_candidate_feed

    save_candidate_feed(response.model_dump(mode="json", by_alias=True))
    return response


runtime = ModelRuntime(MODEL_PATH)


@asynccontextmanager
async def lifespan(_: FastAPI):
    runtime.load()
    yield


app = FastAPI(title="XYN XAUUSD AI Service", version="0.1.0", lifespan=lifespan)


def _camelize(value: object) -> object:
    if isinstance(value, dict):
        return {to_camel(str(key)): _camelize(item) for key, item in value.items()}
    if isinstance(value, list):
        return [_camelize(item) for item in value]
    return value


@app.get("/health")
def health() -> dict[str, object]:
    return {
        "status": "ok",
        "model_loaded": bool(runtime.artifacts),
        "loaded_horizons": sorted(runtime.artifacts),
        "calibration_methods": {
            horizon: artifact.get("calibration_method", "none")
            for horizon, artifact in runtime.artifacts.items()
        },
        "model_schema_version": MODEL_SCHEMA_VERSION,
    }


@app.post("/api/v1/predict", response_model=PredictionResponse)
def predict(request: PredictionRequest) -> PredictionResponse:
    if request.symbol != "XAUUSD":
        raise HTTPException(status_code=422, detail="Only XAUUSD is supported.")
    if request.horizon not in {"1h", "24h", "48h"}:
        raise HTTPException(status_code=422, detail="Unsupported prediction horizon.")

    try:
        return runtime.predict(
            request.features,
            request.horizon,
            request.policy_version,
            request.policy_sha256,
        )
    except ModelUnavailable as error:
        raise HTTPException(
            status_code=503,
            detail="No trained LightGBM artifact is configured. Train and install a model first.",
        ) from error
    except HorizonUnavailable as error:
        raise HTTPException(
            status_code=503,
            detail="No trained artifact is available for this horizon.",
        ) from error
    except PolicyVersionUnavailable as error:
        raise HTTPException(
            status_code=409,
            detail="The signal policy version does not match the trained model artifact.",
        ) from error


@app.post("/api/v1/candidates/generate", response_model=CandidateGenerationResponse)
def generate_signal_candidates(request: CandidateGenerationRequest) -> CandidateGenerationResponse:
    try:
        return _generate_candidate_response(request)
    except CandidateFeedUnavailable as error:
        raise HTTPException(status_code=503, detail=str(error)) from error
    except (OSError, KeyError, TypeError, ValueError) as error:
        raise HTTPException(
            status_code=503,
            detail=f"Candidate generation failed ({type(error).__name__}). Check the signal policy and candle feed.",
        ) from error


@app.get("/api/v1/candidates/latest", response_model=CandidateGenerationResponse)
def get_latest_signal_candidates() -> CandidateGenerationResponse:
    from candidate_store import get_latest_candidate_feed

    try:
        response = get_latest_candidate_feed()
        if response is None:
            raise HTTPException(status_code=404, detail="No previously generated signal candidates are available.")
        return CandidateGenerationResponse.model_validate(response)
    except HTTPException:
        raise
    except (OSError, ValueError, TypeError) as error:
        raise HTTPException(status_code=503, detail=f"Saved signal candidates are unavailable ({type(error).__name__}).") from error


@app.post("/api/v1/learning/ingest")
def ingest_learning_candles(
    request: LearningIngestRequest,
    background_tasks: BackgroundTasks,
) -> dict[str, object]:
    from learning import get_status, ingest_candles, run_cycle

    try:
        result = ingest_candles(
            [candle.model_dump() for candle in request.candles],
            interval=request.interval,
        )
    except (OSError, ValueError, TypeError) as error:
        raise HTTPException(status_code=422, detail=f"Learning candle intake failed: {error}") from error
    if result["training_scheduled"] and request.run_learning_cycle:
        background_tasks.add_task(run_cycle)
    result["training_scheduled"] = bool(result["training_scheduled"] and request.run_learning_cycle)
    return {**result, "learning": get_status()}


@app.get("/api/v1/learning/status")
def learning_status() -> dict[str, object]:
    from learning import get_status

    try:
        return get_status()
    except (OSError, ValueError, TypeError) as error:
        raise HTTPException(status_code=503, detail=f"Learning status unavailable: {type(error).__name__}.") from error


@app.post("/api/v1/learning/activate-best")
def activate_best_learning_candidate(request: LearningCandidateActivationRequest) -> dict[str, object]:
    from learning import activate_best_candidate

    try:
        promotion = activate_best_candidate(request.approved_by)
    except RuntimeError as error:
        raise HTTPException(status_code=409, detail=str(error)) from error
    except ValueError as error:
        raise HTTPException(status_code=409, detail=str(error)) from error
    try:
        runtime.load()
    except (OSError, TypeError, KeyError, RuntimeError) as error:
        raise HTTPException(
            status_code=503,
            detail=f"Моделът е активиран, но презареждането му се провали ({type(error).__name__}).",
        ) from error
    return _camelize(promotion)


@app.get("/api/v1/user/profile")
def get_user_profile() -> dict[str, object]:
    from user_store import get_profile

    try:
        return get_profile()
    except (OSError, ValueError, TypeError) as error:
        raise HTTPException(status_code=503, detail=f"User profile unavailable: {type(error).__name__}.") from error


@app.put("/api/v1/user/signals/{signal_id}")
def save_user_signal_entry(
    request: UserSignalJournalUpdate,
    signal_id: str = ApiPath(min_length=1, max_length=128, pattern=r"^[A-Za-z0-9._-]+$"),
) -> dict[str, object]:
    from user_store import save_signal_entry

    try:
        entry = save_signal_entry(signal_id, request.is_favorite, request.note)
        return {"entry": entry}
    except (OSError, ValueError, TypeError) as error:
        raise HTTPException(status_code=503, detail=f"User journal could not be saved: {type(error).__name__}.") from error


@app.get("/api/v1/backtesting/run")
def backtest_signals(
    horizon: Literal["1h", "24h", "48h"] = "48h",
    start: datetime | None = None,
    end: datetime | None = None,
) -> dict[str, object]:
    from backtest import run_backtest
    from learning import DATABASE_PATH

    if start and end:
        start_utc = start.replace(tzinfo=timezone.utc) if start.tzinfo is None else start.astimezone(timezone.utc)
        end_utc = end.replace(tzinfo=timezone.utc) if end.tzinfo is None else end.astimezone(timezone.utc)
        if start_utc > end_utc:
            raise HTTPException(status_code=422, detail="Backtest start must be earlier than or equal to end.")

    try:
        return _camelize(run_backtest(
            DATABASE_PATH,
            horizon,
            start.isoformat() if start else None,
            end.isoformat() if end else None,
        ))
    except (OSError, ValueError, TypeError, KeyError) as error:
        raise HTTPException(status_code=503, detail=f"Backtest unavailable: {error}") from error
