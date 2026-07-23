from __future__ import annotations

import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from pydantic import ValidationError

from agent_runtime.agents import (
    AgentRole,
    CompletionCapture,
    CompletionNotSubmittedError,
    DuplicateCompletionError,
    RoleAgentFactory,
    RoleConstructionRequest,
    RoleRuntimeDependencyError,
    SerenaStdioConfiguration,
)
from agent_runtime.contracts.plan import PlanV1


def _valid_plan() -> dict[str, object]:
    return {
        "schema_version": "1.0",
        "objective": "Prove typed completion",
        "source_findings": [],
        "assumptions": [],
        "files": [],
        "implementation_steps": ["Submit the result"],
        "tests_to_add_or_change": [],
        "verification": [],
        "risks": [],
        "blockers": [],
        "confidence": 1.0,
    }


class CompletionCaptureTests(unittest.TestCase):
    def test_requires_one_strict_typed_completion(self) -> None:
        capture = CompletionCapture(PlanV1)

        with self.assertRaises(CompletionNotSubmittedError):
            capture.require()
        with self.assertRaises(ValidationError):
            capture.submit({"objective": "missing required fields"})

        submitted = capture.submit(_valid_plan())
        self.assertIsInstance(submitted, PlanV1)
        self.assertEqual(submitted, capture.require())

        with self.assertRaises(DuplicateCompletionError):
            capture.submit(_valid_plan())


class RoleAgentFactoryTests(unittest.TestCase):
    def test_provider_and_runtime_are_separate_and_callback_is_metadata_only(
        self,
    ) -> None:
        provider = _FakeProvider()
        sink = _MetadataSink()
        observed: dict[str, object] = {}

        def runtime_builder(
            request: RoleConstructionRequest,
            workspace: Path,
            llm: object,
            completion: CompletionCapture[object],
            callback: object,
        ) -> tuple[object, object, object]:
            observed.update(
                request=request,
                workspace=workspace,
                llm=llm,
                completion=completion,
            )
            callback(
                SimpleNamespace(
                    source="agent",
                    timestamp="2026-07-23T12:00:00Z",
                    prompt="must-not-be-recorded",
                    tool_output="must-not-be-recorded",
                )
            )
            conversation = _FakeConversation()
            return object(), conversation, lambda: None

        with tempfile.TemporaryDirectory() as temporary_directory:
            workspace = Path(temporary_directory).resolve()
            request = _request(AgentRole.PLANNER, workspace)
            self.assertNotIn("external-test-key", repr(request))
            role = RoleAgentFactory(
                provider,
                metadata_sink=sink,
                runtime_builder=runtime_builder,
            ).create(request)

            self.assertEqual(workspace, observed["workspace"])
            self.assertIs(provider.llm, observed["llm"])
            self.assertEqual(
                {
                    "usage_id": "planner:KAN-1",
                    "model_id": "gemini-3.6-flash",
                    "openhands_model": "gemini/gemini-3.6-flash",
                    "api_key": "external-test-key",
                },
                provider.arguments,
            )
            self.assertEqual(1, len(sink.events))
            metadata_text = repr(sink.events[0])
            self.assertNotIn("must-not-be-recorded", metadata_text)
            self.assertEqual("SimpleNamespace", sink.events[0].event_type)
            role.close()
            self.assertTrue(role.conversation.closed)

    def test_rejects_serena_not_bound_to_exact_role_workspace(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            workspace = Path(temporary_directory).resolve()
            request = _request(AgentRole.CODER, workspace)
            request = RoleConstructionRequest(
                **{
                    **request.__dict__,
                    "serena": SerenaStdioConfiguration(
                        command="serena",
                        arguments=(
                            "caller-probed-subcommand",
                            "--transport",
                            "stdio",
                            "--context",
                            "ide",
                            "--project",
                            str(workspace / "other"),
                            "--enable-web-dashboard",
                            "false",
                            "--open-web-dashboard",
                            "false",
                        ),
                    ),
                }
            )

            with self.assertRaisesRegex(ValueError, "--project"):
                RoleAgentFactory(
                    _FakeProvider(),
                    runtime_builder=lambda *_: (object(), object(), lambda: None),
                ).create(request)

    def test_missing_optional_openhands_dependency_has_a_clear_error(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            workspace = Path(temporary_directory).resolve()
            missing = ModuleNotFoundError("No module named 'openhands'")
            missing.name = "openhands"

            with patch(
                "agent_runtime.agents.roles.import_module",
                side_effect=missing,
            ):
                with self.assertRaisesRegex(
                    RoleRuntimeDependencyError,
                    "pinned 'openhands' runtime extra",
                ):
                    RoleAgentFactory(_FakeProvider()).create(
                        _request(AgentRole.JUDGE, workspace)
                    )


class _FakeProvider:
    provider_name = "fake"

    def __init__(self) -> None:
        self.arguments: dict[str, object] = {}
        self.llm = object()

    def create_llm(self, **kwargs: object) -> object:
        self.arguments.update(kwargs)
        return self.llm


class _MetadataSink:
    def __init__(self) -> None:
        self.events: list[object] = []

    def record(self, metadata: object) -> None:
        self.events.append(metadata)


class _FakeConversation:
    def __init__(self) -> None:
        self.closed = False

    def close(self) -> None:
        self.closed = True


def _request(role: AgentRole, workspace: Path) -> RoleConstructionRequest:
    return RoleConstructionRequest(
        role=role,
        workspace=workspace,
        serena=SerenaStdioConfiguration(
            command="serena",
            arguments=(
                "caller-probed-subcommand",
                "--transport",
                "stdio",
                "--context",
                "ide",
                "--project",
                str(workspace),
                "--enable-web-dashboard",
                "false",
                "--open-web-dashboard",
                "false",
            ),
        ),
        usage_id=f"{role.value}:KAN-1",
        model_id="gemini-3.6-flash",
        openhands_model="gemini/gemini-3.6-flash",
        api_key="external-test-key",
    )


if __name__ == "__main__":
    unittest.main()
