from __future__ import annotations

import json
import asyncio
import subprocess
from dataclasses import replace
from datetime import datetime, timezone
from pathlib import Path
from uuid import UUID

from agent_runtime.contracts.candidate import CandidateV1
from agent_runtime.contracts.judgment import JudgmentV1
from agent_runtime.contracts.plan import PlanV1
from agent_runtime.contracts.result import RoleModelUsage, SessionResult
from agent_runtime.git_repository import GitCredential
from agent_runtime.llm.resolver import LlmProviderResolver
from agent_runtime.manifest import (
    SupplementalRepositoryManifest,
    WorkItemManifest,
)
from agent_runtime.orchestrator import (
    CoderRequest,
    JudgeRequest,
    PlannerRequest,
    RoleOutcome,
    SessionOrchestrator,
)
from agent_runtime.runtime_profile import RuntimeRepositoryProfile
from tests.support import make_manifest


class _Provider:
    provider_name = "gemini"

    def create_llm(self, **_: object) -> object:
        return object()


class _FakeRoles:
    def __init__(
        self,
        *,
        no_change_keys: set[str] | None = None,
        verification_revision: bool = False,
        judgment_revision: bool = False,
        planner_mutates: bool = False,
        judge_mutates: bool = False,
        candidate_ready: bool = True,
        coder_delay_seconds: float = 0,
        coder_delay_keys: set[str] | None = None,
    ) -> None:
        self.no_change_keys = no_change_keys or set()
        self.verification_revision = verification_revision
        self.judgment_revision = judgment_revision
        self.planner_mutates = planner_mutates
        self.judge_mutates = judge_mutates
        self.candidate_ready = candidate_ready
        self.coder_delay_seconds = coder_delay_seconds
        self.coder_delay_keys = coder_delay_keys
        self.coder_calls = 0
        self.judge_calls = 0
        self.probes: list[tuple[str, Path]] = []
        self.serena_projects: list[Path] = []

    async def prove_code_intelligence(self, *, role: str, workspace: Path) -> None:
        self.probes.append((role, workspace))
        project = workspace / ".serena" / "project.yml"
        assert project.is_file()
        self.serena_projects.append(project)

    async def run_planner(self, request: PlannerRequest) -> RoleOutcome:
        assert request.workspace.exists()
        if self.planner_mutates:
            (request.workspace / "README.md").write_text(
                "planner mutation\n", encoding="utf-8"
            )
        return RoleOutcome(
            PlanV1(
                objective="Implement the item",
                source_findings=[],
                assumptions=[],
                files=[],
                implementation_steps=["change source"],
                tests_to_add_or_change=[],
                verification=[],
                risks=[],
                blockers=[],
                confidence=1,
            ),
            _usage(),
        )

    async def run_coder(self, request: CoderRequest) -> RoleOutcome:
        self.coder_calls += 1
        if self.coder_delay_seconds and (
            self.coder_delay_keys is None
            or str(request.item.jira_key) in self.coder_delay_keys
        ):
            await asyncio.sleep(self.coder_delay_seconds)
        if str(request.item.jira_key) not in self.no_change_keys:
            target = request.workspace / f"{str(request.item.jira_key).lower()}.txt"
            if self.verification_revision and request.verification is None:
                target = request.workspace / "README.md"
                target.write_text("bad trailing whitespace   \n", encoding="utf-8")
            elif self.verification_revision and request.verification is not None:
                target = request.workspace / "README.md"
                target.write_text("verification revision\n", encoding="utf-8")
            elif request.judgment is not None:
                target.write_text("judgment revision\n", encoding="utf-8")
            else:
                target.write_text("implemented\n", encoding="utf-8")
        return RoleOutcome(
            CandidateV1(
                summary="candidate",
                acceptance_criteria_evidence=[],
                tests_added_or_changed=[],
                additional_commands_run=[],
                known_risks=[],
                unresolved_questions=[],
                ready_for_verification=self.candidate_ready,
            ),
            _usage(),
        )

    async def run_judge(self, request: JudgeRequest) -> RoleOutcome:
        self.judge_calls += 1
        if self.judge_mutates:
            (request.workspace / "README.md").write_text(
                "judge mutation\n", encoding="utf-8"
            )
        disposition = (
            "revise"
            if self.judgment_revision and self.judge_calls == 1
            else "accept"
        )
        return RoleOutcome(
            JudgmentV1(
                candidate_sha=request.candidate_sha,
                deterministic_verification_passed=True,
                acceptance_criteria_satisfied=True,
                findings=[],
                disposition=disposition,
                summary=disposition,
            ),
            _usage(),
        )


