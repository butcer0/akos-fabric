"""Thread-safe, single-assignment typed role completion capture."""

from __future__ import annotations

from threading import Lock
from typing import Generic, TypeVar

from pydantic import BaseModel


CompletionT = TypeVar("CompletionT", bound=BaseModel)


class DuplicateCompletionError(RuntimeError):
    """A role attempted to invoke its terminal completion tool more than once."""


class CompletionNotSubmittedError(RuntimeError):
    """A role ended without invoking its required terminal completion tool."""


class CompletionCapture(Generic[CompletionT]):
    """Validate and capture exactly one completion contract instance."""

    def __init__(self, contract_type: type[CompletionT]) -> None:
        self._contract_type = contract_type
        self._value: CompletionT | None = None
        self._lock = Lock()

    @property
    def contract_type(self) -> type[CompletionT]:
        return self._contract_type

    @property
    def submitted(self) -> bool:
        with self._lock:
            return self._value is not None

    def submit(self, value: CompletionT | object) -> CompletionT:
        validated = self._contract_type.model_validate(value)
        with self._lock:
            if self._value is not None:
                raise DuplicateCompletionError(
                    f"{self._contract_type.__name__} was already submitted"
                )
            self._value = validated
        return validated

    def require(self) -> CompletionT:
        with self._lock:
            value = self._value
        if value is None:
            raise CompletionNotSubmittedError(
                f"{self._contract_type.__name__} was not submitted"
            )
        return value
