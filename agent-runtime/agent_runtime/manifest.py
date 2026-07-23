"""Validated repository-session manifest."""

from __future__ import annotations

from datetime import datetime
from typing import Literal
from uuid import UUID

from pydantic import Field, JsonValue, field_validator, model_validator

from .contracts._base import CamelContract
from .contracts._validation import GitSha, JiraKey, require_absolute_uri


def _require_aware_datetime(value: datetime) -> datetime:
    if value.tzinfo is None or value.utcoffset() is None:
        raise ValueError("timestamp must include an explicit UTC offset")
    return value


class SourceControlManifest(CamelContract):
    provider: str = Field(min_length=1)
    base_url: str = Field(min_length=1)

    _base_url_is_absolute = field_validator("base_url")(require_absolute_uri)


class MainRepositoryManifest(CamelContract):
    provider_repository_id: str = Field(min_length=1)
    clone_url: str = Field(min_length=1)
    default_branch: str = Field(min_length=1)
    clone_strategy: Literal["full"]
    git_lfs: bool
    submodules: Literal["recursive", "none"]

    _clone_url_is_absolute = field_validator("clone_url")(require_absolute_uri)


class SupplementalRepositoryManifest(MainRepositoryManifest):
    writable: bool


class LlmManifest(CamelContract):
    provider: str = Field(min_length=1)
    model_id: str = Field(min_length=1)
    open_hands_model: str = Field(
        min_length=1,
        validation_alias="openHandsModel",
        serialization_alias="openHandsModel",
    )


class WorkItemManifest(CamelContract):
    work_item_run_id: UUID
    sequence_number: int = Field(ge=1)
    jira_key: JiraKey
    jira_updated_at: datetime
    jira_snapshot: dict[str, JsonValue]

    _timestamp_has_offset = field_validator("jira_updated_at")(_require_aware_datetime)


class SessionBehaviorManifest(CamelContract):
    continue_after_item_failure: bool


class SessionLimitsManifest(CamelContract):
    session_deadline_seconds: int = Field(gt=0)
    maximum_items: int = Field(gt=0)
    maximum_cost_usd_per_item: float = Field(gt=0)
    maximum_changed_files: int = Field(gt=0)
    maximum_diff_lines: int = Field(gt=0)
    maximum_coder_conversations: int = Field(ge=1, le=2)
    maximum_model_calls_per_role: int = Field(gt=0)


class SessionManifest(CamelContract):
    schema_version: Literal[1]
    repository_session_id: UUID
    repository_profile: str = Field(min_length=1)
    profile_revision_sha: GitSha
    image_digest: str = Field(pattern=r"^sha256:[0-9a-f]{64}$")
    source_control: SourceControlManifest
    main_repository: MainRepositoryManifest
    supplemental_repositories: list[SupplementalRepositoryManifest]
    llm: LlmManifest
    work_items: list[WorkItemManifest] = Field(min_length=1)
    session_behavior: SessionBehaviorManifest
    limits: SessionLimitsManifest

    @model_validator(mode="after")
    def work_items_are_bounded_and_unique(self) -> "SessionManifest":
        if len(self.work_items) > self.limits.maximum_items:
            raise ValueError("work item count exceeds limits.maximumItems")

        identifiers = [item.work_item_run_id for item in self.work_items]
        if len(identifiers) != len(set(identifiers)):
            raise ValueError("workItemRunId values must be unique")

        sequences = [item.sequence_number for item in self.work_items]
        if len(sequences) != len(set(sequences)):
            raise ValueError("sequenceNumber values must be unique")
        if sequences != sorted(sequences):
            raise ValueError("workItems must be ordered by sequenceNumber")

        jira_keys = [item.jira_key for item in self.work_items]
        if len(jira_keys) != len(set(jira_keys)):
            raise ValueError("jiraKey values must be unique")

        repository_ids = [
            self.main_repository.provider_repository_id,
            *(
                repository.provider_repository_id
                for repository in self.supplemental_repositories
            ),
        ]
        if len(repository_ids) != len(set(repository_ids)):
            raise ValueError("providerRepositoryId values must be unique")

        return self
