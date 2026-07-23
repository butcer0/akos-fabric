# Akos Fabric Serena runtime

Serena runs in its own locked Python environment inside the repository image.
This is deliberate: `serena-agent==1.5.3` has exact dependency pins that are
incompatible with the `openhands-tools==1.36.1` graph.

The repository image installs:

- `agent-runtime/uv.lock` into the agent/OpenHands environment; and
- `serena-runtime/uv.lock` into the Serena stdio MCP environment.

Roles start the Serena executable from the second environment and communicate
only through MCP stdio. No Serena package is imported into the agent process.
