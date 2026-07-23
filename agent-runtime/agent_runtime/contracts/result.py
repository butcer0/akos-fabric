"""Container-to-control-plane repository-session result contract."""

from __future__ import annotations

from datetime import datetime
from typing import Literal
from uuid import UUID

from pydantic import Field, field_validator, model_validator

from agent_runtime.manifest import SessionManifest, _require_aware_datetime

from ._base import CamelContract
from ._validation import (
    GitSha,
    JiraKey,
    require_absolute_uri,
    require_relative_git_path,
)
from .candidate import CandidateV1
from .judgment import JudgmentV1
from .plan import PlanV1

SessionResultStatus = Literal["completed", "failed"]
WorkItemResultStatus = Literal["branch_pushed", "blocked", "failed"]


class ResultRepository(CamelContract):
    provider: str = Field(min_length=1)
    provider_repository_id: str = Field(min_length=1)
    clone_url: str = Field(min_length=1)

    _clone_url_is_absolute = field_validator("clone_url")(require_absolute_uri)


class ResultLlm(CamelContract):
    provider: str = Field(min_length=1)
    model_id: str = Field(min_length=1)


class CommandVerificationResult(CamelContract):
    name: str = Field(min_length=1)
    argv: list[str] = Field(min_length=1)
    exit_code: int
    duration_ms: int = Field(ge=0)
    timed_out: bool
    stdout_path: str
    stderr_path: str


class VerificationResult(CamelContract):
    passed: bool
    commands: list[CommandVerificationResult]

    @model_validator(mode="after")
    def passed_matches_command_results(self) -> "VerificationResult":
        derived = all(
            command.exit_code == 0 and not command.timed_out
            for command in self.commands
        )
        if self.passed != derived:
            raise ValueError(
                "passed must equal the deterministic outcome of all commands"
            )
        return self


class RoleModelUsage(CamelContract):
    input_tokens: int = Field(default=0, ge=0)
    output_tokens: int = Field(default=0, ge=0)
    model_calls: int = Field(default=0, ge=0)
    estimated_cost_usd: float = Field(default=0.0, ge=0)


class ModelUsage(CamelContract):
    provider: str = Field(min_length=1)
    model_id: str = Field(min_length=1)
    planner: RoleModelUsage
    coder: RoleModelUsage
    judge: RoleModelUsage
    total_estimated_cost_usd: float = Field(ge=0)

    @model_validator(mode="after")
    def total_is_not_less_than_roles(self) -> "ModelUsage":
        role_total = sum(
            role.estimated_cost_usd
            for role in (self.planner, self.coder, self.judge)
        )
        if self.total_estimated_cost_usd + 1e-9 < role_total:
            raise ValueError("totalEstimatedCostUsd is less than the role total")
        return self


class WorkItemResult(CamelContract):
    work_item_run_id: UUID
    jira_key: JiraKey
    status: WorkItemResultStatus
    base_commit_sha: GitSha | None = None
    branch_name: str | None = Field(default=None, min_length=1)
    candidate_commit_sha: GitSha | None = None
    changed_files: list[str]
    plan: PlanV1 | None = None
    candidate: CandidateV1 | None = None
    verification: VerificationResult | None = None
    judgment: JudgmentV1 | None = None
    model_usage: ModelUsage
    failure_code: str | None = None
    failure_message: str | None = None

    @field_validator("changed_files")
    @classmethod
    def changed_file_paths_are_relative_and_unique(
        cls, value: list[str]
    ) -> list[str]:
        validated = [require_relative_git_path(path) for path in value]
        if len(validated) != len(set(validated)):
            raise ValueError("changedFiles must contain unique paths")
        return validated

    @model_validator(mode="after")
    def pushed_branch_is_proven(self) -> "WorkItemResult":
        if self.status != "branch_pushed":
            return self

        missing = [
            name
            for name, value in (
                ("baseCommitSha", self.base_commit_sha),
                ("branchName", self.branch_name),
                ("candidateCommitSha", self.candidate_commit_sha),
                ("plan", self.plan),
                ("candidate", self.candidate),
                ("verification", self.verification),
                ("judgment", self.judgment),
            )
            if value is None
        ]
        if missing:
            raise ValueError(
                "branch_pushed item lacks required evidence: " + ", ".join(missing)
            )
        assert self.verification is not None
        assert self.judgment is not None
        assert self.candidate_commit_sha is not None
        if not self.verification.passed:
            raise ValueError("branch_pushed item requires passing verification")
        if self.judgment.candidate_sha != self.candidate_commit_sha:
            raise ValueError("judgment candidate SHA does not match candidateCommitSha")
        if self.judgment.disposition != "accept":
            raise ValueError("branch_pushed item requires an accepted judgment")
        return self


