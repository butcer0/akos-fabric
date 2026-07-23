"""Typed standard-Git operations for one repository session."""

from __future__ import annotations

import os
import re
import stat
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path
from typing import cast

from .contracts._validation import GitSha, require_relative_git_path
from .process_runner import IProcessRunner, ProcessResult, ProcessSpec
from .telemetry import runtime_telemetry

_BRANCH_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._/-]*$")
_GIT_SHA_PATTERN = re.compile(r"^[0-9a-f]{40,64}$")


class GitRepositoryError(RuntimeError):
    """A deterministic Git operation failed or returned invalid evidence."""


@dataclass(frozen=True)
class GitCredential:
    username: str
    secret: str

    def __post_init__(self) -> None:
        if not self.username or not self.secret:
            raise ValueError("Git credential username and secret are required")
        if "\0" in self.username or "\0" in self.secret:
            raise ValueError("Git credentials cannot contain NUL bytes")


@dataclass(frozen=True)
class CandidateCommit:
    sha: GitSha
    changed_files: tuple[str, ...]
    diff_lines: int


GitCredentialSource = GitCredential | Callable[[], GitCredential]


class GitRepositoryService:
    """Owns clone, worktree, commit, and push mechanics for a session.

    All commands are argument arrays executed by ``IProcessRunner``. Provider
    credentials are exposed only through a temporary askpass helper and child
    process environment, never in a clone URL, remote, or argv.
    """

    def __init__(
        self,
        *,
        session_root: Path,
        runner: IProcessRunner,
        command_timeout_seconds: int = 300,
    ) -> None:
        if command_timeout_seconds <= 0:
            raise ValueError("command_timeout_seconds must be positive")
        self._session_root = session_root.resolve(strict=True)
        if not self._session_root.is_dir():
            raise ValueError("session_root must be a directory")
        self._runner = runner
        self._timeout = command_timeout_seconds

    async def clone_bare(
        self,
        clone_url: str,
        bare_repository: Path,
        credential: GitCredentialSource | None = None,
    ) -> None:
        destination = self._require_session_child(
            bare_repository, must_exist=False
        )
        if destination.exists():
            raise GitRepositoryError(
                f"bare repository destination already exists: {destination}"
            )
        with runtime_telemetry.span(
            "repository.clone", {"repository.kind": "main"}
        ):
            await self._git(
                ("clone", "--bare", "--", clone_url, str(destination)),
                working_directory=self._session_root,
                credential=credential,
            )

    async def clone_checkout(
        self,
        clone_url: str,
        destination: Path,
        default_branch: str,
        *,
        git_lfs: bool,
        submodules: str,
        credential: GitCredentialSource | None = None,
    ) -> None:
        """Create a full-history supplemental checkout below the session root."""

        checkout = self._require_session_child(destination, must_exist=False)
        if checkout.exists():
            raise GitRepositoryError(
                f"repository destination already exists: {checkout}"
            )
        branch = self._require_branch(default_branch)
        environment = {} if git_lfs else {"GIT_LFS_SKIP_SMUDGE": "1"}
        with runtime_telemetry.span(
            "repository.clone", {"repository.kind": "supplemental"}
        ):
            await self._git(
                ("clone", "--branch", branch, "--", clone_url, str(checkout)),
                working_directory=self._session_root,
                credential=credential,
                environment_overrides=environment,
            )
        if submodules == "recursive":
            await self._git(
                ("submodule", "update", "--init", "--recursive"),
                working_directory=checkout,
                credential=credential,
                environment_overrides=environment,
            )
        elif submodules != "none":
            raise ValueError(f"unsupported submodule mode: {submodules!r}")

    async def fetch_default_branch(
        self,
        bare_repository: Path,
        default_branch: str,
        credential: GitCredentialSource | None = None,
    ) -> GitSha:
        bare = self._require_session_child(bare_repository, must_exist=True)
        branch = self._require_branch(default_branch)
        remote_ref = f"refs/remotes/origin/{branch}"
        with runtime_telemetry.span("repository.fetch"):
            await self._git(
                (
                    "--git-dir",
                    str(bare),
                    "fetch",
                    "--prune",
                    "origin",
                    f"refs/heads/{branch}:{remote_ref}",
                ),
                working_directory=self._session_root,
                credential=credential,
            )
        return await self.resolve_revision(bare, remote_ref)

    async def create_coding_worktree(
        self,
        bare_repository: Path,
        worktree: Path,
        branch_name: str,
        base_sha: str,
    ) -> None:
        bare = self._require_session_child(bare_repository, must_exist=True)
        destination = self._require_session_child(worktree, must_exist=False)
        branch = self._require_branch(branch_name)
        sha = self._require_git_sha(base_sha)
        with runtime_telemetry.span(
            "git.worktree.create", {"worktree.kind": "coding"}
        ):
            await self._git(
                (
                    "--git-dir",
                    str(bare),
                    "worktree",
                    "add",
                    "-b",
                    branch,
                    str(destination),
                    sha,
                ),
                working_directory=self._session_root,
            )

    async def create_judge_worktree(
        self,
        bare_repository: Path,
        worktree: Path,
        candidate_sha: str,
    ) -> None:
        bare = self._require_session_child(bare_repository, must_exist=True)
        destination = self._require_session_child(worktree, must_exist=False)
        sha = self._require_git_sha(candidate_sha)
        with runtime_telemetry.span(
            "git.worktree.create", {"worktree.kind": "judge"}
        ):
            await self._git(
                (
                    "--git-dir",
                    str(bare),
                    "worktree",
                    "add",
                    "--detach",
                    str(destination),
                    sha,
                ),
                working_directory=self._session_root,
            )

    async def commit_all(
        self,
        worktree: Path,
        message: str,
        *,
        author_name: str = "Akos Fabric Agent",
        author_email: str = "agent@akos-fabric.invalid",
    ) -> CandidateCommit:
        tree = self._require_session_child(worktree, must_exist=True)
        if not message.strip():
            raise ValueError("commit message must be non-empty")

        changed_files = await self.changed_files(tree)
        if not changed_files:
            raise GitRepositoryError("NO_SOURCE_CHANGE")

        with runtime_telemetry.span("git.commit"):
            await self._git(("add", "--all"), working_directory=tree)
            diff_lines = await self.diff_line_count(tree, staged=True)
            await self._git(
                (
                    "-c",
                    f"user.name={author_name}",
                    "-c",
                    f"user.email={author_email}",
                    "commit",
                    "--no-gpg-sign",
                    "-m",
                    message,
                ),
                working_directory=tree,
            )
        sha = await self.resolve_revision(tree, "HEAD")
        return CandidateCommit(
            sha=sha,
            changed_files=changed_files,
            diff_lines=diff_lines,
        )

    async def changed_files(self, worktree: Path) -> tuple[str, ...]:
        tree = self._require_session_child(worktree, must_exist=True)
        result = await self._git(
            ("status", "--porcelain=v1", "-z"),
            working_directory=tree,
        )
        payload = result.stdout_path.read_bytes()
        paths: list[str] = []
        records = payload.split(b"\0")
        index = 0
        while index < len(records):
            record = records[index]
            index += 1
            if not record:
                continue
            if len(record) < 4:
                raise GitRepositoryError("Git status returned a malformed record")
            status_code = record[:2].decode("ascii", errors="strict")
            path = record[3:].decode("utf-8", errors="strict")
            if status_code[0] in ("R", "C"):
                if index >= len(records) or not records[index]:
                    raise GitRepositoryError(
                        "Git status returned a malformed rename record"
                    )
                path = records[index].decode("utf-8", errors="strict")
                index += 1
            paths.append(require_relative_git_path(path))
        return tuple(sorted(set(paths)))

    async def diff_line_count(
        self, worktree: Path, *, staged: bool = False
    ) -> int:
        tree = self._require_session_child(worktree, must_exist=True)
        arguments = ["diff", "--no-ext-diff", "--numstat"]
        if staged:
            arguments.append("--cached")
        arguments.append("HEAD")
        result = await self._git(
            tuple(arguments),
            working_directory=tree,
        )
        total = 0
        for line in result.stdout_path.read_text(
            encoding="utf-8", errors="strict"
        ).splitlines():
            added, deleted, _path = line.split("\t", maxsplit=2)
            if added != "-":
                total += int(added)
            if deleted != "-":
                total += int(deleted)
        return total

    async def resolve_revision(
        self, repository: Path, revision: str
    ) -> GitSha:
        path = self._require_session_child(repository, must_exist=True)
        result = await self._git(
            ("rev-parse", "--verify", f"{revision}^{{commit}}"),
            working_directory=path,
        )
        value = result.stdout_path.read_text(encoding="utf-8").strip()
        try:
            return self._require_git_sha(value)
        except ValueError as error:
            raise GitRepositoryError(
                f"Git returned an invalid commit SHA: {value!r}"
            ) from error

    async def push_branch(
        self,
        bare_repository: Path,
        branch_name: str,
        credential: GitCredentialSource,
    ) -> None:
        bare = self._require_session_child(bare_repository, must_exist=True)
        branch = self._require_branch(branch_name)
        with runtime_telemetry.span("git.push"):
            await self._git(
                (
                    "--git-dir",
                    str(bare),
                    "push",
                    "origin",
                    f"refs/heads/{branch}:refs/heads/{branch}",
                ),
                working_directory=self._session_root,
                credential=credential,
            )

    def exclude_runtime_path(
        self, bare_repository: Path, relative_path: str
    ) -> None:
        """Exclude generated runtime metadata from all linked worktrees."""

        bare = self._require_session_child(bare_repository, must_exist=True)
        if relative_path != ".serena/":
            raise ValueError("only the generated .serena path may be excluded")
        info = bare / "info"
        info.mkdir(parents=True, exist_ok=True)
        exclude = info / "exclude"
        existing = (
            exclude.read_text(encoding="utf-8").splitlines()
            if exclude.exists()
            else []
        )
        if relative_path not in existing:
            with exclude.open("a", encoding="utf-8", newline="\n") as output:
                if existing and exclude.stat().st_size:
                    output.write("\n")
                output.write(relative_path + "\n")

    async def remove_worktree(
        self, bare_repository: Path, worktree: Path
    ) -> None:
        bare = self._require_session_child(bare_repository, must_exist=True)
        tree = self._require_session_child(worktree, must_exist=True)
        with runtime_telemetry.span("git.worktree.remove"):
            await self._git(
                (
                    "--git-dir",
                    str(bare),
                    "worktree",
                    "remove",
                    "--force",
                    str(tree),
                ),
                working_directory=self._session_root,
            )
            await self._git(
                ("--git-dir", str(bare), "worktree", "prune"),
                working_directory=self._session_root,
            )

    async def _git(
        self,
        arguments: tuple[str, ...],
        *,
        working_directory: Path,
        credential: GitCredentialSource | None = None,
        environment_overrides: dict[str, str] | None = None,
    ) -> ProcessResult:
        environment = {
            "GIT_TERMINAL_PROMPT": "0",
            "GIT_CONFIG_NOSYSTEM": "1",
            "GIT_CONFIG_GLOBAL": os.devnull,
        }
        if environment_overrides:
            environment.update(environment_overrides)
        askpass: Path | None = None
        if credential is not None:
            current = credential() if callable(credential) else credential
            askpass = self._create_askpass()
            environment.update(
                {
                    "GIT_ASKPASS": str(askpass),
                    "AKOS_GIT_USERNAME": current.username,
                    "AKOS_GIT_SECRET": current.secret,
                }
            )

        try:
            result = await self._runner.run(
                ProcessSpec(
                    argv=("git", *arguments),
                    working_directory=working_directory,
                    timeout_seconds=self._timeout,
                    environment_overrides=environment,
                )
            )
        finally:
            if askpass is not None:
                askpass.unlink(missing_ok=True)

        if not result.succeeded:
            detail = result.stderr_path.read_text(
                encoding="utf-8", errors="replace"
            ).strip()
            raise GitRepositoryError(
                f"git {arguments[0]} failed with exit code "
                f"{result.exit_code}: {detail}"
            )
        return result

    def _create_askpass(self) -> Path:
        if os.name == "nt":
            path = self._session_root / "git-askpass.cmd"
            content = (
                "@echo off\r\n"
                "echo %1 | findstr /I \"Username\" >nul\r\n"
                "if %errorlevel%==0 (echo %AKOS_GIT_USERNAME%) "
                "else (echo %AKOS_GIT_SECRET%)\r\n"
            )
        else:
            path = self._session_root / "git-askpass.sh"
            content = (
                "#!/bin/sh\n"
                'case "$1" in\n'
                '  *Username*) printf "%s\\n" "$AKOS_GIT_USERNAME" ;;\n'
                '  *) printf "%s\\n" "$AKOS_GIT_SECRET" ;;\n'
                "esac\n"
            )
        path.write_text(content, encoding="utf-8", newline="")
        path.chmod(stat.S_IRUSR | stat.S_IWUSR | stat.S_IXUSR)
        return path

    def _require_session_child(
        self, path: Path, *, must_exist: bool
    ) -> Path:
        resolved = path.resolve(strict=must_exist)
        if not resolved.is_relative_to(self._session_root):
            raise ValueError("repository path must remain beneath session_root")
        if resolved == self._session_root:
            raise ValueError("repository path cannot equal session_root")
        return resolved

    @staticmethod
    def _require_branch(value: str) -> str:
        if (
            not _BRANCH_PATTERN.fullmatch(value)
            or value.startswith("-")
            or value.endswith(("/", ".", ".lock"))
            or ".." in value
            or "//" in value
            or "@{" in value
        ):
            raise ValueError(f"unsafe Git branch name: {value!r}")
        return value

    @staticmethod
    def _require_git_sha(value: str) -> GitSha:
        if not _GIT_SHA_PATTERN.fullmatch(value):
            raise ValueError(f"invalid full Git commit SHA: {value!r}")
        return cast(GitSha, value)
