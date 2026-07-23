"""Write a deterministic, schema-shaped Akos Fabric session result."""

from __future__ import annotations

import json
import os
import re
import sys
from datetime import UTC, datetime
from pathlib import Path
from typing import Any, NoReturn
from urllib.parse import urlsplit
from uuid import UUID

JIRA_KEY_PATTERN = re.compile(r"^[A-Z][A-Z0-9_]*-[1-9][0-9]*$")


class ManifestError(ValueError):
    """The supplied manifest cannot produce a schema-shaped result."""


def fail(message: str) -> NoReturn:
    print(f"synthetic agent failed: {message}", file=sys.stderr)
    raise SystemExit(2)


def environment_path(name: str) -> Path:
    value = os.environ.get(name)
    if not value:
        fail(f"{name} is required")

    path = Path(value)
    if not path.is_absolute():
        fail(f"{name} must be an absolute path")
    return path


def require_object(value: Any, field: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise ManifestError(f"{field} must be an object")
    return value


def require_non_empty_string(value: Any, field: str) -> str:
    if not isinstance(value, str) or not value:
        raise ManifestError(f"{field} must be a non-empty string")
    return value


def require_uuid(value: Any, field: str) -> str:
    text = require_non_empty_string(value, field)
    try:
        UUID(text)
    except ValueError as error:
        raise ManifestError(f"{field} must be a UUID") from error
    return text


def require_clone_url(value: Any) -> str:
    clone_url = require_non_empty_string(value, "mainRepository.cloneUrl")
    parsed = urlsplit(clone_url)
    if not parsed.scheme or not (parsed.netloc or parsed.path):
        raise ManifestError("mainRepository.cloneUrl must be an absolute URI")
    if parsed.username is not None or parsed.password is not None:
        raise ManifestError("mainRepository.cloneUrl must not contain credentials")
    return clone_url


def utc_timestamp() -> str:
    return datetime.now(UTC).isoformat(timespec="milliseconds").replace("+00:00", "Z")


def empty_role_model_usage() -> dict[str, int | float]:
    return {
        "inputTokens": 0,
        "outputTokens": 0,
        "modelCalls": 0,
        "estimatedCostUsd": 0.0,
    }


def load_manifest(path: Path) -> dict[str, Any]:
    try:
        with path.open("r", encoding="utf-8") as manifest_file:
            value = json.load(manifest_file)
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise ManifestError("TASK_MANIFEST is not readable JSON") from error

    manifest = require_object(value, "manifest")
    if manifest.get("schemaVersion") != 1:
        raise ManifestError("schemaVersion must be 1")
    return manifest


def build_result(manifest: dict[str, Any], started_at: str) -> dict[str, Any]:
    repository_session_id = require_uuid(
        manifest.get("repositorySessionId"), "repositorySessionId"
    )
    source_control = require_object(manifest.get("sourceControl"), "sourceControl")
    main_repository = require_object(
        manifest.get("mainRepository"), "mainRepository"
    )
    llm = require_object(manifest.get("llm"), "llm")
    llm_provider = require_non_empty_string(llm.get("provider"), "llm.provider")
    llm_model_id = require_non_empty_string(llm.get("modelId"), "llm.modelId")

    work_items_value = manifest.get("workItems")
    if not isinstance(work_items_value, list) or not work_items_value:
        raise ManifestError("workItems must be a non-empty array")

    items: list[dict[str, Any]] = []
    for index, value in enumerate(work_items_value):
        item = require_object(value, f"workItems[{index}]")
        jira_key = require_non_empty_string(
            item.get("jiraKey"), f"workItems[{index}].jiraKey"
        )
        if not JIRA_KEY_PATTERN.fullmatch(jira_key):
            raise ManifestError(f"workItems[{index}].jiraKey has an invalid format")

        items.append(
            {
                "workItemRunId": require_uuid(
                    item.get("workItemRunId"),
                    f"workItems[{index}].workItemRunId",
                ),
                "jiraKey": jira_key,
                "status": "blocked",
                "baseCommitSha": None,
                "branchName": None,
                "candidateCommitSha": None,
                "changedFiles": [],
                "plan": None,
                "candidate": None,
                "verification": None,
                "judgment": None,
                "modelUsage": {
                    "provider": llm_provider,
                    "modelId": llm_model_id,
                    "planner": empty_role_model_usage(),
                    "coder": empty_role_model_usage(),
                    "judge": empty_role_model_usage(),
                    "totalEstimatedCostUsd": 0.0,
                },
                "failureCode": "synthetic_agent",
                "failureMessage": (
                    "Synthetic lifecycle probe intentionally performs no repository work."
                ),
            }
        )

    return {
        "schemaVersion": 1,
        "repositorySessionId": repository_session_id,
        "status": "completed",
        "startedAt": started_at,
        "completedAt": utc_timestamp(),
        "failureCode": None,
        "failureMessage": None,
        "repository": {
            "provider": require_non_empty_string(
                source_control.get("provider"), "sourceControl.provider"
            ),
            "providerRepositoryId": require_non_empty_string(
                main_repository.get("providerRepositoryId"),
                "mainRepository.providerRepositoryId",
            ),
            "cloneUrl": require_clone_url(main_repository.get("cloneUrl")),
        },
        "llm": {
            "provider": llm_provider,
            "modelId": llm_model_id,
        },
        "items": items,
    }


def write_json_atomic(path: Path, value: dict[str, Any]) -> None:
    if not path.parent.is_dir():
        raise OSError("RESULT_PATH parent directory does not exist")

    temporary_path = path.with_name(f"{path.name}.tmp")
    flags = os.O_WRONLY | os.O_CREAT | os.O_TRUNC
    if hasattr(os, "O_NOFOLLOW"):
        flags |= os.O_NOFOLLOW

    descriptor = os.open(temporary_path, flags, 0o640)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as result_file:
            json.dump(value, result_file, ensure_ascii=False, separators=(",", ":"))
            result_file.write("\n")
            result_file.flush()
            os.fsync(result_file.fileno())
        os.chmod(temporary_path, 0o640)
        os.replace(temporary_path, path)
    except BaseException:
        temporary_path.unlink(missing_ok=True)
        raise

    # Directory fsync makes the rename durable on the Linux container
    # filesystem. Windows does not allow directories to be opened this way;
    # retaining this branch makes host-side smoke tests possible.
    if os.name != "nt":
        directory_flags = os.O_RDONLY
        if hasattr(os, "O_DIRECTORY"):
            directory_flags |= os.O_DIRECTORY
        directory_descriptor = os.open(path.parent, directory_flags)
        try:
            os.fsync(directory_descriptor)
        finally:
            os.close(directory_descriptor)


def main() -> None:
    started_at = utc_timestamp()
    manifest_path = environment_path("TASK_MANIFEST")
    result_path = environment_path("RESULT_PATH")
    if manifest_path.parent != result_path.parent:
        fail("TASK_MANIFEST and RESULT_PATH must share a session directory")
    if manifest_path == result_path:
        fail("TASK_MANIFEST and RESULT_PATH must be different files")

    try:
        manifest = load_manifest(manifest_path)
        result = build_result(manifest, started_at)
        write_json_atomic(result_path, result)
    except ManifestError as error:
        fail(str(error))
    except OSError:
        fail("result could not be written atomically")

    print("synthetic agent completed")


if __name__ == "__main__":
    main()
