"""Atomic repository-session result publication."""

from __future__ import annotations

import json
import os
import tempfile
from pathlib import Path

from .contracts.result import SessionResult


class ResultWriteError(RuntimeError):
    """A validated result could not be published to its required path."""


def write_result_atomic(result: SessionResult, destination: Path) -> None:
    """Serialize and atomically replace ``destination`` in the same directory."""

    destination = destination.resolve()
    parent = destination.parent
    temporary_path: Path | None = None
    try:
        parent.mkdir(parents=True, exist_ok=True)
        payload = json.dumps(
            result.model_dump(mode="json", by_alias=True),
            ensure_ascii=False,
            separators=(",", ":"),
        ).encode("utf-8")
        descriptor, temporary_name = tempfile.mkstemp(
            prefix=f".{destination.name}.",
            suffix=".tmp",
            dir=parent,
        )
        temporary_path = Path(temporary_name)
        try:
            os.chmod(temporary_path, 0o600)
        except BaseException:
            os.close(descriptor)
            raise
        with os.fdopen(descriptor, "wb") as temporary:
            temporary.write(payload)
            temporary.flush()
            os.fsync(temporary.fileno())

        os.replace(temporary_path, destination)
        temporary_path = None
        _sync_directory(parent)
    except Exception as error:
        raise ResultWriteError(f"cannot write result at {destination}") from error
    finally:
        if temporary_path is not None:
            temporary_path.unlink(missing_ok=True)


def _sync_directory(directory: Path) -> None:
    """Persist the directory entry where the platform supports directory fsync."""

    if os.name == "nt":
        return
    descriptor = os.open(directory, os.O_RDONLY)
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)
