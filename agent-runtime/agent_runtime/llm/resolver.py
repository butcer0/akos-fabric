"""Resolve manifest provider identifiers without provider conditionals."""

from __future__ import annotations

from collections.abc import Iterable

from .interface import ILlmProvider


class UnknownLlmProviderError(ValueError):
    """No registered provider matches the manifest."""


class LlmProviderResolver:
    def __init__(self, providers: Iterable[ILlmProvider]) -> None:
        registrations: dict[str, ILlmProvider] = {}
        for provider in providers:
            name = provider.provider_name
            if not name:
                raise ValueError("LLM provider name cannot be empty")
            if name in registrations:
                raise ValueError(f"duplicate LLM provider registration: {name}")
            registrations[name] = provider
        self._providers = registrations

    def resolve(self, provider_name: str) -> ILlmProvider:
        try:
            return self._providers[provider_name]
        except KeyError as error:
            raise UnknownLlmProviderError(
                f"unknown LLM provider: {provider_name}"
            ) from error

