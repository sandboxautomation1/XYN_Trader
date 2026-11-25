import unittest
from pathlib import Path
from tempfile import TemporaryDirectory

from fastapi import HTTPException

from app import FeatureVector, ModelRuntime, ModelUnavailable, PredictionRequest, predict


class ModelRuntimeTests(unittest.TestCase):
    def test_missing_artifact_is_reported_and_never_returns_a_prediction(self) -> None:
        with TemporaryDirectory() as directory:
            runtime = ModelRuntime(Path(directory) / "missing.joblib")
            runtime.load()

            self.assertIsNone(runtime.artifact)
            with self.assertRaises(ModelUnavailable):
                runtime.predict(valid_features(), "1h")

    def test_api_returns_service_unavailable_without_a_model(self) -> None:
        with TemporaryDirectory() as directory:
            runtime = ModelRuntime(Path(directory) / "missing.joblib")
            runtime.load()
            import app

            original_runtime = app.runtime
            app.runtime = runtime
            try:
                with self.assertRaises(HTTPException) as raised:
                    predict(PredictionRequest(features=valid_features()))
                self.assertEqual(503, raised.exception.status_code)
                self.assertIn("No trained LightGBM artifact", raised.exception.detail)
            finally:
                app.runtime = original_runtime

    def test_feature_contract_rejects_out_of_range_rsi(self) -> None:
        with self.assertRaises(ValueError):
            FeatureVector(
                ema_20_50_spread=1,
                rsi_14=120,
                macd=1,
                atr_14=1,
                adx_14=10,
                support_distance_atr=1,
                resistance_distance_atr=1,
                regime_trending=1,
            )


def valid_features() -> FeatureVector:
    return FeatureVector(
        ema_20_50_spread=1,
        rsi_14=55,
        macd=0.4,
        atr_14=5,
        adx_14=20,
        support_distance_atr=0.5,
        resistance_distance_atr=1.2,
        regime_trending=1,
    )


if __name__ == "__main__":
    unittest.main()
