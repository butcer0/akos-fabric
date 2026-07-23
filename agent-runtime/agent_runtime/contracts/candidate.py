"""Coder completion contract."""

from __future__ import annotations

from typing import Literal

from pydantic import Field

from ._base import StrictContract


class CriterionEvidence(StrictContract):
    criterion: str = Field(min_length=1)
    evidence: str = Field(min_length=1)
    paths: list[str] = Field(default_factory=list)


class CandidateV1(StrictContract):
    schema_version: Literal["1.0"] = "1.0"
    summary: str = Field(min_length=1)
    acceptance_criteria_evidence: list[CriterionEvidence]
    tests_added_or_changed: list[str]
    additional_commands_run: list[str]
    known_risks: list[str]
    unresolved_questions: list[str]
    ready_for_verification: bool

