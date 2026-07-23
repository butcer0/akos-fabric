# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:10.0.302-noble@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664 AS dotnet

FROM python:3.13.11-slim-bookworm@sha256:20080e807bfc404f8450b185cf0fc95d553462673598549613735f70a5b4d5d0

ARG AGENT_UID=10001
ARG AGENT_GID=10001
ARG UV_VERSION=0.11.29

LABEL org.opencontainers.image.title="Akos Fabric repository agent"
LABEL org.opencontainers.image.description="Pinned OpenHands and Serena execution image for the akos-fabric repository profile"
LABEL org.opencontainers.image.source="https://github.com/butcer0/akos-fabric"

ENV DEBIAN_FRONTEND=noninteractive \
    DOTNET_ROOT=/usr/share/dotnet \
    NUGET_PACKAGES=/cache/nuget \
    UV_CACHE_DIR=/cache/uv \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PATH="/opt/agent-runtime/.venv/bin:/opt/serena-runtime/.venv/bin:/usr/share/dotnet:${PATH}"

RUN apt-get update \
    && apt-get install --yes --no-install-recommends \
       build-essential=12.9 \
       ca-certificates=20230311+deb12u1 \
       curl=7.88.1-10+deb12u15 \
       git=1:2.39.5-0+deb12u3 \
       git-lfs=3.3.0-1+deb12u1 \
       jq=1.6-2.1+deb12u2 \
       libicu72=72.1-3+deb12u1 \
       libkrb5-3=1.20.1-2+deb12u5 \
       libssl3=3.0.20-1~deb12u2 \
       ripgrep=13.0.0-4+b2 \
       tini=0.19.0-1+b3 \
       tmux=3.3a-3 \
       zlib1g=1:1.2.13.dfsg-1 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=dotnet /usr/share/dotnet /usr/share/dotnet

RUN python -m pip install \
      --disable-pip-version-check \
      --no-cache-dir \
      "uv==${UV_VERSION}" \
    && groupadd --gid "${AGENT_GID}" agent \
    && useradd \
      --uid "${AGENT_UID}" \
      --gid "${AGENT_GID}" \
      --create-home \
      --shell /usr/sbin/nologin \
      agent \
    && install --directory --owner="${AGENT_UID}" --group="${AGENT_GID}" \
      /cache/nuget \
      /cache/uv \
      /opt/agent-runtime \
      /opt/repository-profile \
      /opt/serena-runtime \
      /run/agent \
      /workspace

COPY --chown=10001:10001 agent-runtime/pyproject.toml agent-runtime/uv.lock /opt/agent-runtime/
USER 10001:10001
RUN UV_PROJECT_ENVIRONMENT=/opt/agent-runtime/.venv \
      uv sync \
      --project /opt/agent-runtime \
      --frozen \
      --extra openhands \
      --no-install-project

COPY --chown=10001:10001 agent-runtime/agent_runtime/ /opt/agent-runtime/agent_runtime/
COPY --chown=10001:10001 agent-runtime/tests/compatibility/inspect_openhands.py /opt/agent-runtime/compatibility/inspect_openhands.py
COPY --chown=10001:10001 schemas/ /opt/agent-runtime/schemas/
RUN UV_PROJECT_ENVIRONMENT=/opt/agent-runtime/.venv \
      uv sync \
      --project /opt/agent-runtime \
      --frozen \
      --extra openhands

COPY --chown=10001:10001 serena-runtime/pyproject.toml serena-runtime/uv.lock /opt/serena-runtime/
RUN UV_PROJECT_ENVIRONMENT=/opt/serena-runtime/.venv \
      uv sync \
      --project /opt/serena-runtime \
      --frozen \
      --no-install-project

COPY --chown=10001:10001 repository-profiles/runtime/akos-fabric.yml /opt/repository-profile/profile.yml
COPY --chown=10001:10001 repository-profiles/akos-fabric.serena-project.yml /opt/repository-profile/serena-project.yml

RUN dotnet --version \
    && python --version \
    && uv --version \
    && git --version \
    && git lfs version \
    && /opt/agent-runtime/.venv/bin/python \
      /opt/agent-runtime/compatibility/inspect_openhands.py \
    && /opt/serena-runtime/.venv/bin/serena --version \
    && /opt/serena-runtime/.venv/bin/serena --help >/dev/null \
    && /opt/serena-runtime/.venv/bin/serena project create --help >/dev/null \
    && /opt/serena-runtime/.venv/bin/serena project index --help >/dev/null \
    && /opt/serena-runtime/.venv/bin/serena start-mcp-server --help >/dev/null

WORKDIR /opt/agent-runtime

ENTRYPOINT ["/usr/bin/tini", "--", "python", "-m", "agent_runtime"]
