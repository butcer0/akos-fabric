from __future__ import annotations

import json
import sys
import tempfile
import unittest
import urllib.request
from pathlib import Path
from typing import Any

from agent_runtime.ci_validation import run_ci_validation
from agent_runtime.contracts.review import (
    CiReviewArtifactV1,
    CiReviewCommandEvidence,
    CiReviewSource,
    INFORMATIONAL_REVIEW_MARKER,
)
from agent_runtime.github_review_glue import (
    normalize_github_context,
    publish_github_review,
)

REVISION = "97ba2f1a7b6c5d4e3f20123456789abcdef01234"
BASE_REVISION = "ef13d82a7b6c5d4e3f20123456789abcdef01234"


class CiValidationTests(unittest.IsolatedAsyncioTestCase):
    async def test_runs_profile_commands_and_writes_machine_readable_evidence(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory).resolve()
            repository = root / "repository"
            repository.mkdir()
            profile = root / "profile.yml"
            result = root / "output" / "validation.json"
            profile.write_text(_profile_yaml(), encoding="utf-8")

            passed = await run_ci_validation(
                repository=repository,
                profile_path=profile,
                result_path=result,
                log_directory=root / "logs",
            )

            self.assertTrue(passed)
            payload = json.loads(result.read_text(encoding="utf-8"))
            self.assertTrue(payload["passed"])
            self.assertEqual(["bootstrap", "tests"], [item["name"] for item in payload["commands"]])
            for command in payload["commands"]:
                self.assertTrue(Path(command["stdoutPath"]).is_file())
                self.assertTrue(Path(command["stderrPath"]).is_file())


