"""Judge completion contract."""

from __future__ import annotations

from typing import Literal

from pydantic import Field, model_validator

from ._base import StrictContract
from ._validation import GitSha

FindingSeverity = Literal["blocking", "major", "minor", "informational"]
FindingCategory = Literal[
    "correctness",
    "acceptance_criteria",
    "testing",
    "maintainability",
    "performance",
    "security",
    "standards",
]
JudgmentDisposition = Literal["accept", "revise", "blocked"]


class JudgeFinding(StrictContract):
    severity: FindingSeverity
    category: FindingCategory
    path: str | None = None
    line: int | None = Field(default=None, ge=1)
    explanation: str = Field(min_length=1)
    required_change: str | None = None


class JudgmentV1(StrictContract):
    schema_version: Literal["1.0"] = "1.0"
    candidate_sha: GitSha
    deterministic_verification_passed: bool
    acceptance_criteria_satisfied: bool
    findings: list[JudgeFinding]
    disposition: JudgmentDisposition
    summary: str = Field(min_length=1)

    @model_validator(mode="after")
    def deterministic_rules_allow_disposition(self) -> "JudgmentV1":
        if self.disposition == "accept" and not self.deterministic_verification_passed:
            raise ValueError(
                "disposition cannot be accept when deterministic verification failed"
            )
        if self.disposition == "accept" and not self.acceptance_criteria_satisfied:
            raise ValueError(
                "disposition cannot be accept when acceptance criteria are unsatisfied"
            )
        return self
