"""Typed terminal and persisted contracts for informational CI review."""

from __future__ import annotations

from typing import Literal

from pydantic import Field, field_validator

from ._base import CamelContract, StrictContract
from ._validation import GitSha, require_relative_git_path

INFORMATIONAL_REVIEW_MARKER = "<!-- akos-fabric:informational-review:v1 -->"


class CiReviewFinding(StrictContract):
    severity: Literal["blocking", "major", "minor", "informational"]
    category: Literal[
        "correctness",
        "acceptance_criteria",
        "testing",
        "maintainability",
        "performance",
        "security",
        "standards",
    ]
    path: str | None = None
    line: int | None = Field(default=None, ge=1)
    explanation: str = Field(min_length=1)
    suggested_change: str | None = None

    @field_validator("path")
    @classmethod
    def validate_path(cls, value: str | None) -> str | None:
        return None if value is None else require_relative_git_path(value)


class ReviewV1(StrictContract):
    """The model's advisory completion; the harness verifies its revision."""

    schema_version: Literal["1.0"] = "1.0"
    reviewed_revision_sha: GitSha
    summary: str = Field(min_length=1)
    findings: list[CiReviewFinding]


class CiReviewSource(CamelContract):
    provider: str = Field(pattern=r"^[a-z][a-z0-9-]*$")
    provider_repository_id: str = Field(min_length=1)
    change_request_id: str = Field(min_length=1)


class CiReviewCommandEvidence(CamelContract):
    name: str = Field(pattern=r"^[a-z][a-z0-9-]*$")
    exit_code: int
    timed_out: bool


class CiReviewArtifactV1(CamelContract):
    """Provider-neutral review artifact consumed by publication glue."""

    schema_version: Literal["1.0"] = "1.0"
    marker: Literal[INFORMATIONAL_REVIEW_MARKER] = INFORMATIONAL_REVIEW_MARKER
    reviewed_revision_sha: GitSha
    source: CiReviewSource
    authority: Literal["informational"] = "informational"
    human_merge_required: Literal[True] = True
    serena_readiness_passed: Literal[True] = True
    deterministic_validation_passed: Literal[True] = True
    deterministic_commands: list[CiReviewCommandEvidence] = Field(min_length=1)
    changed_files: list[str]
    summary: str = Field(min_length=1)
    findings: list[CiReviewFinding]

    @field_validator("changed_files")
    @classmethod
    def validate_changed_files(cls, values: list[str]) -> list[str]:
        normalized = [require_relative_git_path(value) for value in values]
        if len(normalized) != len(set(normalized)):
            raise ValueError("changed_files cannot contain duplicates")
        return normalized