class GitHubNormalizeTests(unittest.TestCase):
    def test_normalizes_provider_environment_into_strict_neutral_manifest(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            stdout = root / "stdout.log"
            stderr = root / "stderr.log"
            stdout.write_text("59 tests passed\n", encoding="utf-8")
            stderr.write_text("", encoding="utf-8")
            validation = root / "validation.json"
            validation.write_text(
                json.dumps(
                    {
                        "schemaVersion": "1.0",
                        "passed": True,
                        "commands": [
                            {
                                "name": "tests",
                                "exitCode": 0,
                                "timedOut": False,
                                "stdoutPath": str(stdout),
                                "stderrPath": str(stderr),
                            }
                        ],
                    }
                ),
                encoding="utf-8",
            )
            destination = root / "manifest.json"

            manifest = normalize_github_context(
                repository_id="owner/repository",
                change_request_id="482",
                revision_sha=REVISION,
                base_revision_sha=BASE_REVISION,
                requirements="Fix the stated requirement.",
                deterministic_result_path=validation,
                destination=destination,
            )

            self.assertEqual("github", manifest.source_control_provider)
            self.assertEqual("59 tests passed\n", manifest.deterministic_validation.commands[0].output.split(":\n", 1)[1])
            wire = json.loads(destination.read_text(encoding="utf-8"))
            self.assertNotIn("githubToken", wire)
            self.assertEqual(REVISION, wire["revisionSha"])


class GitHubPublisherTests(unittest.TestCase):
    def test_creates_one_marker_comment_at_exact_head(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            artifact, markdown = _write_review(root)
            opener = _Opener(
                [
                    {"head": {"sha": REVISION}},
                    [],
                    {"html_url": "https://github.com/owner/repository/pull/482#issuecomment-1"},
                ]
            )

            publication = publish_github_review(
                artifact_path=artifact,
                markdown_path=markdown,
                repository_id="owner/repository",
                change_request_id="482",
                revision_sha=REVISION,
                token="test-token",
                opener=opener,
            )

            self.assertEqual("created", publication)
            self.assertEqual(["GET", "GET", "POST"], [request.method for request in opener.requests])
            posted = json.loads(opener.requests[-1].data or b"{}")
            self.assertEqual(1, posted["body"].count(INFORMATIONAL_REVIEW_MARKER))
            self.assertIn(REVISION, posted["body"])
            self.assertNotIn("test-token", opener.requests[-1].full_url)
            self.assertNotIn(b"test-token", opener.requests[-1].data or b"")

    def test_updates_the_only_existing_marker_comment(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            artifact, markdown = _write_review(root)
            opener = _Opener(
                [
                    {"head": {"sha": REVISION}},
                    [{"id": 77, "body": INFORMATIONAL_REVIEW_MARKER + "\nold"}],
                    {"html_url": "https://github.com/comment/77"},
                ]
            )

            publication = publish_github_review(
                artifact_path=artifact,
                markdown_path=markdown,
                repository_id="owner/repository",
                change_request_id="482",
                revision_sha=REVISION,
                token="test-token",
                opener=opener,
            )

            self.assertEqual("updated", publication)
            self.assertEqual("PATCH", opener.requests[-1].method)
            self.assertTrue(opener.requests[-1].full_url.endswith("/issues/comments/77"))

    def test_refuses_changed_head_before_reading_or_writing_comments(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            artifact, markdown = _write_review(Path(temporary_directory))
            opener = _Opener([{"head": {"sha": BASE_REVISION}}])

            with self.assertRaisesRegex(RuntimeError, "no longer matches"):
                publish_github_review(
                    artifact_path=artifact,
                    markdown_path=markdown,
                    repository_id="owner/repository",
                    change_request_id="482",
                    revision_sha=REVISION,
                    token="test-token",
                    opener=opener,
                )

            self.assertEqual(1, len(opener.requests))

    def test_refuses_ambiguous_existing_marker_comments(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            artifact, markdown = _write_review(Path(temporary_directory))
            opener = _Opener(
                [
                    {"head": {"sha": REVISION}},
                    [
                        {"id": 1, "body": INFORMATIONAL_REVIEW_MARKER},
                        {"id": 2, "body": INFORMATIONAL_REVIEW_MARKER},
                    ],
                ]
            )

            with self.assertRaisesRegex(RuntimeError, "multiple"):
                publish_github_review(
                    artifact_path=artifact,
                    markdown_path=markdown,
                    repository_id="owner/repository",
                    change_request_id="482",
                    revision_sha=REVISION,
                    token="test-token",
                    opener=opener,
                )

            self.assertEqual(["GET", "GET"], [request.method for request in opener.requests])


class WorkflowContractTests(unittest.TestCase):
    def test_workflow_is_fail_closed_pinned_and_profile_aligned(self) -> None:
        repository = Path(__file__).resolve().parents[2]
        workflow = (
            repository / ".github" / "workflows" / "agent-change-request-review.yml"
        ).read_text(encoding="utf-8")

        self.assertEqual(
            2,
            workflow.count(
                "actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1"
            ),
        )
        self.assertEqual(2, workflow.count("persist-credentials: false"))
        self.assertEqual(
            3,
            workflow.count(
                "if: github.event.pull_request.head.repo.full_name == github.repository"
            ),
        )
        self.assertIn("runs-on: [self-hosted, linux, agent-ci]", workflow)
        self.assertIn("sha256:0000000000000000000000000000000000000000000000000000000000000000", workflow)
        self.assertIn("FullyQualifiedName!~PostgresLedgerTests", workflow)
        self.assertIn("uv sync --project agent-runtime --frozen --extra dev", workflow)
        self.assertIn("--extra dev --no-sync", workflow)
        self.assertIn("python -m agent_runtime.ci_review", workflow)


class _Response:
    def __init__(self, payload: Any) -> None:
        self.payload = payload
        self.status = 200

    def read(self) -> bytes:
        return json.dumps(self.payload).encode("utf-8")

    def __enter__(self) -> "_Response":
        return self

    def __exit__(self, *args: object) -> None:
        return None


class _Opener:
    def __init__(self, responses: list[Any]) -> None:
        self.responses = responses
        self.requests: list[urllib.request.Request] = []

    def open(self, request: urllib.request.Request, timeout: int) -> _Response:
        self.requests.append(request)
        if timeout != 30:
            raise AssertionError("unexpected timeout")
        if not self.responses:
            raise AssertionError("unexpected request")
        return _Response(self.responses.pop(0))


def _write_review(root: Path) -> tuple[Path, Path]:
    artifact_path = root / "review.json"
    markdown_path = root / "review.md"
    artifact = CiReviewArtifactV1(
        reviewed_revision_sha=REVISION,
        source=CiReviewSource(
            provider="github",
            provider_repository_id="owner/repository",
            change_request_id="482",
        ),
        deterministic_commands=[
            CiReviewCommandEvidence(name="tests", exit_code=0, timed_out=False)
        ],
        changed_files=["src/change.py"],
        summary="Review summary",
        findings=[],
    )
    artifact_path.write_text(
        artifact.model_dump_json(by_alias=True), encoding="utf-8"
    )
    markdown_path.write_text("## Informational review\n\nNo findings.\n", encoding="utf-8")
    return artifact_path, markdown_path


def _profile_yaml() -> str:
    executable = json.dumps(sys.executable)
    return "\n".join(
        [
            "schemaVersion: 1",
            "id: akos-fabric",
            "languages: [python]",
            "serena:",
            "  context: ide",
            "  projectConfiguration: /tmp/serena.yml",
            "session:",
            "  maxItems: 1",
            "  maxDurationMinutes: 10",
            "  continueAfterItemFailure: true",
            "item:",
            "  maximumCoderConversations: 1",
            "  maximumModelCallsPerRole: 1",
            "  maximumCostUsd: 1",
            "  maximumChangedFiles: 1",
            "  maximumDiffLines: 10",
            "bootstrap:",
            "  - name: bootstrap",
            f"    argv: [{executable}, -c, \"print('bootstrap')\"]",
            "    timeoutSeconds: 30",
            "verification:",
            "  required:",
            "    - name: tests",
            f"      argv: [{executable}, -c, \"print('tests')\"]",
            "      timeoutSeconds: 30",
        ]
    )


if __name__ == "__main__":
    unittest.main()
