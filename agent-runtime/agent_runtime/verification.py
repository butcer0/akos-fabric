"""Deterministic profile command execution."""

from __future__ import annotations

from pathlib import Path

from .contracts.result import CommandVerificationResult, VerificationResult
from .process_runner import IProcessRunner, ProcessSpec
from .runtime_profile import ProfileCommand
from .telemetry import runtime_telemetry


async def run_commands(
    commands: list[ProfileCommand],
    *,
    worktree: Path,
    runner: IProcessRunner,
    record_as_verification: bool = True,
) -> VerificationResult:
    results: list[CommandVerificationResult] = []
    for command in commands:
        span_name = (
            "verification.command" if record_as_verification else "bootstrap.command"
        )
        with runtime_telemetry.span(
            span_name,
            {
                "verification.command.name": command.name,
                "process.executable.name": Path(command.argv[0]).name,
            },
        ) as span:
            result = await runner.run(
                ProcessSpec(
                    argv=tuple(command.argv),
                    working_directory=worktree,
                    timeout_seconds=command.timeout_seconds,
                )
            )
            span.set(exit_code=result.exit_code, timed_out=result.timed_out)
        results.append(
            CommandVerificationResult(
                name=command.name,
                argv=command.argv,
                exit_code=result.exit_code,
                duration_ms=result.duration_ms,
                timed_out=result.timed_out,
                stdout_path=str(result.stdout_path),
                stderr_path=str(result.stderr_path),
            )
        )
        if not result.succeeded and record_as_verification:
            runtime_telemetry.record_verification_failure(command.name)
        if not result.succeeded:
            break
    return VerificationResult(
        passed=all(
            result.exit_code == 0 and not result.timed_out for result in results
        ),
        commands=results,
    )
