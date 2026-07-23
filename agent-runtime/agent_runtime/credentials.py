"""Strict mounted source-control credential loading."""

from __future__ import annotations

import json
from datetime import datetime
from pathlib import Path

from pydantic import ConfigDict, Field

from .contracts._base import CamelContract
from .git_repository import GitCredential


class MountedSourceControlCredential(CamelContract):
    model_config = ConfigDict(extra="forbid")

    username: str = Field(min_length=1)
    secret: str = Field(min_length=1, repr=False)
    expires_at: datetime | None = None

    def as_git_credential(self) -> GitCredential:
        return GitCredential(self.username, self.secret)


def load_source_control_credential(path: Path) -> GitCredential:
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise ValueError(f"cannot read source-control credential: {path}") from error
    return MountedSourceControlCredential.model_validate(payload).as_git_credential()
