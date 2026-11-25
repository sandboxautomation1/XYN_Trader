import csv
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path

from app import FeatureVector, ModelRuntime
from train import train


class TrainingPathTests(unittest.TestCase):
    def test_temporary_matured_dataset_trains_and_loads_a_model(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "synthetic-test-only.csv"
            artifact = root / "model.joblib"
            columns = [
                "symbol",
                "signal_id",
                "timestamp_utc",
                "outcome_matured",
                "ema_20_50_spread",
                "rsi_14",
                "macd",
                "atr_14",
                "adx_14",
                "support_distance_atr",
                "resistance_distance_atr",
                "regime_trending",
                "target_1h",
            ]
            with source.open("w", newline="", encoding="utf-8") as file:
                writer = csv.DictWriter(file, fieldnames=columns)
                writer.writeheader()
                for index in range(40):
                    timestamp = datetime(2026, 1, 1, tzinfo=timezone.utc) + timedelta(days=index)
                    writer.writerow(
                        {
                            "symbol": "XAUUSD",
                            "signal_id": f"test-{index}",
                            "timestamp_utc": timestamp.isoformat(),
                            "outcome_matured": True,
                            "ema_20_50_spread": index / 10,
                            "rsi_14": 45 + index % 20,
                            "macd": (index % 10) / 10,
                            "atr_14": 1 + index / 10,
                            "adx_14": 15 + index % 25,
                            "support_distance_atr": index / 20,
                            "resistance_distance_atr": index / 15,
                            "regime_trending": index % 2,
                            "target_1h": index % 2,
                        }
                    )

            result = train(source, artifact, "1h")
            runtime = ModelRuntime(artifact)
            runtime.load()
            prediction = runtime.predict(
                FeatureVector(
                    ema_20_50_spread=0.5,
                    rsi_14=55,
                    macd=0.4,
                    atr_14=2,
                    adx_14=20,
                    support_distance_atr=0.5,
                    resistance_distance_atr=0.8,
                    regime_trending=1,
                ),
                "1h",
            )

            self.assertTrue(artifact.is_file())
            self.assertEqual(32, result["training_rows"])
            self.assertEqual(8, result["holdout_rows"])
            self.assertGreaterEqual(prediction.probability_positive_outcome, 0)
            self.assertLessEqual(prediction.probability_positive_outcome, 1)


if __name__ == "__main__":
    unittest.main()
