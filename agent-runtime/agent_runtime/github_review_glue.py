"""Thin GitHub Actions glue around the provider-neutral CI review contract."""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Any, Protocol

from pydantic import ValidationError

from agent_runtime.ci_review import (
    CiDeterministicCommand,
    CiDeterministicValidation,
    CiReviewManifestV1,
    _write_atomic,
)
from agent_runtime.contracts.review import (
    CiReviewArtifactV1,
    INFORMATIONAL_REVIEW_MARKER,
)

_REPOSITORY_ID_PATTERN = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
_SHA_PATTERN = re.compile(r"^[0-9a-f]{40,64}$")
_MAXIMUM_EVIDENCE_CHARACTERS = 8_000


class HttpResponse(Protocol):
    status: int

    def read(self) -> bytes:
        ...

    def __enter__(self) -> "HttpResponse":
        ...

    def __exit__(self, *args: object) -> None:
        ...


class HttpOpener(Protocol):
    def open(self, request: urllib.request.Request, timeout: int) -> HttpResponse:
        ...


def normalize_github_context(
    *,
    repository_id: str,
    change_request_id: str,
    revision_sha: str,
    base_revision_sha: str,
    requirements: str,
    deterministic_result_path: Path,
    destination: Path,
) -> CiReviewManifestV1:
    _validate_github_identity(repository_id, change_request_id, revision_sha)
    if not _SHA_PATTERN.fullmatch(base_revision_sha):
        raise ValueError("base revision must be a full lowercase Git SHA")
    if not requirements.strip():
        raise ValueError("change-request requirements cannot be empty")
    try:
        validation = json.loads(
            deterministic_result_path.read_text(encoding="utf-8")
        )
        if validation.get("schemaVersion") != "1.0":
            raise ValueError("unsupported deterministic result schema")
        commands = [
            CiDeterministicCommand(
                name=command["name"],
                exit_code=command["exitCode"],
                timed_out=command["timedOut"],
                output=_read_command_evidence(command),
            )
            for command in validation["commands"]
        ]
        deterministic = CiDeterministicValidation(
            passed=validation["passed"],
            commands=commands,
        )
    except (KeyError, OSError, TypeError, json.JSONDecodeError, ValidationError) as error:
        raise ValueError("deterministic validation result is invalid") from error
    if not deterministic.passed:
        raise ValueError("deterministic validation did not pass")

    manifest = CiReviewManifestV1(
        source_control_provider="github",
        provider_repository_id=repository_id,
        change_request_id=change_request_id,
        revision_sha=revision_sha,
        base_revision_sha=base_revision_sha,
        requirements=requirements,
        deterministic_validation=deterministic,
    )
    _write_atomic(
        destination.resolve(),
        manifest.model_dump_json(by_alias=True, indent=2).encode("utf-8") + b"\n",
    )
    return manifest


def publish_github_review(
    *,
    artifact_path: Path,
    markdown_path: Path,
    repository_id: str,
    change_request_id: str,
    revision_sha: str,
    token: str,
    opener: HttpOpener | None = None,
    api_base_url: str = "https://api.github.com",
) -> str:
    _validate_github_identity(repository_id, change_request_id, revision_sha)
    if not token:
        raise ValueError("GH_TOKEN is required")
    try:
        artifact = CiReviewArtifactV1.model_validate_json(artifact_path.read_bytes())
        markdown = markdown_path.read_text(encoding="utf-8").strip()
    except (OSError, UnicodeError, ValidationError) as error:
        raise ValueError("review artifact is invalid") from error
    if (
        artifact.source.provider != "github"
        or artifact.source.provider_repository_id != repository_id
        or artifact.source.change_request_id != change_request_id
        or artifact.reviewed_revision_sha != revision_sha
    ):
        raise ValueError("review artifact does not match the GitHub change request")
    if INFORMATIONAL_REVIEW_MARKER in markdown:
        raise ValueError("review markdown must not contain the publication marker")
    if not markdown:
        raise ValueError("review markdown cannot be empty")

    client = opener or urllib.request.build_opener()
    pull = _request_json(
        client,
        "GET",
        f"{api_base_url}/repos/{repository_id}/pulls/{change_request_id}",
        token,
    )
    head = pull.get("head")
    if not isinstance(head, dict) or head.get("sha") != revision_sha:
        raise RuntimeError("GitHub change-request head no longer matches the review SHA")

    comments: list[dict[str, Any]] = []
    page = 1
    while True:
        batch = _request_json(
            client,
            "GET",
            (
                f"{api_base_url}/repos/{repository_id}/issues/"
                f"{change_request_id}/comments?per_page=100&page={page}"
            ),
            token,
        )
        if not isinstance(batch, list):
            raise RuntimeError("GitHub comments response is not an array")
        comments.extend(
            comment
            for comment in batch
            if isinstance(comment, dict)
            and isinstance(comment.get("body"), str)
            and INFORMATIONAL_REVIEW_MARKER in comment["body"]
        )
        if len(batch) < 100:
            break
        page += 1
        if page > 100:
            raise RuntimeError("GitHub comment pagination exceeded the safety bound")
    if len(comments) > 1:
        raise RuntimeError("multiple informational review comments already exist")

    body = (
        f"{INFORMATIONAL_REVIEW_MARKER}\n\n"
        f"Revision: `{revision_sha}`\n\n"
        f"{markdown}"
    )
    if comments:
        comment_id = comments[0].get("id")
        if not isinstance(comment_id, int) or comment_id <= 0:
            raise RuntimeError("existing GitHub review comment has an invalid id")
        response = _request_json(
            client,
            "PATCH",
            f"{api_base_url}/repos/{repository_id}/issues/comments/{comment_id}",
            token,
            {"body": body},
        )
        publication = "updated"
    else:
        response = _request_json(
            client,
            "POST",
            (
                f"{api_base_url}/repos/{repository_id}/issues/"
                f"{change_request_id}/comments"
            ),
            token,
            {"body": body},
        )
        publication = "created"
    if not isinstance(response, dict) or not isinstance(response.get("html_url"), str):
        raise RuntimeError("GitHub returned an invalid review-comment response")
    return publication


