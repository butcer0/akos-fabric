"""Repository-container command-line entry point."""

from __future__ import annotations

import asyncio
import os
import sys
from pathlib import Path
from uuid import UUID

from pydantic import ValidationError

from .agents.session_runner import OpenHandsSessionRoleRunner
from .credentials import load_source_control_credential
from .llm.gemini import GeminiLlmProvider
from .llm.resolver import LlmProviderResolver
from .manifest import SessionManifest
from .orchestrator import SessionOrchestrator
from .result_writer import ResultWriteError
from .runtime_profile import load_runtime_profile
from .telemetry import shutdown_runtime_telemetry


def _required_path(name: str) -> Path:
    value = os.environ.get(name)
    if not value:
        raise ValueError(f"{name} is required")
    path = Path(value)
    if not path.is_absolute():
        raise ValueError(f"{name} must be an absolute path")
    return path.resolve()


def _load_manifest(path: Path) -> SessionManifest:
    try:
        return SessionManifest.model_validate_json(path.read_bytes())
    except (OSError, ValidationError) as error:
        raise ValueError("TASK_MANIFEST is not a valid schema-v1 manifest") from error


async def _run() -> int:
    manifest_path = _required_path("TASK_MANIFEST")
    result_path = _required_path("RESULT_PATH")
    credential_path = _required_path("SOURCE_CONTROL_CREDENTIAL_PATH")
    if len({manifest_path.parent, result_path.parent, credential_path.parent}) != 1:
        raise ValueError(
            "manifest, result, and source-control credential must share a session directory"
        )
    if len({manifest_path, result_path, credential_path}) != 3:
        raise ValueError("runtime input and result paths must be distinct")

    manifest = _load_manifest(manifest_path)
    expected_session = os.environ.get("AGENT_SESSION_ID")
    if expected_session and UUID(expected_session) != manifest.repository_session_id:
        raise ValueError("AGENT_SESSION_ID does not match the manifest")
    if (
        os.environ.get("LLM_PROVIDER")
        and os.environ["LLM_PROVIDER"] != manifest.llm.provider
    ):
        raise ValueError("LLM_PROVIDER does not match the manifest")
    if (
        os.environ.get("LLM_MODEL")
        and os.environ["LLM_MODEL"] != manifest.llm.open_hands_model
    ):
        raise ValueError("LLM_MODEL does not match the manifest")

    profile_path = Path(
        os.environ.get(
            "REPOSITORY_PROFILE_PATH", "/opt/repository-profile/profile.yml"
        )
    ).resolve()
    workspace = Path(os.environ.get("WORKSPACE_PATH", "/workspace")).resolve()
    profile = load_runtime_profile(profile_path)
    api_key = os.environ.get("GEMINI_API_KEY", "")
    providers = LlmProviderResolver([GeminiLlmProvider()])
    orchestrator = SessionOrchestrator(
        manifest=manifest,
        profile=profile,
        credential_loader=lambda: load_source_control_credential(
            credential_path
        ),
        workspace=workspace,
        llm_providers=providers,
        role_runner_factory=lambda provider: OpenHandsSessionRoleRunner(
            provider,
            manifest,
            api_key=api_key,
            serena_executable=os.environ.get(
                "SERENA_EXECUTABLE", "/opt/serena-runtime/.venv/bin/serena"
            ),
        ),
    )
    result = await orchestrator.run_and_write(result_path)
    return 0 if result.status == "completed" else 1


def main() -> int:
    try:
        return asyncio.run(_run())
    except (ValueError, OSError, ResultWriteError) as error:
        print(f"agent runtime failed: {error}", file=sys.stderr)
        return 2
    finally:
        shutdown_runtime_telemetry()


if __name__ == "__main__":
    raise SystemExit(main())
