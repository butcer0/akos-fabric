"""No-network stdio MCP used only to prove OpenHands discovery wiring."""

from __future__ import annotations

import argparse
from pathlib import Path

from mcp.server.fastmcp import FastMCP


def _parse_arguments() -> Path:
    parser = argparse.ArgumentParser()
    parser.add_argument("subcommand")
    parser.add_argument("--transport", required=True)
    parser.add_argument("--context", required=True)
    parser.add_argument("--project", type=Path, required=True)
    parser.add_argument("--enable-web-dashboard", required=True)
    parser.add_argument("--open-web-dashboard", required=True)
    arguments = parser.parse_args()
    if (
        arguments.transport != "stdio"
        or arguments.context != "ide"
        or arguments.enable_web_dashboard != "false"
        or arguments.open_web_dashboard != "false"
    ):
        parser.error("the compatibility server requires ide context and no dashboard")
    if not arguments.project.is_dir():
        parser.error("project must be an existing role workspace")
    return arguments.project.resolve()


project = _parse_arguments()
mcp = FastMCP("fake-serena-compatibility")


@mcp.tool()
def get_symbols_overview(relative_path: str = ".") -> dict[str, str]:
    """Return metadata only; this fake never reads repository source."""

    return {
        "project": str(project),
        "relative_path": relative_path,
        "status": "ready",
    }


if __name__ == "__main__":
    mcp.run(transport="stdio")
