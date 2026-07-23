"""Planner completion contract."""

from __future__ import annotations

from typing import Literal

from pydantic import Field

from ._base import StrictContract


class PlannedFile(StrictContract):
    path: str = Field(min_length=1)
    purpose: str = Field(min_length=1)
    symbols: list[str] = Field(default_factory=list)


class VerificationIntent(StrictContract):
    name: str = Field(min_length=1)
    rationale: str = Field(min_length=1)


class PlanV1(StrictContract):
    schema_version: Literal["1.0"] = "1.0"
    objective: str = Field(min_length=1)
    source_findings: list[str]
    assumptions: list[str]
    files: list[PlannedFile]
    implementation_steps: list[str]
    tests_to_add_or_change: list[str]
    verification: list[VerificationIntent]
    risks: list[str]
    blockers: list[str]
    confidence: float = Field(ge=0.0, le=1.0)

