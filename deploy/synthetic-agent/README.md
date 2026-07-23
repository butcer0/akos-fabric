# Synthetic agent

This image is a deterministic lifecycle probe for the Akos Fabric host. It
reads the JSON file named by `TASK_MANIFEST`, creates one `blocked` result item
for every manifest work item, writes the session result to `RESULT_PATH`
atomically, and exits.

It does not clone repositories, call models, open credentials, or contact
external services. A `blocked` item cannot be confused with a branch produced
by the real runtime. Each item records zero-valued planner, coder, and judge
model usage with the provider and model copied from the manifest, as required
by the result schema.

The container runs as UID/GID `10001:10001`, matching the session identity in
the architecture specification. The result writer creates `result.json.tmp`,
flushes and fsyncs it, renames it to `result.json` on the same filesystem, and
fsyncs the session directory.

## Build

From the repository root:

```powershell
docker build `
  --tag akos-fabric/synthetic-agent:development `
  deploy/synthetic-agent
```

## Run the example

Create an isolated session directory, copy the non-secret example manifest, and
mount only that directory:

```powershell
$session = New-Item -ItemType Directory -Path "$env:TEMP/akos-synthetic-session" -Force
Copy-Item deploy/synthetic-agent/example-manifest.json "$session/manifest.json"

docker run --rm --init `
  --user 10001:10001 `
  --mount "type=bind,src=$($session.FullName),dst=/run/agent" `
  --env TASK_MANIFEST=/run/agent/manifest.json `
  --env RESULT_PATH=/run/agent/result.json `
  akos-fabric/synthetic-agent:development

Get-Content "$session/result.json"
```

On Linux, ensure the bind-mounted session directory and manifest are accessible
to UID/GID `10001:10001`, as the production host does. Docker Desktop handles
the Windows bind-mount projection.

The example manifest is structurally valid against
`schemas/agent-session-manifest-v1.schema.json`. The emitted result is shaped
for `schemas/agent-session-result-v1.schema.json`; authoritative schema
validation remains the responsibility of the host control plane.

## Focused tests

From this directory:

```powershell
python -m unittest -v test_synthetic_agent.py
```

The tests cover the required zero-valued model-usage contract, atomic rename,
and rejection of credentials embedded in a clone URL.
