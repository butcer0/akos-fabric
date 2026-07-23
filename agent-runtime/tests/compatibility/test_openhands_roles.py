"""Executable compatibility tests for pinned OpenHands 1.36.1 role APIs."""

from __future__ import annotations

from importlib.metadata import version
from pathlib import Path
import sys

import pytest
from pydantic import ValidationError


pytest.importorskip("openhands.sdk")

from openhands.sdk import LLM  # noqa: E402
from openhands.sdk.tool.registry import resolve_tool  # noqa: E402

from agent_runtime.agents import (  # noqa: E402
    AgentRole,
    DuplicateCompletionError,
    RoleAgentFactory,
    RoleConstructionRequest,
    SerenaStdioConfiguration,
)
from agent_runtime.contracts.candidate import CandidateV1  # noqa: E402
from agent_runtime.contracts.judgment import JudgmentV1  # noqa: E402
from agent_runtime.contracts.plan import PlanV1  # noqa: E402
from agent_runtime.contracts.review import ReviewV1  # noqa: E402
from agent_runtime.llm.gemini import GeminiLlmProvider  # noqa: E402
from tests.support import CANDIDATE_SHA  # noqa: E402


def test_pinned_openhands_versions_are_aligned() -> None:
    assert version("openhands-sdk") == "1.36.1"
    assert version("openhands-tools") == "1.36.1"


def test_actual_agents_have_only_explicit_tools_and_serena_mcp(
    tmp_path: Path,
) -> None:
    role = _create_role(AgentRole.PLANNER, tmp_path)
    try:
        assert [tool.name for tool in role.agent.tools] == [
            "terminal",
            "file_editor",
            "submit_plan",
        ]
        assert role.agent.include_default_tools == []
        assert set(role.agent.mcp_config) == {"serena"}
        serena = role.agent.mcp_config["serena"]
        assert serena.transport == "stdio"
        assert serena.cwd == str(tmp_path.resolve())
        assert serena.args == _serena_args(tmp_path.resolve())
        assert "browser" not in repr(role.agent.tools).lower()
        assert "delegate" not in repr(role.agent.tools).lower()
        assert "company" not in repr(role.agent.mcp_config).lower()
    finally:
        role.close()


def test_gemini_provider_constructs_pinned_llm_without_sampling_overrides(
    tmp_path: Path,
) -> None:
    role = RoleAgentFactory(GeminiLlmProvider()).create(
        _request(AgentRole.JUDGE, tmp_path)
    )
    try:
        assert role.agent.llm.model == "gemini/gemini-3.6-flash"
        assert role.agent.llm.usage_id == "judge:compatibility"
        assert role.agent.llm.temperature is None
        assert role.agent.llm.top_p is None
        assert role.agent.llm.top_k is None
        assert role.agent.llm.num_retries == 1
    finally:
        role.close()


@pytest.mark.parametrize(
    ("role_name", "tool_name", "field_name", "contract_type", "payload"),
    [
        (
            AgentRole.PLANNER,
            "submit_plan",
            "plan",
            PlanV1,
            {
                "schema_version": "1.0",
                "objective": "Construct the role boundary",
                "source_findings": [],
                "assumptions": [],
                "files": [],
                "implementation_steps": ["Use the typed tool"],
                "tests_to_add_or_change": [],
                "verification": [],
                "risks": [],
                "blockers": [],
                "confidence": 1.0,
            },
        ),
        (
            AgentRole.CODER,
            "submit_candidate",
            "candidate",
            CandidateV1,
            {
                "schema_version": "1.0",
                "summary": "Implemented the role boundary",
                "acceptance_criteria_evidence": [],
                "tests_added_or_changed": [],
                "additional_commands_run": [],
                "known_risks": [],
                "unresolved_questions": [],
                "ready_for_verification": True,
            },
        ),
        (
            AgentRole.JUDGE,
            "submit_judgment",
            "judgment",
            JudgmentV1,
            {
                "schema_version": "1.0",
                "candidate_sha": CANDIDATE_SHA,
                "deterministic_verification_passed": True,
                "acceptance_criteria_satisfied": True,
                "findings": [],
                "disposition": "accept",
                "summary": "The candidate is acceptable",
            },
        ),
        (
            AgentRole.CI_REVIEW,
            "submit_review",
            "review",
            ReviewV1,
            {
                "schema_version": "1.0",
                "reviewed_revision_sha": CANDIDATE_SHA,
                "summary": "The informational review found no issues",
                "findings": [],
            },
        ),
    ],
)
def test_typed_completion_tools_reject_malformed_and_duplicate_input(
    tmp_path: Path,
    role_name: AgentRole,
    tool_name: str,
    field_name: str,
    contract_type: type[object],
    payload: dict[str, object],
) -> None:
    role = _create_role(role_name, tmp_path)
    try:
        spec = next(tool for tool in role.agent.tools if tool.name == tool_name)
        tool = resolve_tool(spec, role.conversation.state)[0]

        with pytest.raises(ValidationError):
            tool.action_from_arguments({field_name: {"schema_version": "1.0"}})

        action = tool.action_from_arguments({field_name: payload})
        observation = tool(action, role.conversation)
        assert observation.is_error is False
        assert isinstance(role.completion.require(), contract_type)

        with pytest.raises(DuplicateCompletionError):
            tool(action, role.conversation)
    finally:
        role.close()


