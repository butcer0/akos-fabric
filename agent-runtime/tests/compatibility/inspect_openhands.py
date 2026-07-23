"""Executable inspection of the pinned OpenHands public construction surface."""

from __future__ import annotations

import inspect
import json

import openhands.sdk as sdk
import openhands.sdk.mcp as mcp
import openhands.tools as tools


def main() -> None:
    sdk_names = ("Agent", "LLM", "Conversation", "Tool", "Event")
    payload = {
        "sdk_symbols": {name: hasattr(sdk, name) for name in sdk_names},
        "tool_exports": sorted(
            name for name in dir(tools) if not name.startswith("_")
        ),
        "signatures": {
            name: str(inspect.signature(getattr(sdk, name)))
            for name in ("Agent", "LLM", "Conversation")
            if hasattr(sdk, name)
        },
        "tool_signatures": {
            name: str(inspect.signature(getattr(tools, name)))
            for name in ("TerminalTool", "FileEditorTool", "get_default_tools")
        },
        "tool_values": {
            name: {
                "type": type(getattr(tools, name)).__qualname__,
                "repr": repr(getattr(tools, name)),
            }
            for name in ("terminal", "file_editor")
        },
        "mcp_exports": sorted(
            name for name in dir(mcp) if not name.startswith("_")
        ),
    }
    print(json.dumps(payload, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
