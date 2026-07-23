"""Shared Pydantic configuration for wire contracts."""

from __future__ import annotations

from pydantic import BaseModel, ConfigDict


def to_camel(name: str) -> str:
    first, *rest = name.split("_")
    return first + "".join(part.capitalize() for part in rest)


class StrictContract(BaseModel):
    """A contract that rejects silently ignored or coerced input."""

    model_config = ConfigDict(
        extra="forbid",
        populate_by_name=True,
        strict=True,
    )


class CamelContract(StrictContract):
    """A strict control-plane wire contract using lower camel case."""

    model_config = ConfigDict(alias_generator=to_camel)
