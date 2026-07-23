"""Safe materialization of the baked Serena project template per worktree."""

from __future__ import annotations

from pathlib import Path

import yaml
from pydantic import ConfigDict, Field

from .contracts._base import CamelContract
from .runtime_profile import RuntimeRepositoryProfile


class SerenaProjectTemplate(CamelContract):
    model_config = ConfigDict(extra="allow")

    project_name: str = Field(min_length=1)
    languages: list[str] = Field(min_length=1)
    ls_specific_settings: dict[str, object]


def materialize_serena_project(
    profile: RuntimeRepositoryProfile,
    worktree: Path,
    *,
    project_suffix: str,
) -> Path:
    """Validate the baked template and write worktree-local `.serena/project.yml`.

    The caller must configure Git's per-repository exclude for `.serena/`; this
    generated runtime metadata is never candidate source.
    """

    source = Path(profile.serena.project_configuration)
    if not source.is_absolute():
        raise ValueError("Serena projectConfiguration must be an absolute path")
    try:
        source = source.resolve(strict=True)
        payload = yaml.safe_load(source.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, yaml.YAMLError) as error:
        raise FileNotFoundError("Serena project configuration is unavailable") from error
    if not isinstance(payload, dict):
        raise ValueError("Serena project configuration must be a YAML object")
    template = SerenaProjectTemplate.model_validate(payload)
    if template.languages != profile.languages:
        raise ValueError(
            "Serena project languages do not match the repository profile"
        )

    root = worktree.resolve(strict=True)
    target_directory = root / ".serena"
    target_directory.mkdir(parents=True, exist_ok=True)
    target = target_directory / "project.yml"
    if target.resolve(strict=False).parent != target_directory.resolve(strict=True):
        raise ValueError("Serena project path escapes the active worktree")

    rendered = template.model_dump(mode="python", by_alias=False)
    rendered["project_name"] = f"{profile.id}-{project_suffix}"
    content = yaml.safe_dump(
        rendered,
        allow_unicode=True,
        default_flow_style=False,
        sort_keys=False,
    )
    if target.exists():
        existing = yaml.safe_load(target.read_text(encoding="utf-8"))
        if existing != rendered:
            raise ValueError(
                "repository contains a conflicting .serena/project.yml"
            )
        return target
    target.write_text(content, encoding="utf-8", newline="\n")
    return target
