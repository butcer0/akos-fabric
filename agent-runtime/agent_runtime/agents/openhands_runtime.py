"""OpenHands 1.36.1 adapter, imported only when a role is constructed."""

from __future__ import annotations

from collections.abc import Callable, Sequence
from threading import Lock
from typing import TYPE_CHECKING, Any, ClassVar, Self
from uuid import uuid4
from weakref import WeakValueDictionary

from openhands.sdk import Agent, Conversation, Tool
from openhands.sdk.mcp import MCPServer
from openhands.sdk.tool import (
    Action,
    Observation,
    ToolAnnotations,
    ToolDefinition,
    ToolExecutor,
    register_tool,
)
from openhands.tools import FileEditorTool, TerminalTool
from pydantic import ConfigDict

from agent_runtime.contracts.candidate import CandidateV1
from agent_runtime.contracts.judgment import JudgmentV1
from agent_runtime.contracts.plan import PlanV1
from agent_runtime.contracts.review import ReviewV1

from .completion import CompletionCapture


if TYPE_CHECKING:
    from openhands.sdk.conversation.state import ConversationState

    from .roles import (
        RoleCompletion,
        RoleConstructionRequest,
    )


class SubmitPlanAction(Action):
    plan: PlanV1


class SubmitCandidateAction(Action):
    candidate: CandidateV1


class SubmitJudgmentAction(Action):
    judgment: JudgmentV1


class SubmitReviewAction(Action):
    review: ReviewV1


class CompletionObservation(Observation):
    model_config: ClassVar[ConfigDict] = ConfigDict(extra="forbid", frozen=True)


_captures: WeakValueDictionary[str, CompletionCapture[Any]] = WeakValueDictionary()
_captures_lock = Lock()


def _register_capture(capture: CompletionCapture[Any]) -> tuple[str, Callable[[], None]]:
    capture_id = uuid4().hex
    with _captures_lock:
        _captures[capture_id] = capture

    def release() -> None:
        with _captures_lock:
            _captures.pop(capture_id, None)

    return capture_id, release


def _resolve_capture(capture_id: str) -> CompletionCapture[Any]:
    with _captures_lock:
        capture = _captures.get(capture_id)
    if capture is None:
        raise RuntimeError("Completion capture is unavailable or already released")
    return capture


class _PlanExecutor(ToolExecutor[SubmitPlanAction, CompletionObservation]):
    def __init__(self, capture: CompletionCapture[PlanV1]) -> None:
        self._capture = capture

    def __call__(
        self,
        action: SubmitPlanAction,
        conversation: object | None = None,
    ) -> CompletionObservation:
        del conversation
        self._capture.submit(action.plan)
        return CompletionObservation.from_text("Plan completion accepted.")


class _CandidateExecutor(
    ToolExecutor[SubmitCandidateAction, CompletionObservation]
):
    def __init__(self, capture: CompletionCapture[CandidateV1]) -> None:
        self._capture = capture

    def __call__(
        self,
        action: SubmitCandidateAction,
        conversation: object | None = None,
    ) -> CompletionObservation:
        del conversation
        self._capture.submit(action.candidate)
        return CompletionObservation.from_text("Candidate completion accepted.")


class _JudgmentExecutor(
    ToolExecutor[SubmitJudgmentAction, CompletionObservation]
):
    def __init__(self, capture: CompletionCapture[JudgmentV1]) -> None:
        self._capture = capture

    def __call__(
        self,
        action: SubmitJudgmentAction,
        conversation: object | None = None,
    ) -> CompletionObservation:
        del conversation
        self._capture.submit(action.judgment)
        return CompletionObservation.from_text("Judgment completion accepted.")


class _ReviewExecutor(ToolExecutor[SubmitReviewAction, CompletionObservation]):
    def __init__(self, capture: CompletionCapture[ReviewV1]) -> None:
        self._capture = capture

    def __call__(
        self,
        action: SubmitReviewAction,
        conversation: object | None = None,
    ) -> CompletionObservation:
        del conversation
        self._capture.submit(action.review)
        return CompletionObservation.from_text("CI review completion accepted.")


_COMPLETION_ANNOTATIONS = ToolAnnotations(
    readOnlyHint=False,
    destructiveHint=False,
    idempotentHint=False,
    openWorldHint=False,
)


