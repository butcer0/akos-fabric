FROM python:3.14.6-slim-bookworm

LABEL org.opencontainers.image.title="Akos Fabric synthetic agent"
LABEL org.opencontainers.image.description="Deterministic development probe for Akos Fabric container lifecycle testing"
LABEL org.opencontainers.image.source="https://github.com/butcer0/akos-fabric"

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

RUN groupadd --gid 10001 agent \
    && useradd \
        --uid 10001 \
        --gid 10001 \
        --no-create-home \
        --home-dir /nonexistent \
        --shell /usr/sbin/nologin \
        agent \
    && install --directory --owner=10001 --group=10001 /opt/synthetic-agent

COPY --chown=10001:10001 synthetic_agent.py /opt/synthetic-agent/synthetic_agent.py

USER 10001:10001
WORKDIR /opt/synthetic-agent

ENTRYPOINT ["python", "/opt/synthetic-agent/synthetic_agent.py"]
