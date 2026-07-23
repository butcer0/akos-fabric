from __future__ import annotations

import subprocess
import tempfile
import unittest
from pathlib import Path

from agent_runtime.git_repository import (
    GitCredential,
    GitRepositoryError,
    GitRepositoryService,
)
from agent_runtime.process_runner import DeterministicProcessRunner


def _git(working_directory: Path, *arguments: str) -> str:
    completed = subprocess.run(
        ("git", *arguments),
        cwd=working_directory,
        check=True,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    return completed.stdout.strip()


class GitRepositoryServiceTests(unittest.IsolatedAsyncioTestCase):
    async def test_full_clone_worktree_commit_judge_and_push_flow(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            remote = root / "remote.git"
            seed = root / "seed"
            session = root / "session"
            remote.mkdir()
            seed.mkdir()
            session.mkdir()
            _git(remote, "init", "--bare")
            _git(seed, "init")
            _git(seed, "config", "user.name", "Akos Tests")
            _git(seed, "config", "user.email", "tests@akos.invalid")
            (seed / "source.txt").write_text("base\n", encoding="utf-8")
            _git(seed, "add", "source.txt")
            _git(seed, "commit", "-m", "base")
            _git(seed, "branch", "-M", "main")
            _git(seed, "remote", "add", "origin", str(remote))
            _git(seed, "push", "-u", "origin", "main")

            runner = DeterministicProcessRunner(
                active_worktree=session,
                log_directory=session / "logs",
            )
            service = GitRepositoryService(
                session_root=session,
                runner=runner,
                command_timeout_seconds=30,
            )
            bare = session / "main.git"
            coding = session / "coding"
            judge = session / "judge"
            branch = "agent/kan-1/11111111"

            await service.clone_bare(str(remote), bare)
            base_sha = await service.fetch_default_branch(bare, "main")
            await service.create_coding_worktree(
                bare,
                coding,
                branch,
                base_sha,
            )
            (coding / "source.txt").write_text(
                "base\nchange\n", encoding="utf-8", newline="\n"
            )
            (coding / "new.txt").write_text(
                "new\n", encoding="utf-8", newline="\n"
            )

            candidate = await service.commit_all(coding, "KAN-1: change")

            self.assertEqual(("new.txt", "source.txt"), candidate.changed_files)
            self.assertEqual(2, candidate.diff_lines)
            self.assertEqual(40, len(candidate.sha))

            await service.create_judge_worktree(
                bare,
                judge,
                candidate.sha,
            )
            self.assertEqual(
                candidate.sha,
                _git(judge, "rev-parse", "HEAD"),
            )
            self.assertEqual(
                "base\nchange\n",
                (judge / "source.txt").read_text(encoding="utf-8"),
            )
            await service.remove_worktree(bare, judge)

            credential = GitCredential("x-access-token", "canary-secret")
            await service.push_branch(bare, branch, credential)

            self.assertEqual(
                candidate.sha,
                _git(remote, "rev-parse", f"refs/heads/{branch}"),
            )
            self.assertEqual(str(remote), _git(bare, "remote", "get-url", "origin"))
            self.assertNotIn(
                credential.secret,
                (bare / "config").read_text(encoding="utf-8"),
            )
            self.assertFalse((session / "git-askpass.cmd").exists())
            self.assertFalse((session / "git-askpass.sh").exists())

    async def test_no_change_is_a_deterministic_item_failure(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            repository = root / "repository"
            session = root / "session"
            repository.mkdir()
            session.mkdir()
            _git(repository, "init")
            _git(repository, "config", "user.name", "Akos Tests")
            _git(repository, "config", "user.email", "tests@akos.invalid")
            (repository / "source.txt").write_text("base\n", encoding="utf-8")
            _git(repository, "add", "source.txt")
            _git(repository, "commit", "-m", "base")
            clone = session / "coding"
            _git(session, "clone", str(repository), str(clone))
            runner = DeterministicProcessRunner(
                active_worktree=session,
                log_directory=session / "logs",
            )
            service = GitRepositoryService(
                session_root=session,
                runner=runner,
                command_timeout_seconds=30,
            )

            with self.assertRaisesRegex(GitRepositoryError, "NO_SOURCE_CHANGE"):
                await service.commit_all(clone, "KAN-1: no-op")

    async def test_rejects_unsafe_paths_branches_and_shas_before_git(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            session = Path(temporary)
            runner = DeterministicProcessRunner(
                active_worktree=session,
                log_directory=session / "logs",
            )
            service = GitRepositoryService(
                session_root=session,
                runner=runner,
            )

            with self.assertRaisesRegex(ValueError, "session_root"):
                await service.clone_bare(
                    "https://example.invalid/repository.git",
                    session.parent / "outside.git",
                )
            with self.assertRaisesRegex(ValueError, "unsafe Git branch"):
                GitRepositoryService._require_branch("../escape")
            with self.assertRaisesRegex(ValueError, "Git commit SHA"):
                GitRepositoryService._require_git_sha("abc123")


if __name__ == "__main__":
    unittest.main()
