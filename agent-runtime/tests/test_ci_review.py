from __future__ import annotations

import json
import subprocess
import tempfile
import unittest
from pathlib import Path

from pydantic import ValidationError

from agent_runtime.ci_review import (
    CiDeterministicCommand,
    CiDeterministicValidation,
    CiReviewManifestV1,
    CiReviewRequest,
    load_ci_review_manifest,
    load_ci_review_profile,
    run_ci_review,
)
from agent_runtime.contracts.review import (
    CiReviewFinding,
    INFORMATIONAL_REVIEW_MARKER,
    ReviewV1,
)


class _Reviewer:
    def __init__(
        self,
        *,
        revision_override: str | None = None,
        mutate: bool = False,
    ) -> None:
        self.revision_override = revision_override
        self.mutate = mutate
        self.requests: list[CiReviewRequest] = []

    async def review(self, request: CiReviewRequest) -> ReviewV1:
        self.requests.append(request)
        if self.mutate:
            (request.repository / "src" / "changed.py").write_text(
                "print('mutated')\n", encoding="utf-8"
            )
        return ReviewV1(
            reviewed_revision_sha=self.revision_override or request.revision_sha,
            summary="One actionable issue was found.",
            findings=[
                CiReviewFinding(
                    severity="major",
                    category="correctness",
                    path="src/changed.py",
                    line=1,
                    explanation="The changed value violates the requirement.",
                    suggested_change="Return the required value.",
                )
            ],
        )


