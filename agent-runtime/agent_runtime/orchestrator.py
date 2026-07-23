"""Ordinary, bounded repository-session application orchestration."""

from __future__ import annotations

import asyncio
import time
from collections.abc import Callable
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Protocol, TypeVar

from .contracts.candidate import CandidateV1
from .contracts.judgment import JudgmentV1
from .contracts.plan import PlanV1
from .contracts.result import (
    ModelUsage,
    ResultLlm,
    ResultRepository,
    RoleModelUsage,
    SessionResult,
    VerificationResult,
    WorkItemResult,
)
from .git_repository import (
    GitCredential,
    GitRepositoryError,
    GitRepositoryService,
)
from .llm.interface import ILlmProvider
from .llm.resolver import LlmProviderResolver, UnknownLlmProviderError
from .manifest import SessionManifest, WorkItemManifest
from .process_runner import DeterministicProcessRunner, IProcessRunner
from .result_writer import write_result_atomic
from .runtime_profile import RuntimeRepositoryProfile
from .serena_project import materialize_serena_project
from .telemetry import runtime_telemetry
from .verification import run_commands

RoleValue = TypeVar("RoleValue", PlanV1, CandidateV1, JudgmentV1)


@dataclass(frozen=True)
class RoleOutcome:
    value: PlanV1 | CandidateV1 | JudgmentV1
    usage: RoleModelUsage


@dataclass(frozen=True)
class PlannerRequest:
    item: WorkItemManifest
    base_sha: str
    workspace: Path
    profile: RuntimeRepositoryProfile


@dataclass(frozen=True)
class CoderRequest:
    item: WorkItemManifest
    base_sha: str
    workspace: Path
    profile: RuntimeRepositoryProfile
    plan: PlanV1
    prior_candidate: CandidateV1 | None = None
    verification: VerificationResult | None = None
    judgment: JudgmentV1 | None = None


@dataclass(frozen=True)
class JudgeRequest:
    item: WorkItemManifest
    workspace: Path
    profile: RuntimeRepositoryProfile
    plan: PlanV1
    candidate: CandidateV1
    verification: VerificationResult
    candidate_sha: str


class IRoleRunner(Protocol):
    async def prove_code_intelligence(self, *, role: str, workspace: Path) -> None:
        """Perform one non-mutating semantic operation or raise."""

    async def run_planner(self, request: PlannerRequest) -> RoleOutcome:
        ...

    async def run_coder(self, request: CoderRequest) -> RoleOutcome:
        ...

    async def run_judge(self, request: JudgeRequest) -> RoleOutcome:
        ...


RoleRunnerFactory = Callable[[ILlmProvider], IRoleRunner]
ProcessRunnerFactory = Callable[[Path, Path], IProcessRunner]
Clock = Callable[[], datetime]
CredentialLoader = Callable[[], GitCredential]


class SessionFatalError(RuntimeError):
    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code


class ItemFailure(RuntimeError):
    def __init__(self, code: str, message: str, *, blocked: bool = False) -> None:
        super().__init__(message)
        self.code = code
        self.blocked = blocked


class _Usage:
    def __init__(self, manifest: SessionManifest) -> None:
        self._manifest = manifest
        self.planner = RoleModelUsage()
        self.coder = RoleModelUsage()
        self.judge = RoleModelUsage()

    def add(self, role: str, usage: RoleModelUsage) -> None:
        prior = getattr(self, role)
        combined = RoleModelUsage(
            input_tokens=prior.input_tokens + usage.input_tokens,
            output_tokens=prior.output_tokens + usage.output_tokens,
            model_calls=prior.model_calls + usage.model_calls,
            estimated_cost_usd=(
                prior.estimated_cost_usd + usage.estimated_cost_usd
            ),
        )
        setattr(self, role, combined)
        if combined.model_calls > self._manifest.limits.maximum_model_calls_per_role:
            raise ItemFailure(
                "MODEL_BUDGET_EXCEEDED",
                f"{role} exceeded maximumModelCallsPerRole",
                blocked=True,
            )
        if self.total_cost > self._manifest.limits.maximum_cost_usd_per_item:
            raise ItemFailure(
                "MODEL_BUDGET_EXCEEDED",
                "item exceeded maximumCostUsdPerItem",
                blocked=True,
            )

    @property
    def total_cost(self) -> float:
        return sum(
            usage.estimated_cost_usd
            for usage in (self.planner, self.coder, self.judge)
        )

    def result(self) -> ModelUsage:
        return ModelUsage(
            provider=self._manifest.llm.provider,
            model_id=self._manifest.llm.model_id,
            planner=self.planner,
            coder=self.coder,
            judge=self.judge,
            total_estimated_cost_usd=self.total_cost,
        )


