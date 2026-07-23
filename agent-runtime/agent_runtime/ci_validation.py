"""Run repository-profile deterministic validation for provider-native CI."""

from __future__ import annotations

import argparse
import asyncio
import json
import sys
from pathlib import Path

from agent_runtime.ci_review import _write_atomic
from agent_runtime.process_runner import DeterministicProcessRunner
from agent_runtime.runtime_profile import load_runtime_profile
from agent_runtime.verification import run_commands


async def run_ci_validation(
    *,
    repository: Path,
    profile_path: Path,
    result_path: Path,
    log_directory: Path,
) -> bool:
    repository = repository.resolve(strict=True)
    if not repository.is_dir():
        raise ValueError("repository must be a directory")
    if result_path.resolve().is_relative_to(repository):
        raise ValueError("validation result must be outside the repository checkout")
    profile = load_runtime_profile(profile_path)
    runner = DeterministicProcessRunner(
        active_worktree=repository,
        log_directory=log_directory,
    )
    bootstrap = await run_commands(profile.bootstrap, worktree=repository, runner=runner)
    verification = None
    if bootstrap.passed:
        verification = await run_commands(
            profile.verification.required,
            worktree=repository,
            runner=runner,
        )
    commands = list(bootstrap.commands)
    if verification is not None:
        commands.extend(verification.commands)
    passed = bootstrap.passed and verification is not None and verification.passed
    payload = {
        "schemaVersion": "1.0",
        "passed": passed,
        "commands": [
            command.model_dump(mode="json", by_alias=True) for command in commands
        ],
    }
    _write_atomic(
        result_path.resolve(),
        json.dumps(payload, indent=2, separators=(",", ": ")).encode("utf-8")
        + b"\n",
    )
    return passed


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Run repository-owned bootstrap and required validation."
    )
    parser.add_argument("--repository", required=True)
    parser.add_argument("--profile", required=True)
    parser.add_argument("--result", required=True)
    parser.add_argument("--logs", required=True)
    return parser


def main() -> int:
    arguments = _parser().parse_args()
    try:
        passed = asyncio.run(
            run_ci_validation(
                repository=Path(arguments.repository),
                profile_path=Path(arguments.profile),
                result_path=Path(arguments.result),
                log_directory=Path(arguments.logs),
            )
        )
        return 0 if passed else 1
    except (OSError, RuntimeError, ValueError) as error:
        print(f"CI validation failed: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
