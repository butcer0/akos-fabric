from __future__ import annotations

from datetime import datetime, timezone
from uuid import UUID

from agent_runtime.manifest import (
    LlmManifest,
    MainRepositoryManifest,
    SessionBehaviorManifest,
    SessionLimitsManifest,
    SessionManifest,
    SourceControlManifest,
    WorkItemManifest,
)

SESSION_ID = UUID("6a92a62a-1e93-4b5b-a52c-dcc541fb591c")
ITEM_ID = UUID("5e8a8ae4-65b2-4db8-aa62-949121cbd5f3")
PROFILE_SHA = "9f6e4f8a7b6c5d4e3f20123456789abcdef01234"
BASE_SHA = "ef13d82a7b6c5d4e3f20123456789abcdef01234"
CANDIDATE_SHA = "97ba2f1a7b6c5d4e3f20123456789abcdef01234"


def make_manifest() -> SessionManifest:
    return SessionManifest(
        schema_version=1,
        repository_session_id=SESSION_ID,
        repository_profile="akos-fabric",
        profile_revision_sha=PROFILE_SHA,
        image_digest="sha256:" + ("a" * 64),
        source_control=SourceControlManifest(
            provider="github", base_url="https://github.com"
        ),
        main_repository=MainRepositoryManifest(
            provider_repository_id="butcer0/akos-fabric",
            clone_url="https://github.com/butcer0/akos-fabric.git",
            default_branch="main",
            clone_strategy="full",
            git_lfs=True,
            submodules="recursive",
        ),
        supplemental_repositories=[],
        llm=LlmManifest(
            provider="gemini",
            model_id="gemini-3.6-flash",
            open_hands_model="gemini/gemini-3.6-flash",
        ),
        work_items=[
            WorkItemManifest(
                work_item_run_id=ITEM_ID,
                sequence_number=1,
                jira_key="KAN-1",
                jira_updated_at=datetime(2026, 7, 23, tzinfo=timezone.utc),
                jira_snapshot={"summary": "Implement a deterministic runtime"},
            )
        ],
        session_behavior=SessionBehaviorManifest(
            continue_after_item_failure=True
        ),
        limits=SessionLimitsManifest(
            session_deadline_seconds=14_400,
            maximum_items=5,
            maximum_cost_usd_per_item=25.0,
            maximum_changed_files=30,
            maximum_diff_lines=3_000,
            maximum_coder_conversations=2,
            maximum_model_calls_per_role=60,
        ),
    )