class SessionResult(CamelContract):
    schema_version: Literal[1]
    repository_session_id: UUID
    status: SessionResultStatus
    started_at: datetime
    completed_at: datetime
    failure_code: str | None = None
    failure_message: str | None = None
    repository: ResultRepository
    llm: ResultLlm
    items: list[WorkItemResult]

    _started_has_offset = field_validator("started_at")(_require_aware_datetime)
    _completed_has_offset = field_validator("completed_at")(_require_aware_datetime)

    @model_validator(mode="after")
    def completion_is_not_before_start(self) -> "SessionResult":
        if self.completed_at < self.started_at:
            raise ValueError("completedAt cannot precede startedAt")
        identifiers = [item.work_item_run_id for item in self.items]
        if len(identifiers) != len(set(identifiers)):
            raise ValueError("result contains duplicate workItemRunId values")
        return self

    def validate_against_manifest(self, manifest: SessionManifest) -> None:
        """Enforce the cross-document controls required by Section 25."""

        errors: list[str] = []
        if self.repository_session_id != manifest.repository_session_id:
            errors.append("repositorySessionId does not match manifest")
        if self.repository.provider != manifest.source_control.provider:
            errors.append("repository provider does not match manifest")
        if (
            self.repository.provider_repository_id
            != manifest.main_repository.provider_repository_id
        ):
            errors.append("providerRepositoryId does not match manifest")
        if self.repository.clone_url != manifest.main_repository.clone_url:
            errors.append("cloneUrl does not match manifest")
        if self.llm.provider != manifest.llm.provider:
            errors.append("LLM provider does not match manifest")
        if self.llm.model_id != manifest.llm.model_id:
            errors.append("LLM modelId does not match manifest")

        manifest_items = {
            item.work_item_run_id: item.jira_key for item in manifest.work_items
        }
        result_ids = {item.work_item_run_id for item in self.items}
        missing_ids = set(manifest_items) - result_ids
        if missing_ids:
            errors.append(
                "result lacks terminal items: "
                + ", ".join(str(identifier) for identifier in sorted(missing_ids))
            )
        for item in self.items:
            expected_jira_key = manifest_items.get(item.work_item_run_id)
            if expected_jira_key is None:
                errors.append(
                    f"unknown workItemRunId in result: {item.work_item_run_id}"
                )
                continue
            if item.jira_key != expected_jira_key:
                errors.append(
                    f"jiraKey mismatch for work item {item.work_item_run_id}"
                )
            if item.model_usage.provider != manifest.llm.provider:
                errors.append(
                    f"modelUsage provider mismatch for {item.work_item_run_id}"
                )
            if item.model_usage.model_id != manifest.llm.model_id:
                errors.append(
                    f"modelUsage modelId mismatch for {item.work_item_run_id}"
                )
            if len(item.changed_files) > manifest.limits.maximum_changed_files:
                errors.append(
                    f"changedFiles exceeds manifest limit for {item.work_item_run_id}"
                )

        if errors:
            raise ValueError("; ".join(errors))
