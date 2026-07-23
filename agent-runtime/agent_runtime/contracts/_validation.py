"""Reusable validation constraints shared across wire contracts."""

from __future__ import annotations

import re
from pathlib import PurePosixPath, PureWindowsPath
from typing import Annotated
from urllib.parse import urlsplit

from pydantic import Field

GitSha = Annotated[str, Field(pattern=r"^[0-9a-f]{40,64}$")]
JiraKey = Annotated[str, Field(pattern=r"^[A-Z][A-Z0-9_]*-[1-9][0-9]*$")]


def require_absolute_uri(value: str) -> str:
    parsed = urlsplit(value)
    if not parsed.scheme:
        raise ValueError("value must be an absolute URI")
    if parsed.scheme in {"http", "https", "ssh"} and not parsed.netloc:
        raise ValueError("network URI must include an authority")
    return value


def require_relative_git_path(value: str) -> str:
    """Require a normalized, repository-relative Git path."""

    if not value:
        raise ValueError("changed file path cannot be empty")
    if "\\" in value:
        raise ValueError("changed file path must use forward slashes")
    if PurePosixPath(value).is_absolute() or PureWindowsPath(value).is_absolute():
        raise ValueError("changed file path must be repository-relative")
    parts = value.split("/")
    if any(part in {"", ".", ".."} for part in parts):
        raise ValueError("changed file path must be normalized and cannot traverse")
    if re.match(r"^[A-Za-z]:", value):
        raise ValueError("changed file path must not contain a drive prefix")
    return value

