from __future__ import annotations

import sys
import tempfile
import unittest
import asyncio
from pathlib import Path

from agent_runtime.process_runner import (
    DeterministicProcessRunner,
    ProcessSpec,
    ProcessSpecificationError,
)


class DeterministicProcessRunnerTests(unittest.IsolatedAsyncioTestCase):
    async def test_captures_exit_duration_environment_and_output(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            runner = DeterministicProcessRunner(
                active_worktree=root,
                log_directory=root / "logs",
            )
            result = await runner.run(
                ProcessSpec(
                    argv=(
                        sys.executable,
                        "-c",
                        "import os,sys; print(os.environ['AKOS_TEST']); "
                        "print('problem', file=sys.stderr)",
                    ),
                    working_directory=root,
                    timeout_seconds=5,
                    environment_overrides={"AKOS_TEST": "present"},
                )
            )

            self.assertTrue(result.succeeded)
            self.assertGreaterEqual(result.duration_ms, 0)
            self.assertEqual("present\n", result.stdout_path.read_text())
            self.assertEqual("problem\n", result.stderr_path.read_text())

    async def test_does_not_interpret_shell_syntax(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            runner = DeterministicProcessRunner(
                active_worktree=root, log_directory=root / "logs"
            )
            shell_text = "literal && text > file"
            result = await runner.run(
                ProcessSpec(
                    argv=(
                        sys.executable,
                        "-c",
                        "import sys; print(sys.argv[1])",
                        shell_text,
                    ),
                    working_directory=root,
                    timeout_seconds=5,
                )
            )

            self.assertEqual(shell_text + "\n", result.stdout_path.read_text())
            self.assertFalse((root / "file").exists())

    async def test_bounds_each_log_while_draining_process(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            runner = DeterministicProcessRunner(
                active_worktree=root,
                log_directory=root / "logs",
                maximum_log_bytes=64,
            )
            result = await runner.run(
                ProcessSpec(
                    argv=(
                        sys.executable,
                        "-c",
                        "import sys; print('x' * 1000); "
                        "print('y' * 1000, file=sys.stderr)",
                    ),
                    working_directory=root,
                    timeout_seconds=5,
                )
            )

            self.assertTrue(result.succeeded)
            self.assertEqual(64, result.stdout_path.stat().st_size)
            self.assertEqual(64, result.stderr_path.stat().st_size)

    async def test_times_out_and_records_result(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            runner = DeterministicProcessRunner(
                active_worktree=root, log_directory=root / "logs"
            )
            result = await runner.run(
                ProcessSpec(
                    argv=(
                        sys.executable,
                        "-c",
                        "import time; time.sleep(10)",
                    ),
                    working_directory=root,
                    timeout_seconds=1,
                )
            )

            self.assertTrue(result.timed_out)
            self.assertFalse(result.succeeded)
            self.assertLess(result.duration_ms, 5_000)

    async def test_timeout_terminates_descendant_processes(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            marker = root / "descendant-survived"
            runner = DeterministicProcessRunner(
                active_worktree=root,
                log_directory=root / "logs",
            )
            child_program = (
                "import pathlib,sys,time; time.sleep(2); "
                "pathlib.Path(sys.argv[1]).write_text('survived')"
            )
            parent_program = (
                "import subprocess,sys,time; "
                "subprocess.Popen([sys.executable, '-c', sys.argv[1], sys.argv[2]]); "
                "time.sleep(10)"
            )

            result = await runner.run(
                ProcessSpec(
                    argv=(
                        sys.executable,
                        "-c",
                        parent_program,
                        child_program,
                        str(marker),
                    ),
                    working_directory=root,
                    timeout_seconds=1,
                )
            )
            await asyncio.sleep(2)

            self.assertTrue(result.timed_out)
            self.assertFalse(marker.exists())

    async def test_rejects_working_directory_outside_worktree(self) -> None:
        with tempfile.TemporaryDirectory() as worktree:
            with tempfile.TemporaryDirectory() as outside:
                runner = DeterministicProcessRunner(
                    active_worktree=Path(worktree),
                    log_directory=Path(worktree) / "logs",
                )
                with self.assertRaisesRegex(
                    ProcessSpecificationError, "active worktree"
                ):
                    await runner.run(
                        ProcessSpec(
                            argv=(sys.executable, "--version"),
                            working_directory=Path(outside),
                            timeout_seconds=5,
                        )
                    )


if __name__ == "__main__":
    unittest.main()