class CiReviewContractTests(unittest.TestCase):
    def test_manifest_rejects_contradictory_deterministic_evidence(self) -> None:
        with self.assertRaisesRegex(ValidationError, "contradicts"):
            CiDeterministicValidation(
                passed=True,
                commands=[
                    CiDeterministicCommand(
                        name="tests",
                        exit_code=1,
                        timed_out=False,
                    )
                ],
            )

    def test_review_rejects_absolute_finding_path(self) -> None:
        with self.assertRaises(ValidationError):
            CiReviewFinding(
                severity="major",
                category="security",
                path="/etc/passwd",
                explanation="Invalid path",
            )

    def test_manifest_loader_is_strict_and_provider_neutral(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            path = Path(temporary_directory) / "manifest.json"
            payload = _manifest_payload("a" * 40)
            payload["githubRepository"] = "must-not-be-accepted"
            path.write_text(json.dumps(payload), encoding="utf-8")

            with self.assertRaisesRegex(ValueError, "manifest is invalid"):
                load_ci_review_manifest(path)

    def test_profile_loader_uses_baked_llm_and_serena_fields(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            path = Path(temporary_directory) / "profile.yml"
            path.write_text(
                "\n".join(
                    [
                        "schemaVersion: 1",
                        "id: akos-fabric",
                        "llm:",
                        "  provider: gemini",
                        "  modelId: gemini-3.6-flash",
                        "  openHandsModel: gemini/gemini-3.6-flash",
                        "serena:",
                        "  context: ide",
                        "  projectConfiguration: /opt/repository-profile/serena-project.yml",
                    ]
                ),
                encoding="utf-8",
            )

            profile = load_ci_review_profile(path)

            self.assertEqual("gemini", profile.llm.provider)
            self.assertEqual("ide", profile.serena.context)


class CiReviewHarnessTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self) -> None:
        self.temporary_directory = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary_directory.name).resolve()
        self.repository = self.root / "repository"
        self.repository.mkdir()
        _git(self.repository, "init")
        _git(self.repository, "config", "user.name", "CI Test")
        _git(self.repository, "config", "user.email", "ci@example.invalid")
        (self.repository / "AGENTS.md").write_text(
            "Do not fabricate review evidence.\n", encoding="utf-8"
        )
        source = self.repository / "src" / "changed.py"
        source.parent.mkdir()
        source.write_text("VALUE = 1\n", encoding="utf-8")
        _git(self.repository, "add", ".")
        _git(self.repository, "commit", "-m", "base")
        self.base_sha = _git(self.repository, "rev-parse", "HEAD").strip()
        source.write_text("VALUE = 2\n", encoding="utf-8")
        _git(self.repository, "add", ".")
        _git(self.repository, "commit", "-m", "candidate")
        self.revision_sha = _git(self.repository, "rev-parse", "HEAD").strip()
        self.output = self.root / "output"
        self.result_path = self.output / "review.json"
        self.markdown_path = self.output / "review.md"
        self.logs = self.root / "logs"

    async def asyncTearDown(self) -> None:
        self.temporary_directory.cleanup()

    async def test_emits_atomic_provider_neutral_artifacts_for_exact_head(self) -> None:
        reviewer = _Reviewer()

        artifact = await self._run(reviewer)

        self.assertEqual(self.revision_sha, artifact.reviewed_revision_sha)
        self.assertEqual(["src/changed.py"], artifact.changed_files)
        self.assertEqual("informational", artifact.authority)
        self.assertTrue(artifact.human_merge_required)
        self.assertTrue(artifact.serena_readiness_passed)
        self.assertEqual(1, len(reviewer.requests))
        request = reviewer.requests[0]
        self.assertEqual("Fix the changed value.", request.requirements)
        self.assertEqual("AGENTS.md", request.repository_instructions[0][0])
        self.assertEqual(
            ["Do not fabricate review evidence."],
            request.repository_instructions[0][1].splitlines(),
        )

        wire = json.loads(self.result_path.read_text(encoding="utf-8"))
        self.assertEqual(INFORMATIONAL_REVIEW_MARKER, wire["marker"])
        self.assertEqual(self.revision_sha, wire["reviewedRevisionSha"])
        self.assertEqual("github", wire["source"]["provider"])
        markdown = self.markdown_path.read_text(encoding="utf-8")
        self.assertIn(f"Reviewed revision: `{self.revision_sha}`", markdown)
        self.assertIn("human merge authority is required", markdown)
        self.assertNotIn(INFORMATIONAL_REVIEW_MARKER, markdown)
        self.assertEqual([], list(self.output.glob("*.tmp")))

    async def test_rejects_checkout_at_a_different_revision_before_model(self) -> None:
        reviewer = _Reviewer()
        manifest = self._manifest(revision_sha=self.base_sha)

        with self.assertRaisesRegex(ValueError, "HEAD does not match"):
            await run_ci_review(
                repository=self.repository,
                revision_sha=self.base_sha,
                result_path=self.result_path,
                markdown_path=self.markdown_path,
                manifest=manifest,
                reviewer=reviewer,
                log_directory=self.logs,
            )

        self.assertEqual([], reviewer.requests)
        self.assertFalse(self.result_path.exists())

    async def test_rejects_model_review_for_a_different_revision(self) -> None:
        reviewer = _Reviewer(revision_override=self.base_sha)

        with self.assertRaisesRegex(ValueError, "completion revision"):
            await self._run(reviewer)

        self.assertFalse(self.result_path.exists())
        self.assertFalse(self.markdown_path.exists())

    async def test_rejects_any_reviewer_checkout_mutation(self) -> None:
        reviewer = _Reviewer(mutate=True)

        with self.assertRaisesRegex(RuntimeError, "modified"):
            await self._run(reviewer)

        self.assertFalse(self.result_path.exists())
        self.assertFalse(self.markdown_path.exists())

    async def test_rejects_failed_deterministic_validation_without_model(self) -> None:
        reviewer = _Reviewer()
        manifest = self._manifest(
            validation=CiDeterministicValidation(
                passed=False,
                commands=[
                    CiDeterministicCommand(
                        name="tests", exit_code=1, timed_out=False, output="failed"
                    )
                ],
            )
        )

        with self.assertRaisesRegex(ValueError, "successful deterministic"):
            await run_ci_review(
                repository=self.repository,
                revision_sha=self.revision_sha,
                result_path=self.result_path,
                markdown_path=self.markdown_path,
                manifest=manifest,
                reviewer=reviewer,
                log_directory=self.logs,
            )

        self.assertEqual([], reviewer.requests)

    async def test_rejects_output_inside_checkout(self) -> None:
        with self.assertRaisesRegex(ValueError, "outside"):
            await run_ci_review(
                repository=self.repository,
                revision_sha=self.revision_sha,
                result_path=self.repository / "review.json",
                markdown_path=self.markdown_path,
                manifest=self._manifest(),
                reviewer=_Reviewer(),
                log_directory=self.logs,
            )

    async def _run(self, reviewer: _Reviewer):
        return await run_ci_review(
            repository=self.repository,
            revision_sha=self.revision_sha,
            result_path=self.result_path,
            markdown_path=self.markdown_path,
            manifest=self._manifest(),
            reviewer=reviewer,
            log_directory=self.logs,
        )

    def _manifest(
        self,
        *,
        revision_sha: str | None = None,
        validation: CiDeterministicValidation | None = None,
    ) -> CiReviewManifestV1:
        return CiReviewManifestV1(
            source_control_provider="github",
            provider_repository_id="butcer0/akos-fabric",
            change_request_id="482",
            revision_sha=revision_sha or self.revision_sha,
            base_revision_sha=self.base_sha,
            requirements="Fix the changed value.",
            deterministic_validation=validation
            or CiDeterministicValidation(
                passed=True,
                commands=[
                    CiDeterministicCommand(
                        name="tests", exit_code=0, timed_out=False, output="passed"
                    )
                ],
            ),
        )


def _manifest_payload(revision_sha: str) -> dict[str, object]:
    return {
        "schemaVersion": "1.0",
        "sourceControlProvider": "github",
        "providerRepositoryId": "owner/repository",
        "changeRequestId": "1",
        "revisionSha": revision_sha,
        "requirements": "Review requirements.",
        "deterministicValidation": {
            "passed": True,
            "commands": [{"name": "tests", "exitCode": 0, "timedOut": False}],
        },
    }


def _git(repository: Path, *arguments: str) -> str:
    completed = subprocess.run(
        ["git", *arguments],
        cwd=repository,
        check=True,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )
    return completed.stdout


if __name__ == "__main__":
    unittest.main()