def _default_runner(worktree: Path, logs: Path) -> IProcessRunner:
    return DeterministicProcessRunner(
        active_worktree=worktree,
        log_directory=logs,
    )


class SessionOrchestrator:
    def __init__(
        self,
        *,
        manifest: SessionManifest,
        profile: RuntimeRepositoryProfile,
        credential: GitCredential | None = None,
        credential_loader: CredentialLoader | None = None,
        workspace: Path,
        llm_providers: LlmProviderResolver,
        role_runner_factory: RoleRunnerFactory,
        process_runner_factory: ProcessRunnerFactory = _default_runner,
        clock: Clock | None = None,
    ) -> None:
        profile.validate_against_manifest(manifest)
        if (credential is None) == (credential_loader is None):
            raise ValueError(
                "exactly one of credential or credential_loader must be supplied"
            )
        self._manifest = manifest
        self._profile = profile
        self._credential_loader = (
            credential_loader
            if credential_loader is not None
            else lambda: credential  # type: ignore[return-value]
        )
        self._workspace = workspace.resolve()
        self._llm_providers = llm_providers
        self._role_runner_factory = role_runner_factory
        self._process_runner_factory = process_runner_factory
        self._clock = clock or (lambda: datetime.now(timezone.utc))

    async def run(self) -> SessionResult:
        started = self._clock()
        monotonic_started = time.monotonic()
        items: list[WorkItemResult] = []
        with runtime_telemetry.span(
            "repository_session.run",
            {
                "source_control.provider": self._manifest.source_control.provider,
                "gen_ai.provider.name": self._manifest.llm.provider,
                "gen_ai.request.model": self._manifest.llm.model_id,
            },
            remote_parent_from_environment=True,
        ):
            try:
                async with asyncio.timeout(
                    self._manifest.limits.session_deadline_seconds
                ):
                    result = await self._run_core(started, items)
            except TimeoutError:
                result = self._fatal_result(
                    started,
                    items,
                    "SESSION_DEADLINE_EXCEEDED",
                    "repository session reached its configured deadline",
                )
        runtime_telemetry.record_session(
            result.status, time.monotonic() - monotonic_started
        )
        return result

    async def _run_core(
        self,
        started: datetime,
        items: list[WorkItemResult],
    ) -> SessionResult:
        try:
            # Provider resolution is intentionally before repository or role work.
            provider = self._llm_providers.resolve(self._manifest.llm.provider)
            try:
                role_runner = self._role_runner_factory(provider)
            except Exception as error:
                raise SessionFatalError(
                    "MODEL_CONFIGURATION_INVALID",
                    "model role runtime could not be configured",
                ) from error
            self._workspace.mkdir(parents=True, exist_ok=True)
            session_runner = self._process_runner_factory(
                self._workspace, self._workspace / "logs" / "git"
            )
            git = GitRepositoryService(
                session_root=self._workspace,
                runner=session_runner,
            )
            bare = self._workspace / "repository.git"
            try:
                await git.clone_bare(
                    self._manifest.main_repository.clone_url,
                    bare,
                    self._load_credential,
                )
                await self._clone_supplementals(git)
            except (GitRepositoryError, FileNotFoundError) as error:
                raise SessionFatalError(
                    "REPOSITORY_CLONE_FAILED", "required repository clone failed"
                ) from error
            git.exclude_runtime_path(bare, ".serena/")

            for index, item in enumerate(self._manifest.work_items):
                item_started = time.monotonic()
                with runtime_telemetry.span("work_item.run") as item_span:
                    result = await self._run_item(
                        item=item,
                        git=git,
                        bare=bare,
                        role_runner=role_runner,
                    )
                    item_span.set(
                        status=result.status,
                        failure_code=result.failure_code or "",
                    )
                runtime_telemetry.record_item(
                    result.status, time.monotonic() - item_started
                )
                self._record_item_model_metrics(result)
                if result.judgment is not None:
                    runtime_telemetry.record_judge_disposition(
                        result.judgment.disposition
                    )
                items.append(result)
                if (
                    result.status != "branch_pushed"
                    and not self._manifest.session_behavior.continue_after_item_failure
                ):
                    for remaining in self._manifest.work_items[index + 1 :]:
                        items.append(
                            self._failed_item(
                                remaining,
                                code="NOT_PROCESSED_AFTER_ITEM_FAILURE",
                                message="session policy stopped after an item failure",
                            )
                        )
                    break
            return self._session_result(started, items, status="completed")
        except UnknownLlmProviderError as error:
            return self._fatal_result(
                started, items, "UNKNOWN_LLM_PROVIDER", str(error)
            )
        except SessionFatalError as error:
            return self._fatal_result(started, items, error.code, str(error))
        except Exception:
            return self._fatal_result(
                started,
                items,
                "SESSION_SETUP_FAILED",
                "unexpected session-level setup failure",
            )

    async def _run_item(
        self,
        *,
        item: WorkItemManifest,
        git: GitRepositoryService,
        bare: Path,
        role_runner: IRoleRunner,
    ) -> WorkItemResult:
        try:
            return await self._process_item(
                item=item,
                git=git,
                bare=bare,
                role_runner=role_runner,
            )
        except SessionFatalError:
            raise
        except FileNotFoundError as error:
            raise SessionFatalError(
                "REQUIRED_TOOLCHAIN_MISSING",
                "image lacks an executable required by the repository profile",
            ) from error
        except ItemFailure as error:
            return self._failed_item(
                item,
                code=error.code,
                message=str(error),
                status="blocked" if error.blocked else "failed",
            )
        except Exception as error:
            return self._failed_item(
                item,
                code="ITEM_EXECUTION_FAILED",
                message=type(error).__name__,
            )

    async def run_and_write(self, result_path: Path) -> SessionResult:
        result = await self.run()
        result.validate_against_manifest(self._manifest)
        expected = {item.work_item_run_id for item in self._manifest.work_items}
        actual = {item.work_item_run_id for item in result.items}
        if actual != expected:
            raise ValueError("result does not contain exactly one terminal item per manifest item")
        write_result_atomic(result, result_path)
        return result

    async def _clone_supplementals(self, git: GitRepositoryService) -> None:
        root = self._workspace / "supplemental"
        for index, repository in enumerate(self._manifest.supplemental_repositories):
            root.mkdir(parents=True, exist_ok=True)
            await git.clone_checkout(
                repository.clone_url,
                root / str(index),
                repository.default_branch,
                git_lfs=repository.git_lfs,
                submodules=repository.submodules,
                credential=self._load_credential,
            )

    def _load_credential(self) -> GitCredential:
        try:
            credential = self._credential_loader()
        except Exception as error:
            raise SessionFatalError(
                "SOURCE_CONTROL_CREDENTIAL_UNAVAILABLE",
                "mounted source-control credential could not be reloaded",
            ) from error
        if not isinstance(credential, GitCredential):
            raise SessionFatalError(
                "SOURCE_CONTROL_CREDENTIAL_UNAVAILABLE",
                "mounted source-control credential has an invalid contract",
            )
        return credential

    async def _process_item(
        self,
        *,
        item: WorkItemManifest,
        git: GitRepositoryService,
        bare: Path,
        role_runner: IRoleRunner,
    ) -> WorkItemResult:
        usage = _Usage(self._manifest)
        base_sha: str | None = None
        branch = f"agent/{item.jira_key.lower()}/{str(item.work_item_run_id)[:8]}"
        item_root = self._workspace / "items" / str(item.jira_key)
        coding = item_root / "coding"
        judge: Path | None = None
        plan: PlanV1 | None = None
        candidate: CandidateV1 | None = None
        verification: VerificationResult | None = None
        changed_files: tuple[str, ...] = ()
        candidate_sha: str | None = None
        judgment: JudgmentV1 | None = None
        try:
            base_sha = await git.fetch_default_branch(
                bare,
                self._manifest.main_repository.default_branch,
                self._load_credential,
            )
            item_root.mkdir(parents=True, exist_ok=True)
            await git.create_coding_worktree(bare, coding, branch, base_sha)
            materialize_serena_project(
                self._profile,
                coding,
                project_suffix=f"{item.jira_key}-coding",
            )
            runner = self._process_runner_factory(
                coding, item_root / "logs" / "commands"
            )
            bootstrap = await run_commands(
                self._profile.bootstrap,
                worktree=coding,
                runner=runner,
                record_as_verification=False,
            )
            if not bootstrap.passed:
                raise ItemFailure("BOOTSTRAP_FAILED", "bootstrap command failed")
            planner_baseline = await git.changed_files(coding)

            await self._prove_serena(role_runner, "planner", coding)
            planner_outcome = await role_runner.run_planner(
                PlannerRequest(item, base_sha, coding, self._profile)
            )
            if not isinstance(planner_outcome.value, PlanV1):
                raise ItemFailure("PLANNING_FAILED", "planner returned wrong contract")
            plan = planner_outcome.value
            usage.add("planner", planner_outcome.usage)
            if await git.changed_files(coding) != planner_baseline:
                raise ItemFailure(
                    "PLANNER_MODIFIED_SOURCE",
                    "planner changed the coding worktree",
                )

            await self._prove_serena(role_runner, "coder", coding)
            coder_outcome = await role_runner.run_coder(
                CoderRequest(item, base_sha, coding, self._profile, plan)
            )
            if not isinstance(coder_outcome.value, CandidateV1):
                raise ItemFailure("CODING_FAILED", "coder returned wrong contract")
            candidate = coder_outcome.value
            usage.add("coder", coder_outcome.usage)
            if not candidate.ready_for_verification:
                raise ItemFailure(
                    "CANDIDATE_NOT_READY",
                    "coder declared the candidate not ready for verification",
                    blocked=True,
                )
            coder_conversations = 1
            verification = await run_commands(
                self._profile.verification.required,
                worktree=coding,
                runner=runner,
            )
            if not verification.passed:
                if coder_conversations >= self._manifest.limits.maximum_coder_conversations:
                    raise ItemFailure(
                        "REQUIRED_VERIFICATION_FAILED",
                        "required verification failed and no revision is available",
                        blocked=True,
                    )
                await self._prove_serena(role_runner, "coder", coding)
                revision = await role_runner.run_coder(
                    CoderRequest(
                        item,
                        base_sha,
                        coding,
                        self._profile,
                        plan,
                        prior_candidate=candidate,
                        verification=verification,
                    )
                )
                if not isinstance(revision.value, CandidateV1):
                    raise ItemFailure("CODING_FAILED", "coder returned wrong contract")
                candidate = revision.value
                usage.add("coder", revision.usage)
                if not candidate.ready_for_verification:
                    raise ItemFailure(
                        "CANDIDATE_NOT_READY",
                        "coder revision is not ready for verification",
                        blocked=True,
                    )
                coder_conversations += 1
                verification = await run_commands(
                    self._profile.verification.required,
                    worktree=coding,
                    runner=runner,
                )
            if not verification.passed:
                raise ItemFailure(
                    "REQUIRED_VERIFICATION_FAILED",
                    "required verification failed after the allowed revision",
                    blocked=True,
                )

            commit = await self._commit(git, coding, item)
            changed_files = commit.changed_files
            candidate_sha = str(commit.sha)
            judgment, judge = await self._judge(
                git, bare, item_root, item, plan, candidate, verification,
                candidate_sha, role_runner, usage,
            )
            if judgment.disposition == "revise":
                await git.remove_worktree(bare, judge)
                judge = None
                if coder_conversations >= self._manifest.limits.maximum_coder_conversations:
                    raise ItemFailure(
                        "CODER_CONVERSATION_LIMIT_REACHED",
                        "judge requested revision after coder conversation limit",
                        blocked=True,
                    )
                await self._prove_serena(role_runner, "coder", coding)
                revision = await role_runner.run_coder(
                    CoderRequest(
                        item,
                        base_sha,
                        coding,
                        self._profile,
                        plan,
                        prior_candidate=candidate,
                        judgment=judgment,
                    )
                )
                if not isinstance(revision.value, CandidateV1):
                    raise ItemFailure("CODING_FAILED", "coder returned wrong contract")
                candidate = revision.value
                usage.add("coder", revision.usage)
                if not candidate.ready_for_verification:
                    raise ItemFailure(
                        "CANDIDATE_NOT_READY",
                        "coder revision is not ready for verification",
                        blocked=True,
                    )
                verification = await run_commands(
                    self._profile.verification.required,
                    worktree=coding,
                    runner=runner,
                )
                if not verification.passed:
                    raise ItemFailure(
                        "REQUIRED_VERIFICATION_FAILED",
                        "required verification failed after judgment revision",
                        blocked=True,
                    )
                commit = await self._commit(git, coding, item)
                changed_files = commit.changed_files
                candidate_sha = str(commit.sha)
                judgment, judge = await self._judge(
                    git, bare, item_root, item, plan, candidate, verification,
                    candidate_sha, role_runner, usage,
                )

            if judgment.candidate_sha != candidate_sha:
                raise ItemFailure(
                    "INVALID_JUDGMENT_SHA",
                    "judgment candidate SHA does not match the harness candidate",
                    blocked=True,
                )
            if judgment.disposition != "accept":
                raise ItemFailure(
                    "JUDGE_BLOCKED",
                    f"judge disposition was {judgment.disposition}",
                    blocked=True,
                )
            if (
                len(changed_files) > self._manifest.limits.maximum_changed_files
                or commit.diff_lines > self._manifest.limits.maximum_diff_lines
            ):
                raise ItemFailure(
                    "CHANGE_LIMIT_EXCEEDED",
                    "harness-derived change size exceeds configured limits",
                    blocked=True,
                )
            try:
                await git.push_branch(bare, branch, self._load_credential)
            except GitRepositoryError as error:
                raise ItemFailure("GIT_PUSH_FAILED", "candidate branch push failed") from error

            return WorkItemResult(
                work_item_run_id=item.work_item_run_id,
                jira_key=item.jira_key,
                status="branch_pushed",
                base_commit_sha=base_sha,
                branch_name=branch,
                candidate_commit_sha=candidate_sha,
                changed_files=list(changed_files),
                plan=plan,
                candidate=candidate,
                verification=verification,
                judgment=judgment,
                model_usage=usage.result(),
            )
        except GitRepositoryError as error:
            code = "NO_SOURCE_CHANGE" if str(error) == "NO_SOURCE_CHANGE" else "GIT_OPERATION_FAILED"
            failure = ItemFailure(
                code, str(error), blocked=(code == "NO_SOURCE_CHANGE")
            )
            return await self._partial_failed_item(
                item=item,
                failure=failure,
                usage=usage,
                git=git,
                coding=coding,
                base_sha=base_sha,
                branch=branch,
                plan=plan,
                candidate=candidate,
                verification=verification,
                changed_files=changed_files,
                candidate_sha=candidate_sha,
                judgment=judgment,
            )
        except ItemFailure as failure:
            return await self._partial_failed_item(
                item=item,
                failure=failure,
                usage=usage,
                git=git,
                coding=coding,
                base_sha=base_sha,
                branch=branch,
                plan=plan,
                candidate=candidate,
                verification=verification,
                changed_files=changed_files,
                candidate_sha=candidate_sha,
                judgment=judgment,
            )
        finally:
            if judge is not None and judge.exists():
                await git.remove_worktree(bare, judge)
            if coding.exists():
                await git.remove_worktree(bare, coding)

    async def _commit(
        self, git: GitRepositoryService, coding: Path, item: WorkItemManifest
    ):
        summary = str(item.jira_snapshot.get("summary", "agent change")).strip()
        return await git.commit_all(coding, f"{item.jira_key}: {summary}")

    async def _judge(
        self,
        git: GitRepositoryService,
        bare: Path,
        item_root: Path,
        item: WorkItemManifest,
        plan: PlanV1,
        candidate: CandidateV1,
        verification: VerificationResult,
        candidate_sha: str,
        role_runner: IRoleRunner,
        usage: _Usage,
    ) -> tuple[JudgmentV1, Path]:
        judge = item_root / f"judge-{candidate_sha[:8]}"
        await git.create_judge_worktree(bare, judge, candidate_sha)
        materialize_serena_project(
            self._profile,
            judge,
            project_suffix=f"{item.jira_key}-judge-{candidate_sha[:8]}",
        )
        await self._prove_serena(role_runner, "judge", judge)
        outcome = await role_runner.run_judge(
            JudgeRequest(
                item, judge, self._profile, plan, candidate, verification, candidate_sha
            )
        )
        if not isinstance(outcome.value, JudgmentV1):
            raise ItemFailure("JUDGMENT_FAILED", "judge returned wrong contract")
        usage.add("judge", outcome.usage)
        if await git.changed_files(judge):
            raise ItemFailure(
                "JUDGE_MODIFIED_SOURCE",
                "judge changed the detached review worktree",
            )
        return outcome.value, judge

    @staticmethod
    async def _prove_serena(
        role_runner: IRoleRunner, role: str, workspace: Path
    ) -> None:
        for attempt in range(2):
            try:
                await role_runner.prove_code_intelligence(
                    role=role, workspace=workspace
                )
                return
            except Exception as error:
                if attempt == 1:
                    raise ItemFailure(
                        "CODE_INTELLIGENCE_UNAVAILABLE",
                        f"{role} Serena readiness failed after one restart",
                    ) from error

    async def _partial_failed_item(
        self,
        *,
        item: WorkItemManifest,
        failure: ItemFailure,
        usage: _Usage,
        git: GitRepositoryService,
        coding: Path,
        base_sha: str | None,
        branch: str,
        plan: PlanV1 | None,
        candidate: CandidateV1 | None,
        verification: VerificationResult | None,
        changed_files: tuple[str, ...],
        candidate_sha: str | None,
        judgment: JudgmentV1 | None,
    ) -> WorkItemResult:
        derived_files = changed_files
        if not derived_files and coding.exists():
            try:
                derived_files = await git.changed_files(coding)
            except (GitRepositoryError, OSError, UnicodeError):
                derived_files = ()
        return WorkItemResult(
            work_item_run_id=item.work_item_run_id,
            jira_key=item.jira_key,
            status="blocked" if failure.blocked else "failed",
            base_commit_sha=base_sha,
            branch_name=branch if base_sha is not None else None,
            candidate_commit_sha=candidate_sha,
            changed_files=list(derived_files),
            plan=plan,
            candidate=candidate,
            verification=verification,
            judgment=judgment,
            model_usage=usage.result(),
            failure_code=failure.code,
            failure_message=str(failure),
        )

    def _record_item_model_metrics(self, result: WorkItemResult) -> None:
        usage = result.model_usage
        for role, role_usage in (
            ("planner", usage.planner),
            ("coder", usage.coder),
            ("judge", usage.judge),
        ):
            runtime_telemetry.record_model_usage(
                provider=usage.provider,
                model=usage.model_id,
                role=role,
                requests=role_usage.model_calls,
                input_tokens=role_usage.input_tokens,
                output_tokens=role_usage.output_tokens,
                cost_usd=role_usage.estimated_cost_usd,
            )

    def _failed_item(
        self,
        item: WorkItemManifest,
        *,
        code: str,
        message: str,
        status: str = "failed",
    ) -> WorkItemResult:
        return WorkItemResult(
            work_item_run_id=item.work_item_run_id,
            jira_key=item.jira_key,
            status=status,
            changed_files=[],
            model_usage=ModelUsage(
                provider=self._manifest.llm.provider,
                model_id=self._manifest.llm.model_id,
                planner=RoleModelUsage(),
                coder=RoleModelUsage(),
                judge=RoleModelUsage(),
                total_estimated_cost_usd=0,
            ),
            failure_code=code,
            failure_message=message,
        )

    def _fatal_result(
        self,
        started: datetime,
        prior_items: list[WorkItemResult],
        code: str,
        message: str,
    ) -> SessionResult:
        seen = {item.work_item_run_id for item in prior_items}
        terminal = list(prior_items)
        for item in self._manifest.work_items:
            if item.work_item_run_id not in seen:
                terminal.append(
                    self._failed_item(
                        item,
                        code="NOT_PROCESSED_SESSION_FATAL",
                        message="item was not processed because the session failed",
                    )
                )
        return self._session_result(
            started,
            terminal,
            status="failed",
            failure_code=code,
            failure_message=message,
        )

    def _session_result(
        self,
        started: datetime,
        items: list[WorkItemResult],
        *,
        status: str,
        failure_code: str | None = None,
        failure_message: str | None = None,
    ) -> SessionResult:
        return SessionResult(
            schema_version=1,
            repository_session_id=self._manifest.repository_session_id,
            status=status,
            started_at=started,
            completed_at=self._clock(),
            failure_code=failure_code,
            failure_message=failure_message,
            repository=ResultRepository(
                provider=self._manifest.source_control.provider,
                provider_repository_id=(
                    self._manifest.main_repository.provider_repository_id
                ),
                clone_url=self._manifest.main_repository.clone_url,
            ),
            llm=ResultLlm(
                provider=self._manifest.llm.provider,
                model_id=self._manifest.llm.model_id,
            ),
            items=items,
        )