def _usage() -> RoleModelUsage:
    return RoleModelUsage(model_calls=1, estimated_cost_usd=0.01)


def _profile(
    tmp_path: Path, *, diff_check: bool = True
) -> RuntimeRepositoryProfile:
    serena_template = tmp_path / "serena-project.yml"
    serena_template.write_text(
        "project_name: test\n"
        "languages:\n"
        "  - python\n"
        "ls_specific_settings: {}\n",
        encoding="utf-8",
    )
    return RuntimeRepositoryProfile.model_validate(
        {
            "schemaVersion": 1,
            "id": "akos-fabric",
            "languages": ["python"],
            "serena": {
                "context": "ide",
                "projectConfiguration": str(serena_template.resolve()),
            },
            "session": {
                "maxItems": 5,
                "maxDurationMinutes": 240,
                "continueAfterItemFailure": True,
            },
            "item": {
                "maximumCoderConversations": 2,
                "maximumModelCallsPerRole": 60,
                "maximumCostUsd": 25,
                "maximumChangedFiles": 30,
                "maximumDiffLines": 3000,
            },
            "bootstrap": [
                {
                    "name": "bootstrap",
                    "argv": ["git", "status", "--short"],
                    "timeoutSeconds": 10,
                }
            ],
            "verification": {
                "required": [
                    {
                        "name": "diff-check",
                        "argv": (
                            ["git", "diff", "--check"]
                            if diff_check
                            else ["git", "status", "--short"]
                        ),
                        "timeoutSeconds": 10,
                    }
                ]
            },
        }
    )


def _git(cwd: Path, *args: str) -> str:
    completed = subprocess.run(
        ["git", *args],
        cwd=cwd,
        check=True,
        text=True,
        capture_output=True,
    )
    return completed.stdout.strip()


def _remote(tmp_path: Path) -> Path:
    source = tmp_path / "seed"
    source.mkdir()
    _git(source, "init", "-b", "main")
    (source / "README.md").write_text("seed\n", encoding="utf-8")
    _git(source, "add", ".")
    _git(
        source,
        "-c",
        "user.name=Test",
        "-c",
        "user.email=test@example.invalid",
        "commit",
        "-m",
        "seed",
    )
    remote = tmp_path / "remote.git"
    _git(tmp_path, "clone", "--bare", str(source), str(remote))
    return remote


def _manifest(remote: Path, count: int = 1):
    manifest = make_manifest()
    items = [
        WorkItemManifest(
            work_item_run_id=UUID(f"00000000-0000-0000-0000-{index:012d}"),
            sequence_number=index,
            jira_key=f"KAN-{index}",
            jira_updated_at=datetime(2026, 7, 23, tzinfo=timezone.utc),
            jira_snapshot={"summary": f"item {index}"},
        )
        for index in range(1, count + 1)
    ]
    return manifest.model_copy(
        update={
            "main_repository": manifest.main_repository.model_copy(
                update={"clone_url": remote.resolve().as_uri(), "git_lfs": False, "submodules": "none"}
            ),
            "work_items": items,
        }
    )


