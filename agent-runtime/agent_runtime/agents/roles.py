"""Provider-neutral construction boundary for independent OpenHands roles."""

from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass, field
from enum import StrEnum
from importlib import import_module
from pathlib import Path
from typing import Any, Protocol

from agent_runtime.contracts.candidate import CandidateV1
from agent_runtime.contracts.judgment import JudgmentV1
from agent_runtime.contracts.plan import PlanV1
from agent_runtime.contracts.review import ReviewV1
from agent_runtime.llm.interface import ILlmProvider

from .completion import CompletionCapture


class AgentRole(StrEnum):
    PLANNER = "planner"
    CODER = "coder"
    JUDGE = "judge"
    CI_REVIEW = "ci-review"


RoleCompletion = PlanV1 | CandidateV1 | JudgmentV1 | ReviewV1


class RoleRuntimeDependencyError(RuntimeError):
    """The optional, pinned OpenHands role runtime is unavailable."""


@dataclass(frozen=True)
class SerenaStdioConfiguration:
    """Caller-supplied Serena command proven by a separate compatibility gate."""

    command: str
    arguments: tuple[str, ...]

    def validate_for(self, workspace: Path) -> None:
        if not self.command or any(character in self.command for character in "\0\r\n"):
            raise ValueError("Serena command must be a non-empty executable name")
        if not self.arguments or any(
            not argument or any(character in argument for character in "\0\r\n")
            for argument in self.arguments
        ):
            raise ValueError("Serena arguments must be non-empty shell-free values")

        expected_workspace = str(workspace)
        self._require_option("--transport", "stdio")
        self._require_option("--context", "ide")
        self._require_option("--project", expected_workspace)
        self._require_option("--enable-web-dashboard", "false")
        self._require_option("--open-web-dashboard", "false")

    def _require_option(self, name: str, expected_value: str) -> None:
        positions = [
            index for index, argument in enumerate(self.arguments) if argument == name
        ]
        if len(positions) != 1:
            raise ValueError(f"Serena arguments must contain exactly one {name}")
        position = positions[0]
        if position + 1 >= len(self.arguments):
            raise ValueError(f"Serena argument {name} requires a value")
        if self.arguments[position + 1] != expected_value:
            raise ValueError(
                f"Serena argument {name} must equal {expected_value!r}"
            )


@dataclass(frozen=True)
class RoleConstructionRequest:
    role: AgentRole
    workspace: Path
    serena: SerenaStdioConfiguration
    usage_id: str
    model_id: str
    openhands_model: str
    api_key: str = field(repr=False)

    def validated_workspace(self) -> Path:
        workspace = self.workspace.resolve(strict=True)
        if not workspace.is_dir():
            raise ValueError("Role workspace must be an existing directory")
        self.serena.validate_for(workspace)
        if not self.usage_id:
            raise ValueError("Role usage_id cannot be empty")
        if not self.model_id or not self.openhands_model or not self.api_key:
            raise ValueError("Role model identifiers and API key are required")
        return workspace


@dataclass(frozen=True)
class RoleEventMetadata:
    """The complete allowed event callback payload; it contains no event content."""

    role: AgentRole
    event_type: str
    source: str
    timestamp: str


class MetadataEventSink(Protocol):
    def record(self, metadata: RoleEventMetadata) -> None:
        ...


class _DiscardMetadataSink:
    def record(self, metadata: RoleEventMetadata) -> None:
        del metadata


class _MetadataOnlyCallback:
    def __init__(self, role: AgentRole, sink: MetadataEventSink) -> None:
        self._role = role
        self._sink = sink

    def __call__(self, event: object) -> None:
        self._sink.record(
            RoleEventMetadata(
                role=self._role,
                event_type=type(event).__name__,
                source=str(getattr(event, "source", "unknown")),
                timestamp=str(getattr(event, "timestamp", "")),
            )
        )


@dataclass
class RoleConversation:
    """One role's isolated agent, conversation, workspace, and terminal result."""

    role: AgentRole
    workspace: Path
    agent: Any
    conversation: Any
    completion: CompletionCapture[RoleCompletion]
    _release_completion: Callable[[], None]
    _closed: bool = False

    def close(self) -> None:
        if self._closed:
            return
        try:
            self.conversation.close()
        finally:
            self._release_completion()
            self._closed = True

    def __enter__(self) -> "RoleConversation":
        return self

    def __exit__(self, *_: object) -> None:
        self.close()


RuntimeBuilder = Callable[
    [
        RoleConstructionRequest,
        Path,
        object,
        CompletionCapture[RoleCompletion],
        Callable[[object], None],
    ],
    tuple[object, object, Callable[[], None]],
]


class RoleAgentFactory:
    """Create independent role conversations while leaving LLMs to ILlmProvider."""

    def __init__(
        self,
        llm_provider: ILlmProvider,
        *,
        metadata_sink: MetadataEventSink | None = None,
        runtime_builder: RuntimeBuilder | None = None,
    ) -> None:
        self._llm_provider = llm_provider
        self._metadata_sink = metadata_sink or _DiscardMetadataSink()
        self._runtime_builder = runtime_builder

    def create(self, request: RoleConstructionRequest) -> RoleConversation:
        workspace = request.validated_workspace()
        completion = _completion_capture(request.role)
        llm = self._llm_provider.create_llm(
            usage_id=request.usage_id,
            model_id=request.model_id,
            openhands_model=request.openhands_model,
            api_key=request.api_key,
        )
        callback = _MetadataOnlyCallback(request.role, self._metadata_sink)
        builder = self._runtime_builder or _load_openhands_runtime_builder()
        agent, conversation, release = builder(
            request,
            workspace,
            llm,
            completion,
            callback,
        )
        return RoleConversation(
            role=request.role,
            workspace=workspace,
            agent=agent,
            conversation=conversation,
            completion=completion,
            _release_completion=release,
        )


def _completion_capture(
    role: AgentRole,
) -> CompletionCapture[RoleCompletion]:
    contract: type[RoleCompletion]
    if role is AgentRole.PLANNER:
        contract = PlanV1
    elif role is AgentRole.CODER:
        contract = CandidateV1
    elif role is AgentRole.JUDGE:
        contract = JudgmentV1
    elif role is AgentRole.CI_REVIEW:
        contract = ReviewV1
    else:  # pragma: no cover - StrEnum construction prevents this
        raise ValueError(f"Unsupported role: {role}")
    return CompletionCapture(contract)


def _load_openhands_runtime_builder() -> RuntimeBuilder:
    try:
        module = import_module("agent_runtime.agents.openhands_runtime")
    except ModuleNotFoundError as error:
        missing = error.name or ""
        if missing == "openhands" or missing.startswith(("openhands.", "litellm")):
            raise RoleRuntimeDependencyError(
                "Install the pinned 'openhands' runtime extra to construct roles"
            ) from error
        raise

    builder = getattr(module, "create_openhands_role", None)
    if not callable(builder):
        raise RoleRuntimeDependencyError(
            "The OpenHands role runtime does not expose create_openhands_role"
        )
    return builder