def _request_json(
    opener: HttpOpener,
    method: str,
    url: str,
    token: str,
    payload: dict[str, object] | None = None,
) -> Any:
    data = None if payload is None else json.dumps(payload).encode("utf-8")
    request = urllib.request.Request(
        url,
        data=data,
        method=method,
        headers={
            "Accept": "application/vnd.github+json",
            "Authorization": f"Bearer {token}",
            "User-Agent": "akos-fabric-ci-review",
            "X-GitHub-Api-Version": "2022-11-28",
            **({"Content-Type": "application/json"} if data is not None else {}),
        },
    )
    try:
        with opener.open(request, timeout=30) as response:
            return json.loads(response.read().decode("utf-8"))
    except (UnicodeError, json.JSONDecodeError, urllib.error.URLError) as error:
        raise RuntimeError(f"GitHub API {method} request failed") from error


def _read_command_evidence(command: dict[str, object]) -> str:
    output: list[str] = []
    remaining = _MAXIMUM_EVIDENCE_CHARACTERS
    for key in ("stdoutPath", "stderrPath"):
        raw_path = command.get(key)
        if not isinstance(raw_path, str) or not raw_path:
            raise ValueError(f"deterministic command lacks {key}")
        try:
            text = Path(raw_path).read_text(encoding="utf-8", errors="replace")
        except OSError as error:
            raise ValueError(f"cannot read deterministic command {key}") from error
        chunk = text[:remaining]
        if chunk:
            output.append(f"{key}:\n{chunk}")
            remaining -= len(chunk)
        if remaining == 0:
            break
    return "\n".join(output)[:_MAXIMUM_EVIDENCE_CHARACTERS]


def _validate_github_identity(
    repository_id: str, change_request_id: str, revision_sha: str
) -> None:
    if not _REPOSITORY_ID_PATTERN.fullmatch(repository_id):
        raise ValueError("GitHub repository id must be owner/name")
    if not change_request_id.isascii() or not change_request_id.isdigit():
        raise ValueError("GitHub change-request id must be a positive integer")
    if int(change_request_id) <= 0:
        raise ValueError("GitHub change-request id must be a positive integer")
    if not _SHA_PATTERN.fullmatch(revision_sha):
        raise ValueError("revision must be a full lowercase Git SHA")


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="GitHub CI-review pipeline glue.")
    subparsers = parser.add_subparsers(dest="operation", required=True)
    normalize = subparsers.add_parser("normalize")
    normalize.add_argument("--repository-id", required=True)
    normalize.add_argument("--change-request-id", required=True)
    normalize.add_argument("--revision", required=True)
    normalize.add_argument("--base-revision", required=True)
    normalize.add_argument("--requirements-file", required=True)
    normalize.add_argument("--deterministic-result", required=True)
    normalize.add_argument("--output", required=True)
    publish = subparsers.add_parser("publish")
    publish.add_argument("--artifact", required=True)
    publish.add_argument("--markdown", required=True)
    publish.add_argument("--repository-id", required=True)
    publish.add_argument("--change-request-id", required=True)
    publish.add_argument("--revision", required=True)
    return parser


def main() -> int:
    arguments = _parser().parse_args()
    try:
        if arguments.operation == "normalize":
            requirements = Path(arguments.requirements_file).read_text(encoding="utf-8")
            normalize_github_context(
                repository_id=arguments.repository_id,
                change_request_id=arguments.change_request_id,
                revision_sha=arguments.revision,
                base_revision_sha=arguments.base_revision,
                requirements=requirements,
                deterministic_result_path=Path(arguments.deterministic_result),
                destination=Path(arguments.output),
            )
        else:
            publication = publish_github_review(
                artifact_path=Path(arguments.artifact),
                markdown_path=Path(arguments.markdown),
                repository_id=arguments.repository_id,
                change_request_id=arguments.change_request_id,
                revision_sha=arguments.revision,
                token=os.environ.get("GH_TOKEN", ""),
            )
            print(publication)
        return 0
    except (OSError, RuntimeError, ValueError) as error:
        print(f"GitHub CI review glue failed: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
