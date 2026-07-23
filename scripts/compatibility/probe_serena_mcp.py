"""Fail-fast readiness probe for the pinned Serena stdio MCP server."""

from __future__ import annotations

import argparse
import asyncio
import json
from datetime import timedelta
from pathlib import Path

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client


async def probe(project: Path, source_file: str, serena: Path) -> None:
    parameters = StdioServerParameters(
        command=str(serena),
        args=[
            "start-mcp-server",
            "--transport",
            "stdio",
            "--context",
            "ide",
            "--project",
            str(project),
            "--enable-web-dashboard",
            "false",
            "--open-web-dashboard",
            "false",
        ],
        cwd=project,
    )
    timeout = timedelta(seconds=120)
    async with stdio_client(parameters) as (read_stream, write_stream):
        async with ClientSession(
            read_stream,
            write_stream,
            read_timeout_seconds=timeout,
        ) as session:
            initialized = await session.initialize()
            tools = await session.list_tools()
            tool_names = sorted(tool.name for tool in tools.tools)
            if "get_symbols_overview" not in tool_names:
                raise RuntimeError(
                    "Serena did not expose get_symbols_overview"
                )

            result = await session.call_tool(
                "get_symbols_overview",
                {"relative_path": source_file, "depth": 1},
                read_timeout_seconds=timeout,
            )
            if result.isError:
                raise RuntimeError(
                    "Serena get_symbols_overview returned an error"
                )
            response_text = "\n".join(
                getattr(content, "text", "")
                for content in result.content
            )
            if "Error executing tool:" in response_text:
                raise RuntimeError(
                    "Serena get_symbols_overview returned an embedded tool error"
                )
            content_size = len(response_text)
            if content_size == 0:
                raise RuntimeError(
                    "Serena get_symbols_overview returned no content"
                )

            print(
                json.dumps(
                    {
                        "serverName": initialized.serverInfo.name,
                        "serverVersion": initialized.serverInfo.version,
                        "toolCount": len(tool_names),
                        "semanticTool": "get_symbols_overview",
                        "sourceFile": source_file,
                        "responseCharacters": content_size,
                    },
                    sort_keys=True,
                )
            )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--project", type=Path, required=True)
    parser.add_argument("--source-file", required=True)
    parser.add_argument(
        "--serena",
        type=Path,
        default=Path("/opt/serena-runtime/.venv/bin/serena"),
    )
    arguments = parser.parse_args()

    project = arguments.project.resolve(strict=True)
    source = (project / arguments.source_file).resolve(strict=True)
    source.relative_to(project)
    asyncio.run(probe(project, arguments.source_file, arguments.serena))


if __name__ == "__main__":
    main()
