from __future__ import annotations

import json
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path
from uuid import UUID

from pydantic import ValidationError

from agent_runtime.contracts.judgment import JudgmentV1
from agent_runtime.contracts.candidate import CandidateV1
from agent_runtime.contracts.plan import PlanV1
from agent_runtime.contracts.result import (
    CommandVerificationResult,
    ModelUsage,
    ResultLlm,
    ResultRepository,
    RoleModelUsage,
    SessionResult,
    VerificationResult,
    WorkItemResult,
)
from agent_runtime.result_writer import write_result_atomic
from tests.support import (
    BASE_SHA,
    CANDIDATE_SHA,
    ITEM_ID,
    SESSION_ID,
    make_manifest,
)


def model_usage() -> ModelUsage:
    return ModelUsage(
        provider="gemini",
        model_id="gemini-3.6-flash",
        planner=RoleModelUsage(),
        coder=RoleModelUsage(),
        judge=RoleModelUsage(),
        total_estimated_cost_usd=0.0,
    )


def plan() -> PlanV1:
    return PlanV1(
        objective="Implement the requested behavior",
        source_findings=[],
        assumptions=[],
        files=[],
        implementation_steps=[],
        tests_to_add_or_change=[],
        verification=[],
        risks=[],
        blockers=[],
        confidence=1.0,
    )


def candidate() -> CandidateV1:
    return CandidateV1(
        summary="Implemented the requested behavior",
        acceptance_criteria_evidence=[],
        tests_added_or_changed=[],
        additional_commands_run=[],
        known_risks=[],
        unresolved_questions=[],
        ready_for_verification=True,
    )


def passing_verification() -> VerificationResult:
    return VerificationResult(
        passed=True,
        commands=[
            CommandVerificationResult(
                name="test",
                argv=["python", "-m", "unittest"],
                exit_code=0,
                duration_ms=10,
                timed_out=False,
                stdout_path="logs/test.stdout.log",
                stderr_path="logs/test.stderr.log",
            )
        ],
    )


def make_result(*, item_id: UUID = ITEM_ID) -> SessionResult:
    started = datetime(2026, 7, 23, 12, 35, tzinfo=timezone.utc)
    return SessionResult(
        schema_version=1,
        repository_session_id=SESSION_ID,
        status="completed",
        started_at=started,
        completed_at=started + timedelta(minutes=2),
        repository=ResultRepository(
            provider="github",
            provider_repository_id="butcer0/akos-fabric",
            clone_url="https://github.com/butcer0/akos-fabric.git",
        ),
        llm=ResultLlm(provider="gemini", model_id="gemini-3.6-flash"),
        items=[
            WorkItemResult(
                work_item_run_id=item_id,
                jira_key="KAN-1",
                status="branch_pushed",
                base_commit_sha=BASE_SHA,
                branch_name="agent/kan-1/5e8a8ae4",
                candidate_commit_sha=CANDIDATE_SHA,
                changed_files=["agent_runtime/example.py"],
                plan=plan(),
                candidate=candidate(),
                verification=passing_verification(),
                judgment=JudgmentV1(
                    candidate_sha=CANDIDATE_SHA,
                    deterministic_verification_passed=True,
                    acceptance_criteria_satisfied=True,
                    findings=[],
                    disposition="accept",
                    summary="Accepted",
                ),
                model_usage=model_usage(),
            )
        ],
    )