def _orchestrator(
    tmp_path: Path,
    manifest,
    roles: _FakeRoles,
    *,
    providers: LlmProviderResolver | None = None,
    profile: RuntimeRepositoryProfile | None = None,
) -> SessionOrchestrator:
    return SessionOrchestrator(
        manifest=manifest,
        profile=profile or _profile(tmp_path),
        credential=GitCredential("test", "canary-secret"),
        workspace=tmp_path / "workspace",
        llm_providers=providers or LlmProviderResolver([_Provider()]),
        role_runner_factory=lambda _: roles,
    )


def test_accepts_pushes_cleans_and_atomically_writes_result(tmp_path: Path) -> None:
    remote = _remote(tmp_path)
    manifest = _manifest(remote)
    roles = _FakeRoles()
    result_path = tmp_path / "result.json"

    result = asyncio.run(
        _orchestrator(tmp_path, manifest, roles).run_and_write(result_path)
    )

    assert result.status == "completed"
    assert result.items[0].status == "branch_pushed"
    branch = result.items[0].branch_name
    assert branch is not None
    assert _git(remote, "rev-parse", f"refs/heads/{branch}") == result.items[0].candidate_commit_sha
    assert not (tmp_path / "workspace" / "items" / "KAN-1" / "coding").exists()
    assert not list(tmp_path.glob(".result.json.*.tmp"))
    persisted = SessionResult.model_validate_json(result_path.read_bytes())
    assert persisted == result
    assert [role for role, _ in roles.probes] == ["planner", "coder", "judge"]


def test_failed_item_continues_to_next_item(tmp_path: Path) -> None:
    manifest = _manifest(_remote(tmp_path), count=2)
    roles = _FakeRoles(no_change_keys={"KAN-1"})

    result = asyncio.run(_orchestrator(tmp_path, manifest, roles).run())

    assert [(item.status, item.failure_code) for item in result.items] == [
        ("blocked", "NO_SOURCE_CHANGE"),
        ("branch_pushed", None),
    ]


def test_allows_only_one_verification_revision(tmp_path: Path) -> None:
    manifest = _manifest(_remote(tmp_path))
    roles = _FakeRoles(verification_revision=True)

    result = asyncio.run(_orchestrator(tmp_path, manifest, roles).run())

    assert result.items[0].status == "branch_pushed"
    assert roles.coder_calls == 2
    assert roles.judge_calls == 1


def test_allows_only_one_judgment_revision_and_rejudges_new_sha(tmp_path: Path) -> None:
    manifest = _manifest(_remote(tmp_path))
    roles = _FakeRoles(judgment_revision=True)

    result = asyncio.run(
        _orchestrator(
            tmp_path,
            manifest,
            roles,
            profile=_profile(tmp_path, diff_check=False),
        ).run()
    )

    assert result.items[0].status == "branch_pushed"
    assert roles.coder_calls == 2
    assert roles.judge_calls == 2


def test_unknown_provider_is_session_fatal_before_clone_or_roles(tmp_path: Path) -> None:
    manifest = _manifest(_remote(tmp_path))
    roles = _FakeRoles()
    result = asyncio.run(
        _orchestrator(
            tmp_path,
            manifest,
            roles,
            providers=LlmProviderResolver([]),
        ).run()
    )

    assert result.status == "failed"
    assert result.failure_code == "UNKNOWN_LLM_PROVIDER"
    assert result.items[0].failure_code == "NOT_PROCESSED_SESSION_FATAL"
    assert not (tmp_path / "workspace" / "repository.git").exists()
    assert roles.probes == []


def test_main_clone_failure_is_session_fatal(tmp_path: Path) -> None:
    missing = tmp_path / "missing.git"
    manifest = _manifest(missing)
    roles = _FakeRoles()

    result = asyncio.run(_orchestrator(tmp_path, manifest, roles).run())

    assert result.status == "failed"
    assert result.failure_code == "REPOSITORY_CLONE_FAILED"
    assert result.items[0].failure_code == "NOT_PROCESSED_SESSION_FATAL"
    assert roles.probes == []


