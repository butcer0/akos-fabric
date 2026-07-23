"""Shell-free, bounded deterministic subprocess execution."""

from __future__ import annotations

import asyncio
import os
import signal
import time
from collections.abc import Mapping
from dataclasses import dataclass, field
from pathlib import Path
from typing import Protocol
from uuid import uuid4

from .telemetry import runtime_telemetry

DEFAULT_MAXIMUM_LOG_BYTES = 10 * 1024 * 1024


class ProcessSpecificationError(ValueError):
    """The requested process violates a deterministic execution boundary."""


@dataclass(frozen=True)
class ProcessSpec:
    argv: tuple[str, ...]
    working_directory: Path
    timeout_seconds: int
    environment_overrides: Mapping[str, str] = field(default_factory=dict)

    def __post_init__(self) -> None:
        if not self.argv:
            raise ProcessSpecificationError("argv must contain an executable")
        if not self.argv[0]:
            raise ProcessSpecificationError("argv executable cannot be empty")
        if any("\0" in argument for argument in self.argv):
            raise ProcessSpecificationError("argv cannot contain NUL bytes")
        if self.timeout_seconds <= 0:
            raise ProcessSpecificationError("timeout_seconds must be positive")
        for name, value in self.environment_overrides.items():
            if not name or "=" in name or "\0" in name:
                raise ProcessSpecificationError(f"invalid environment name: {name!r}")
            if "\0" in value:
                raise ProcessSpecificationError(
                    f"environment value for {name!r} contains a NUL byte"
                )


@dataclass(frozen=True)
class ProcessResult:
    exit_code: int
    duration_ms: int
    stdout_path: Path
    stderr_path: Path
    timed_out: bool

    @property
    def succeeded(self) -> bool:
        return self.exit_code == 0 and not self.timed_out


class IProcessRunner(Protocol):
    async def run(self, spec: ProcessSpec) -> ProcessResult:
        """Run a process without invoking a command shell."""


class DeterministicProcessRunner:
    """Executes argument arrays below one active worktree.

    stdout and stderr are drained concurrently and truncated independently at
    ``maximum_log_bytes``. Truncation never stops pipe draining, so a noisy
    process cannot deadlock after reaching the persisted-output limit.
    """

    def __init__(
        self,
        *,
        active_worktree: Path,
        log_directory: Path,
        maximum_log_bytes: int = DEFAULT_MAXIMUM_LOG_BYTES,
    ) -> None:
        if maximum_log_bytes <= 0:
            raise ValueError("maximum_log_bytes must be positive")
        root = active_worktree.resolve(strict=True)
        if not root.is_dir():
            raise ValueError("active_worktree must be a directory")
        self._active_worktree = root
        self._log_directory = log_directory
        self._maximum_log_bytes = maximum_log_bytes

    async def run(self, spec: ProcessSpec) -> ProcessResult:
        working_directory = spec.working_directory.resolve(strict=True)
        if not working_directory.is_dir():
            raise ProcessSpecificationError("working_directory must be a directory")
        if not working_directory.is_relative_to(self._active_worktree):
            raise ProcessSpecificationError(
                "working_directory must resolve beneath the active worktree"
            )

        self._log_directory.mkdir(parents=True, exist_ok=True)
        invocation_id = uuid4().hex
        stdout_path = self._log_directory / f"{invocation_id}.stdout.log"
        stderr_path = self._log_directory / f"{invocation_id}.stderr.log"
        environment = os.environ.copy()
        environment.update(spec.environment_overrides)

        subprocess_options: dict[str, object] = {}
        if os.name == "nt":
            subprocess_options["creationflags"] = 0x00000200  # CREATE_NEW_PROCESS_GROUP
        else:
            subprocess_options["start_new_session"] = True

        with runtime_telemetry.span(
            "shell.command", {"process.executable.name": Path(spec.argv[0]).name}
        ) as span:
            started = time.monotonic_ns()
            process = await asyncio.create_subprocess_exec(
                *spec.argv,
                cwd=working_directory,
                env=environment,
                stdin=asyncio.subprocess.DEVNULL,
                stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.PIPE,
                **subprocess_options,
            )
            assert process.stdout is not None
            assert process.stderr is not None
            stdout_task = asyncio.create_task(
                self._copy_bounded(process.stdout, stdout_path)
            )
            stderr_task = asyncio.create_task(
                self._copy_bounded(process.stderr, stderr_path)
            )

            timed_out = False
            try:
                async with asyncio.timeout(spec.timeout_seconds):
                    await process.wait()
            except TimeoutError:
                timed_out = True
                await self._terminate_process_tree(process)
            except BaseException:
                if process.returncode is None:
                    await self._terminate_process_tree(process)
                raise
            finally:
                await asyncio.gather(stdout_task, stderr_task)

            duration_ms = (time.monotonic_ns() - started) // 1_000_000
            assert process.returncode is not None
            span.set(
                exit_code=process.returncode,
                timed_out=timed_out,
                stdout_bytes=stdout_path.stat().st_size,
                stderr_bytes=stderr_path.stat().st_size,
            )
            return ProcessResult(
                exit_code=process.returncode,
                duration_ms=duration_ms,
                stdout_path=stdout_path,
                stderr_path=stderr_path,
                timed_out=timed_out,
            )

    @staticmethod
    async def _terminate_process_tree(
        process: asyncio.subprocess.Process,
    ) -> None:
        if process.returncode is not None:
            return

        if os.name == "nt":
            terminator = await asyncio.create_subprocess_exec(
                "taskkill",
                "/PID",
                str(process.pid),
                "/T",
                "/F",
                stdin=asyncio.subprocess.DEVNULL,
                stdout=asyncio.subprocess.DEVNULL,
                stderr=asyncio.subprocess.DEVNULL,
            )
            await terminator.wait()
            if process.returncode is None and terminator.returncode != 0:
                process.kill()
        else:
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass

        await process.wait()

    async def _copy_bounded(
        self, stream: asyncio.StreamReader, destination: Path
    ) -> None:
        remaining = self._maximum_log_bytes
        with destination.open("wb") as output:
            while chunk := await stream.read(64 * 1024):
                if remaining:
                    persisted = chunk[:remaining]
                    output.write(persisted)
                    remaining -= len(persisted)
            output.flush()
            os.fsync(output.fileno())
