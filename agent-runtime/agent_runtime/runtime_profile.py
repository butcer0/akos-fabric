"""Strict execution subset of the baked repository profile."""

from __future__ import annotations

from pathlib import Path
from typing import Literal

import yaml
from pydantic import Field, model_validator

from .contracts._base import CamelContract
from .manifest import SessionManifest


class ProfileCommand(CamelContract):
    name: str = Field(pattern=r"^[a-z][a-z0-9-]*$")
    argv: list[str] = Field(min_length=1)
    timeout_seconds: int = Field(gt=0)


class ProfileVerification(CamelContract):
    required: list[ProfileCommand] = Field(min_length=1)


class ProfileSerena(CamelContract):
    context: Literal["ide"]
    project_configuration: str = Field(min_length=1)


class ProfileSession(CamelContract):
    max_items: int = Field(gt=0)
    max_duration_minutes: int = Field(gt=0)
    continue_after_item_failure: bool


class ProfileItem(CamelContract):
    maximum_coder_conversations: int = Field(ge=1, le=2)
    maximum_model_calls_per_role: int = Field(gt=0)
    maximum_cost_usd: float = Field(gt=0)
    maximum_changed_files: int = Field(gt=0)
    maximum_diff_lines: int = Field(gt=0)


class RuntimeRepositoryProfile(CamelContract):
    """Fields consumed by the in-container harness.

    The control plane validates the complete authoritative profile schema before
    creating a manifest. The runtime validates every execution-relevant field
    again and proves its values match the immutable manifest.
    """

    schema_version: Literal[1]
    id: str = Field(pattern=r"^[a-z][a-z0-9-]*$")
    languages: list[str] = Field(min_length=1)
    serena: ProfileSerena
    session: ProfileSession
    item: ProfileItem
    bootstrap: list[ProfileCommand]
    verification: ProfileVerification

    @model_validator(mode="after")
    def command_names_are_unique(self) -> "RuntimeRepositoryProfile":
        commands = [*self.bootstrap, *self.verification.required]
        names = [command.name for command in commands]
        if len(names) != len(set(names)):
            raise ValueError("bootstrap and verification command names must be unique")
        return self

    def validate_against_manifest(self, manifest: SessionManifest) -> None:
        mismatches: list[str] = []
        checks = (
            ("repositoryProfile", self.id, manifest.repository_profile),
            ("maximumItems", self.session.max_items, manifest.limits.maximum_items),
            (
                "continueAfterItemFailure",
                self.session.continue_after_item_failure,
                manifest.session_behavior.continue_after_item_failure,
            ),
            (
                "maximumCoderConversations",
                self.item.maximum_coder_conversations,
                manifest.limits.maximum_coder_conversations,
            ),
            (
                "maximumModelCallsPerRole",
                self.item.maximum_model_calls_per_role,
                manifest.limits.maximum_model_calls_per_role,
            ),
            (
                "maximumCostUsd",
                self.item.maximum_cost_usd,
                manifest.limits.maximum_cost_usd_per_item,
            ),
            (
                "maximumChangedFiles",
                self.item.maximum_changed_files,
                manifest.limits.maximum_changed_files,
            ),
            (
                "maximumDiffLines",
                self.item.maximum_diff_lines,
                manifest.limits.maximum_diff_lines,
            ),
        )
        for name, profile_value, manifest_value in checks:
            if profile_value != manifest_value:
                mismatches.append(name)
        if self.session.max_duration_minutes * 60 < manifest.limits.session_deadline_seconds:
            mismatches.append("sessionDeadlineSeconds")
        if mismatches:
            raise ValueError(
                "profile does not match manifest: " + ", ".join(mismatches)
            )


def load_runtime_profile(path: Path) -> RuntimeRepositoryProfile:
    try:
        payload = yaml.safe_load(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, yaml.YAMLError) as error:
        raise ValueError(f"cannot read repository profile: {path}") from error
    if not isinstance(payload, dict):
        raise ValueError("repository profile must be a YAML object")
    execution_keys = (
        "schemaVersion",
        "id",
        "languages",
        "serena",
        "session",
        "item",
        "bootstrap",
        "verification",
    )
    missing = [key for key in execution_keys if key not in payload]
    if missing:
        raise ValueError(
            "repository profile lacks execution fields: " + ", ".join(missing)
        )
    return RuntimeRepositoryProfile.model_validate(
        {key: payload[key] for key in execution_keys}
    )