def test_planner_source_mutation_is_rejected(tmp_path: Path) -> None:
    manifest = _manifest(_remote(tmp_path))
    result = asyncio.run(
        _orchestrator(
            tmp_path, manifest, _FakeRoles(planner_mutates=True)
        ).run()
    )

    assert result.items[0].status == "failed"
    assert result.items[0].failure_code == "PLANNER_MODIFIED_SOURCE"


def test_candidate_not_ready_blocks_before_verification_or_commit(
    tmp_path: Path,
) -> None:
    remote = _remote(tmp_path)
    manifest = _manifest(remote)
    result = asyncio.run(
        _orchestrator(
            tmp_path, manifest, _FakeRoles(candidate_ready=False)
        ).run()
    )

    assert result.items[0].status == "blocked"
    assert result.items[0].failure_code == "CANDIDATE_NOT_READY"
    assert "agent/" not in _git(remote, "for-each-ref", "--format=%(refname)", "refs/heads")


def test_judge_source_mutation_is_rejected(tmp_path: Path) -> None:
    manifest = _manifest(_remote(tmp_path))
    result = asyncio.run(
        _orchestrator(
            tmp_path, manifest, _FakeRoles(judge_mutates=True)
        ).run()
    )

    assert result.items[0].status == "failed"
    assert result.items[0].failure_code == "JUDGE_MODIFIED_SOURCE"


def test_session_deadline_preserves_completed_item_results(tmp_path: Path) -> None:
    manifest = _manifest(_remote(tmp_path), count=2)
    manifest = manifest.model_copy(
        update={
            "limits": manifest.limits.model_copy(
                update={"session_deadline_seconds": 10}
            )
        }
    )
    roles = _FakeRoles(
        coder_delay_seconds=15,
        coder_delay_keys={"KAN-2"},
    )

    result = asyncio.run(_orchestrator(tmp_path, manifest, roles).run())

    assert result.status == "failed"
    assert result.failure_code == "SESSION_DEADLINE_EXCEEDED"
    assert result.items[0].status == "branch_pushed"
    assert result.items[1].failure_code == "NOT_PROCESSED_SESSION_FATAL"


def test_reloads_mounted_credential_for_each_git_operation(
    tmp_path: Path,
) -> None:
    manifest = _manifest(_remote(tmp_path))
    loads: list[int] = []

    def load_credential() -> GitCredential:
        loads.append(len(loads) + 1)
        return GitCredential("test", f"credential-{len(loads)}")

    result = asyncio.run(
        SessionOrchestrator(
            manifest=manifest,
            profile=_profile(tmp_path),
            credential_loader=load_credential,
            workspace=tmp_path / "workspace",
            llm_providers=LlmProviderResolver([_Provider()]),
            role_runner_factory=lambda _: _FakeRoles(),
        ).run()
    )

    assert result.items[0].status == "branch_pushed"
    assert len(loads) >= 3


def test_clones_full_supplemental_checkout_without_push(tmp_path: Path) -> None:
    main_root = tmp_path / "main"
    supplemental_root = tmp_path / "supplemental-source"
    main_root.mkdir()
    supplemental_root.mkdir()
    main_remote = _remote(main_root)
    supplemental_remote = _remote(supplemental_root)
    manifest = _manifest(main_remote).model_copy(
        update={
            "supplemental_repositories": [
                SupplementalRepositoryManifest(
                    provider_repository_id="example/supporting",
                    clone_url=supplemental_remote.resolve().as_uri(),
                    default_branch="main",
                    clone_strategy="full",
                    git_lfs=False,
                    submodules="none",
                    writable=False,
                )
            ]
        }
    )

    result = asyncio.run(
        _orchestrator(tmp_path, manifest, _FakeRoles()).run()
    )

    checkout = tmp_path / "workspace" / "supplemental" / "0"
    assert result.items[0].status == "branch_pushed"
    assert (checkout / ".git").exists()
    assert _git(checkout, "rev-parse", "--is-shallow-repository") == "false"
