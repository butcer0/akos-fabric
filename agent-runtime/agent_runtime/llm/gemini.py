"""Gemini implementation of the OpenHands LLM provider seam."""

from __future__ import annotations

from collections.abc import Callable
from importlib import import_module
from typing import Any

from .interface import LLM, LlmRuntimeDependencyError

SUPPORTED_MODEL_ID = "gemini-3.6-flash"


class GeminiLlmProvider:
    provider_name = "gemini"

    def __init__(self, llm_factory: Callable[..., LLM] | None = None) -> None:
        self._llm_factory = llm_factory

    def create_llm(
        self,
        *,
        usage_id: str,
        model_id: str,
        openhands_model: str,
        api_key: str,
    ) -> LLM:
        if model_id != SUPPORTED_MODEL_ID:
            raise ValueError(f"Unsupported configured Gemini model: {model_id}")
        expected_openhands_model = f"gemini/{model_id}"
        if openhands_model != expected_openhands_model:
            raise ValueError(
                "Gemini model identifiers are inconsistent: "
                f"model_id={model_id!r} openhands_model={openhands_model!r}"
            )
        if not usage_id:
            raise ValueError("usage_id cannot be empty")
        if not api_key:
            raise ValueError("api_key cannot be empty")

        factory = self._llm_factory or _load_openhands_llm()
        return factory(
            usage_id=usage_id,
            model=openhands_model,
            api_key=api_key,
            num_retries=1,
        )


def _load_openhands_llm() -> Callable[..., Any]:
    """Load OpenHands only in an execution path that constructs an LLM."""

    try:
        sdk = import_module("openhands.sdk")
        llm = getattr(sdk, "LLM")
    except (ImportError, AttributeError) as error:
        raise LlmRuntimeDependencyError(
            "OpenHands SDK with openhands.sdk.LLM is required to create an LLM"
        ) from error
    if not callable(llm):
        raise LlmRuntimeDependencyError("openhands.sdk.LLM is not callable")
    return llm
