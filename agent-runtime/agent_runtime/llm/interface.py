"""LLM provider seam owned by the repository-session runtime."""

from __future__ import annotations

from typing import TYPE_CHECKING, Protocol

if TYPE_CHECKING:
    from openhands.sdk import LLM
else:
    LLM = object


class LlmRuntimeDependencyError(RuntimeError):
    """The pinned OpenHands runtime dependency is unavailable or incompatible."""


class ILlmProvider(Protocol):
    @property
    def provider_name(self) -> str:
        ...

    def create_llm(
        self,
        *,
        usage_id: str,
        model_id: str,
        openhands_model: str,
        api_key: str,
    ) -> LLM:
        ...