class ResultContractTests(unittest.TestCase):
    def test_pushed_branch_requires_exact_judgment_sha(self) -> None:
        with self.assertRaisesRegex(ValidationError, "does not match"):
            WorkItemResult(
                work_item_run_id=ITEM_ID,
                jira_key="KAN-1",
                status="branch_pushed",
                base_commit_sha=BASE_SHA,
                branch_name="agent/kan-1/5e8a8ae4",
                candidate_commit_sha=CANDIDATE_SHA,
                changed_files=[],
                plan=plan(),
                candidate=candidate(),
                verification=passing_verification(),
                judgment=JudgmentV1(
                    candidate_sha="a" * 40,
                    deterministic_verification_passed=True,
                    acceptance_criteria_satisfied=True,
                    findings=[],
                    disposition="accept",
                    summary="Accepted",
                ),
                model_usage=model_usage(),
            )

    def test_verification_pass_is_derived_from_all_commands(self) -> None:
        with self.assertRaisesRegex(ValidationError, "deterministic outcome"):
            VerificationResult(
                passed=True,
                commands=[
                    CommandVerificationResult(
                        name="test",
                        argv=["false"],
                        exit_code=1,
                        duration_ms=1,
                        timed_out=False,
                        stdout_path="stdout",
                        stderr_path="stderr",
                    )
                ],
            )

    def test_control_validation_matches_manifest(self) -> None:
        result = make_result()

        result.validate_against_manifest(make_manifest())

        unknown = make_result(
            item_id=UUID("11111111-1111-4111-8111-111111111111")
        )
        with self.assertRaisesRegex(ValueError, "unknown workItemRunId"):
            unknown.validate_against_manifest(make_manifest())

    def test_control_validation_rejects_item_model_usage_mismatch(self) -> None:
        payload = make_result().model_dump()
        payload["items"][0]["model_usage"]["provider"] = "other"
        result = SessionResult.model_validate(payload)

        with self.assertRaisesRegex(ValueError, "modelUsage provider mismatch"):
            result.validate_against_manifest(make_manifest())

    def test_atomic_writer_publishes_camel_case_json(self) -> None:
        result = make_result()
        with tempfile.TemporaryDirectory() as temporary:
            destination = Path(temporary) / "result.json"

            write_result_atomic(result, destination)

            payload = json.loads(destination.read_text(encoding="utf-8"))
            self.assertEqual(str(SESSION_ID), payload["repositorySessionId"])
            self.assertEqual("branch_pushed", payload["items"][0]["status"])
            self.assertEqual(
                CANDIDATE_SHA,
                payload["items"][0]["judgment"]["candidate_sha"],
            )
            self.assertEqual([], list(destination.parent.glob("*.tmp")))

    def test_rejects_cancelled_session_status(self) -> None:
        payload = make_result().model_dump()
        payload["status"] = "cancelled"

        with self.assertRaises(ValidationError):
            SessionResult.model_validate(payload)

    def test_model_usage_is_required_for_every_item_status(self) -> None:
        for status in ("blocked", "failed"):
            with self.subTest(status=status):
                with self.assertRaisesRegex(ValidationError, "modelUsage"):
                    WorkItemResult(
                        work_item_run_id=ITEM_ID,
                        jira_key="KAN-1",
                        status=status,
                        changed_files=[],
                    )

    def test_pushed_branch_requires_all_structured_evidence(self) -> None:
        common = {
            "work_item_run_id": ITEM_ID,
            "jira_key": "KAN-1",
            "status": "branch_pushed",
            "base_commit_sha": BASE_SHA,
            "branch_name": "agent/kan-1/5e8a8ae4",
            "candidate_commit_sha": CANDIDATE_SHA,
            "changed_files": [],
            "plan": plan(),
            "candidate": candidate(),
            "verification": passing_verification(),
            "judgment": JudgmentV1(
                candidate_sha=CANDIDATE_SHA,
                deterministic_verification_passed=True,
                acceptance_criteria_satisfied=True,
                findings=[],
                disposition="accept",
                summary="Accepted",
            ),
            "model_usage": model_usage(),
        }
        for omitted in ("plan", "candidate", "verification", "judgment"):
            with self.subTest(omitted=omitted):
                payload = dict(common)
                payload[omitted] = None
                with self.assertRaisesRegex(ValidationError, omitted):
                    WorkItemResult(**payload)

        empty_branch = dict(common)
        empty_branch["branch_name"] = ""
        with self.assertRaises(ValidationError):
            WorkItemResult(**empty_branch)

    def test_rejects_invalid_or_duplicate_changed_file_paths(self) -> None:
        invalid_sets = (
            ["/absolute/file.py"],
            ["../outside.py"],
            ["folder/../outside.py"],
            [r"folder\windows.py"],
            ["same.py", "same.py"],
        )
        for changed_files in invalid_sets:
            with self.subTest(changed_files=changed_files):
                with self.assertRaises(ValidationError):
                    WorkItemResult(
                        work_item_run_id=ITEM_ID,
                        jira_key="KAN-1",
                        status="blocked",
                        changed_files=changed_files,
                        model_usage=model_usage(),
                    )

    def test_rejects_short_or_uppercase_result_shas(self) -> None:
        for invalid_sha in ("abc123", "A" * 40):
            with self.subTest(invalid_sha=invalid_sha):
                with self.assertRaises(ValidationError):
                    WorkItemResult(
                        work_item_run_id=ITEM_ID,
                        jira_key="KAN-1",
                        status="blocked",
                        base_commit_sha=invalid_sha,
                        changed_files=[],
                        model_usage=model_usage(),
                    )

                with self.assertRaises(ValidationError):
                    JudgmentV1(
                        candidate_sha=invalid_sha,
                        deterministic_verification_passed=False,
                        acceptance_criteria_satisfied=False,
                        findings=[],
                        disposition="blocked",
                        summary="Invalid SHA must not cross the boundary",
                    )


if __name__ == "__main__":
    unittest.main()
