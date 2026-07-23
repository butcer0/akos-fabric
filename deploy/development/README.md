# Akos Fabric development services

This Compose project runs the local supporting services required by the Akos
Fabric control plane:

- PostgreSQL `18.4-bookworm`
- RabbitMQ `4.3.2-management`
- Grafana OpenTelemetry LGTM `0.27.1`

The images use exact version tags, persistent named volumes, and health checks.
Every published port is bound to `127.0.0.1`; these services are not exposed on
other host interfaces.

## Start

From the repository root in PowerShell:

```powershell
Copy-Item deploy/development/.env.example deploy/development/.env
docker compose --env-file deploy/development/.env `
  --file deploy/development/compose.yaml config --quiet
docker compose --env-file deploy/development/.env `
  --file deploy/development/compose.yaml up --detach --wait
docker compose --env-file deploy/development/.env `
  --file deploy/development/compose.yaml ps
```

The example passwords are public, local-development-only defaults. Change them
in the ignored `.env` file if this workstation is shared. Do not use them in a
shared or production deployment.

## Endpoints

Using the example ports:

| Service | Endpoint |
| --- | --- |
| PostgreSQL | `Host=127.0.0.1;Port=5432;Database=akos_fabric;Username=akos_fabric` |
| RabbitMQ AMQP | `amqp://127.0.0.1:5672` |
| RabbitMQ management | `http://127.0.0.1:15672` |
| Grafana | `http://127.0.0.1:3000` |
| OTLP/gRPC | `http://127.0.0.1:4317` |
| OTLP/HTTP | `http://127.0.0.1:4318` |

Use the RabbitMQ and Grafana credentials from the local `.env` file. An
application running directly on the host can use the loopback endpoints above.
Containers attached to the `akos-fabric-development_supporting-services`
network should use service names (`postgres`, `rabbitmq`, and `otel-lgtm`) and
container ports instead.

## Grafana dashboard

Compose provisions the `Akos Fabric Agent Control` dashboard in the
`Akos Fabric` folder without replacing LGTM's built-in dashboards. The
dashboard uses the shipped `prometheus` datasource UID and only exact metrics
declared by the control plane.

The control plane records session creation/duration, validated work-item
outcomes, per-role model usage, verification failures, judge dispositions, and
newly created change requests. The v1 result contract has no per-item
timestamps, so that duration panel remains empty rather than reporting
invented timing data. Required Section 32 alert and dashboard gaps are recorded in
`docs/operations/observability-readiness.md`; no unsupported alert rules are
provisioned.

## Stop and inspect

```powershell
docker compose --env-file deploy/development/.env `
  --file deploy/development/compose.yaml logs --follow
docker compose --env-file deploy/development/.env `
  --file deploy/development/compose.yaml down
```

`down` retains the named volumes. To deliberately erase all local database,
queue, and telemetry data, run:

```powershell
docker compose --env-file deploy/development/.env `
  --file deploy/development/compose.yaml down --volumes
```

## Synthetic execution image

The sibling `deploy/synthetic-agent` directory contains a no-credentials,
no-repository test image for exercising the host's Docker lifecycle and result
processing before the real agent runtime is connected. See its README for build
and run commands.