def test_terminal_and_file_editor_execute_in_explicit_workspace_without_llm(
    tmp_path: Path,
) -> None:
    role = _create_role(AgentRole.CODER, tmp_path)
    resolved_tools = []
    try:
        terminal_spec = next(
            tool for tool in role.agent.tools if tool.name == "terminal"
        )
        file_spec = next(
            tool for tool in role.agent.tools if tool.name == "file_editor"
        )
        terminal = resolve_tool(terminal_spec, role.conversation.state)[0]
        file_editor = resolve_tool(file_spec, role.conversation.state)[0]
        resolved_tools.extend((terminal, file_editor))

        terminal_action = terminal.action_from_arguments(
            {"command": "pwd", "timeout": 5}
        )
        terminal_result = terminal(terminal_action, role.conversation)
        assert str(tmp_path.resolve()) in terminal_result.text

        proof_file = tmp_path / "openhands-tool-proof.txt"
        create_action = file_editor.action_from_arguments(
            {
                "command": "create",
                "path": str(proof_file),
                "file_text": "created by the explicit file editor tool\n",
            }
        )
        create_result = file_editor(create_action, role.conversation)
        assert create_result.is_error is False
        assert proof_file.read_text(encoding="utf-8") == (
            "created by the explicit file editor tool\n"
        )
    finally:
        for tool in resolved_tools:
            if tool.executor is not None:
                tool.executor.close()
        role.close()


def test_role_conversations_have_independent_ids_and_histories(
    tmp_path: Path,
) -> None:
    event_metadata = _ListMetadataSink()
    factory = RoleAgentFactory(
        _NoNetworkLlmProvider(),
        metadata_sink=event_metadata,
    )
    roles = []
    try:
        for role_name in AgentRole:
            workspace = tmp_path / role_name.value
            workspace.mkdir()
            roles.append(factory.create(_request(role_name, workspace)))

        assert len({role.conversation.id for role in roles}) == 4
        roles[0].conversation.send_message("planner-private-transcript")
        planner_events = list(roles[0].conversation.state.events)
        assert len(planner_events) == 2
        assert list(roles[1].conversation.state.events) == []
        assert list(roles[2].conversation.state.events) == []
        assert "get_symbols_overview" in roles[0].conversation.state.agent.tools_map
        assert "planner-private-transcript" not in repr(event_metadata.events)
    finally:
        for role in roles:
            role.close()


class _NoNetworkLlmProvider:
    provider_name = "fake"

    def create_llm(self, **kwargs: object) -> LLM:
        return LLM(
            model="openai/no-network-fake",
            api_key="not-a-real-key",
            usage_id=str(kwargs["usage_id"]),
            num_retries=0,
        )


class _ListMetadataSink:
    def __init__(self) -> None:
        self.events: list[object] = []

    def record(self, metadata: object) -> None:
        self.events.append(metadata)


def _create_role(role: AgentRole, workspace: Path):
    return RoleAgentFactory(_NoNetworkLlmProvider()).create(
        _request(role, workspace)
    )


def _request(role: AgentRole, workspace: Path) -> RoleConstructionRequest:
    resolved = workspace.resolve()
    return RoleConstructionRequest(
        role=role,
        workspace=resolved,
        serena=SerenaStdioConfiguration(
            command=sys.executable,
            arguments=tuple(_serena_args(resolved)),
        ),
        usage_id=f"{role.value}:compatibility",
        model_id="gemini-3.6-flash",
        openhands_model="gemini/gemini-3.6-flash",
        api_key="not-a-real-key",
    )


def _serena_args(workspace: Path) -> list[str]:
    return [
        str(Path(__file__).with_name("fake_serena_mcp.py")),
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
    ]