class SubmitPlanTool(ToolDefinition[SubmitPlanAction, CompletionObservation]):
    @classmethod
    def create(
        cls,
        conv_state: "ConversationState | None" = None,
        *,
        capture_id: str,
    ) -> Sequence[Self]:
        del conv_state
        capture = _resolve_capture(capture_id)
        if capture.contract_type is not PlanV1:
            raise TypeError("submit_plan requires a PlanV1 capture")
        return [
            cls(
                action_type=SubmitPlanAction,
                observation_type=CompletionObservation,
                description=(
                    "Submit the final structured planner result. "
                    "This tool may be called exactly once."
                ),
                annotations=_COMPLETION_ANNOTATIONS,
                executor=_PlanExecutor(capture),
            )
        ]


class SubmitCandidateTool(
    ToolDefinition[SubmitCandidateAction, CompletionObservation]
):
    @classmethod
    def create(
        cls,
        conv_state: "ConversationState | None" = None,
        *,
        capture_id: str,
    ) -> Sequence[Self]:
        del conv_state
        capture = _resolve_capture(capture_id)
        if capture.contract_type is not CandidateV1:
            raise TypeError("submit_candidate requires a CandidateV1 capture")
        return [
            cls(
                action_type=SubmitCandidateAction,
                observation_type=CompletionObservation,
                description=(
                    "Submit the final structured coder result. "
                    "This tool may be called exactly once."
                ),
                annotations=_COMPLETION_ANNOTATIONS,
                executor=_CandidateExecutor(capture),
            )
        ]


class SubmitJudgmentTool(
    ToolDefinition[SubmitJudgmentAction, CompletionObservation]
):
    @classmethod
    def create(
        cls,
        conv_state: "ConversationState | None" = None,
        *,
        capture_id: str,
    ) -> Sequence[Self]:
        del conv_state
        capture = _resolve_capture(capture_id)
        if capture.contract_type is not JudgmentV1:
            raise TypeError("submit_judgment requires a JudgmentV1 capture")
        return [
            cls(
                action_type=SubmitJudgmentAction,
                observation_type=CompletionObservation,
                description=(
                    "Submit the final structured judge result. "
                    "This tool may be called exactly once."
                ),
                annotations=_COMPLETION_ANNOTATIONS,
                executor=_JudgmentExecutor(capture),
            )
        ]


class SubmitReviewTool(ToolDefinition[SubmitReviewAction, CompletionObservation]):
    @classmethod
    def create(
        cls,
        conv_state: "ConversationState | None" = None,
        *,
        capture_id: str,
    ) -> Sequence[Self]:
        del conv_state
        capture = _resolve_capture(capture_id)
        if capture.contract_type is not ReviewV1:
            raise TypeError("submit_review requires a ReviewV1 capture")
        return [
            cls(
                action_type=SubmitReviewAction,
                observation_type=CompletionObservation,
                description=(
                    "Submit the final structured informational CI review. "
                    "This tool may be called exactly once."
                ),
                annotations=_COMPLETION_ANNOTATIONS,
                executor=_ReviewExecutor(capture),
            )
        ]


register_tool(SubmitPlanTool.name, SubmitPlanTool)
register_tool(SubmitCandidateTool.name, SubmitCandidateTool)
register_tool(SubmitJudgmentTool.name, SubmitJudgmentTool)
register_tool(SubmitReviewTool.name, SubmitReviewTool)


def create_openhands_role(
    request: "RoleConstructionRequest",
    workspace: Any,
    llm: object,
    capture: "CompletionCapture[RoleCompletion]",
    callback: Callable[[object], None],
) -> tuple[object, object, Callable[[], None]]:
    capture_id, release = _register_capture(capture)
    completion_tool_name = {
        "planner": SubmitPlanTool.name,
        "coder": SubmitCandidateTool.name,
        "judge": SubmitJudgmentTool.name,
        "ci-review": SubmitReviewTool.name,
    }[request.role.value]

    try:
        serena = MCPServer(
            transport="stdio",
            command=request.serena.command,
            args=list(request.serena.arguments),
            cwd=str(workspace),
        )
        agent = Agent(
            llm=llm,
            tools=[
                Tool(
                    name=TerminalTool.name,
                    params={"terminal_type": "subprocess"},
                ),
                Tool(name=FileEditorTool.name),
                Tool(
                    name=completion_tool_name,
                    params={"capture_id": capture_id},
                ),
            ],
            mcp_config={"serena": serena},
            include_default_tools=[],
        )
        conversation = Conversation(
            agent=agent,
            workspace=workspace,
            callbacks=[callback],
            visualizer=None,
            observability_metadata={"role": request.role.value},
            observability_span_name=f"agent.role.{request.role.value}",
        )
    except BaseException:
        release()
        raise

    return agent, conversation, release
