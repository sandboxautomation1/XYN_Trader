"""Promote a reviewed model candidate to its horizon's active artifact path."""

from __future__ import annotations

import argparse
import json
import os
import shutil
from datetime import datetime, timezone
from pathlib import Path

import joblib

from app import FEATURE_COLUMNS, MODEL_SCHEMA_VERSION


def promote(
    candidate_path: Path,
    destination: Path,
    approved_by: str,
    minimum_auc: float,
    maximum_brier: float,
    replace_existing: bool,
) -> dict[str, object]:
    if not approved_by.strip():
        raise ValueError("An approver name or review reference is required.")
    if not 0 <= minimum_auc <= 1 or not 0 <= maximum_brier <= 1:
        raise ValueError("AUC and Brier thresholds must be between 0 and 1.")
    if not candidate_path.is_file():
        raise FileNotFoundError(f"Candidate artifact not found: {candidate_path}")

    candidate = joblib.load(candidate_path)
    if (
        not isinstance(candidate, dict)
        or candidate.get("schema_version") != MODEL_SCHEMA_VERSION
        or tuple(candidate.get("feature_columns", ())) != FEATURE_COLUMNS
        or candidate.get("horizon") not in {"1h", "24h", "48h"}
        or not candidate.get("model_version")
        or not candidate.get("policy_version")
        or candidate.get("policy_version") == "legacy-unspecified"
        or "classifier" not in candidate
        or candidate.get("calibration_method", "none") not in {"none", "platt_sigmoid", "isotonic"}
        or (
            candidate.get("calibration_method", "none") != "none"
            and candidate.get("calibrator") is None
        )
    ):
        raise ValueError("Candidate artifact has an incompatible or incomplete schema.")
    expected_name = f"lightgbm_{candidate['horizon']}.joblib"
    if destination.name != expected_name:
        raise ValueError(f"Destination filename must be {expected_name} for this model horizon.")
    policy_hash = candidate.get("policy_sha256")
    if not isinstance(policy_hash, str) or len(policy_hash) != 64 or any(
        character not in "0123456789abcdef" for character in policy_hash
    ):
        raise ValueError("Candidate has no valid signal policy SHA-256; legacy or unversioned models cannot be promoted.")

    auc = candidate.get("holdout_auc")
    brier = candidate.get("holdout_brier_score")
    if auc is None or not 0 <= float(auc) <= 1:
        raise ValueError("A two-class holdout AUC is required before promotion.")
    if float(auc) < minimum_auc:
        raise ValueError(f"Holdout AUC {float(auc):.4f} is below the approved minimum {minimum_auc:.4f}.")
    if brier is None or not 0 <= float(brier) <= 1:
        raise ValueError("A valid holdout Brier score is required before promotion.")
    if float(brier) > maximum_brier:
        raise ValueError(
            f"Holdout Brier score {float(brier):.4f} exceeds the approved maximum {maximum_brier:.4f}."
        )
    if destination.exists() and not replace_existing:
        raise FileExistsError("An active model already exists; pass --replace only after reviewing the replacement.")

    destination.parent.mkdir(parents=True, exist_ok=True)
    archive_path: Path | None = None
    if destination.exists():
        archive_dir = destination.parent / "archive"
        archive_dir.mkdir(parents=True, exist_ok=True)
        archive_path = archive_dir / f"{destination.stem}-{datetime.now(timezone.utc):%Y%m%dT%H%M%S%fZ}.joblib"
        shutil.copy2(destination, archive_path)

    promoted_at = datetime.now(timezone.utc).isoformat()
    candidate["approval_status"] = "approved"
    candidate["approved_by"] = approved_by.strip()
    candidate["promoted_at_utc"] = promoted_at
    candidate["promotion_thresholds"] = {
        "minimum_holdout_auc": minimum_auc,
        "maximum_holdout_brier_score": maximum_brier,
    }
    temporary_path = destination.with_suffix(destination.suffix + ".tmp")
    joblib.dump(candidate, temporary_path)
    os.replace(temporary_path, destination)

    promotion_record = {
        "model_version": candidate["model_version"],
        "horizon": candidate["horizon"],
        "calibration_method": candidate.get("calibration_method", "none"),
        "is_calibrated": candidate.get("calibrator") is not None,
        "calibration_rows": candidate.get("calibration_rows", 0),
        "raw_holdout_brier_score": candidate.get("raw_holdout_brier_score"),
        "holdout_expected_calibration_error": candidate.get("holdout_expected_calibration_error"),
        "policy_version": candidate["policy_version"],
        "policy_sha256": candidate.get("policy_sha256"),
        "approved_by": approved_by.strip(),
        "promoted_at_utc": promoted_at,
        "minimum_holdout_auc": minimum_auc,
        "holdout_auc": float(auc),
        "maximum_holdout_brier_score": maximum_brier,
        "holdout_brier_score": float(brier),
        "dataset_sha256": candidate.get("dataset_sha256"),
        "candidate_artifact": str(candidate_path),
        "active_artifact": str(destination),
        "archived_previous_artifact": str(archive_path) if archive_path else None,
    }
    record_path = destination.with_suffix(destination.suffix + ".promotion.json")
    temporary_record = record_path.with_suffix(record_path.suffix + ".tmp")
    temporary_record.write_text(json.dumps(promotion_record, indent=2), encoding="utf-8")
    os.replace(temporary_record, record_path)
    return promotion_record


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Promote a reviewed LightGBM candidate after explicit holdout thresholds and approval."
    )
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--destination", type=Path, required=True)
    parser.add_argument("--approved-by", required=True, help="Reviewer identity or review ticket reference.")
    parser.add_argument("--minimum-auc", type=float, required=True)
    parser.add_argument("--maximum-brier", type=float, required=True)
    parser.add_argument("--replace", action="store_true", help="Archive and replace an existing active model.")
    args = parser.parse_args()
    record = promote(
        args.candidate,
        args.destination,
        args.approved_by,
        args.minimum_auc,
        args.maximum_brier,
        args.replace,
    )
    print(json.dumps(record, indent=2))


if __name__ == "__main__":
    main()
