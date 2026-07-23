# Akos Fabric

Autonomous software engineering fabric for turning backlogs, such as Jira, stories and defects into validated, review-ready code changes.

Akos Fabric provisions a disposable workspace for a real repository, then runs independent planning, coding, verification, and judging agents against the full codebase. Agents work with the repository, Git history, build system, tests, and Serena-powered semantic code intelligence—not extracted snippets or synthetic source context.

A single repository session can process a bounded set of backlog items before disposal. Each item still receives its own worktree, branch, candidate commit, and independent review.

**No snippet-only coding. No model-asserted test results. No autonomous merge.**

## What You Get

| Capability | What it does |
|---|---|
| Backlog-driven work | Selects eligible stories and bugs from a configurable backlog site and workflow. |
| Disposable repository sessions | Creates an isolated environment with the real repository and its required engineering tools. |
| Planner agent | Investigates the issue, relevant code, tests, dependencies, and likely implementation path. |
| Coder agent | Makes the code change directly in an isolated worktree. |
| Deterministic verification | Runs the repository's required build, test, lint, and validation commands outside the model's authority. |
| Independent judge | Reviews a clean checkout of the exact candidate commit without inheriting the coder's conversation. |
| Change-request delivery | Pushes accepted branches and opens a provider-native change request for CI and human review. |
| Execution ledger | Records repository sessions, work-item outcomes, verification, revisions, and delivery state. |
| Full observability | Correlates queue, container, agent, model, command, Git, and delivery activity through OpenTelemetry and Grafana. |

## How It Works

```text
Backlog story or bug
    -> repository session
    -> isolated Git worktree
    -> planner agent
    -> coder agent
    -> deterministic build and tests
    -> candidate commit
    -> independent judge agent
    -> change request
    -> CI validation and AI review
    -> human approval
```

The repository is cloned once per session. Each backlog item is then processed in its own worktree and branch, preventing one task's uncommitted state from leaking into another while avoiding repeated repository setup.

## What Makes Akos Fabric Different

### Repository-native agents

Agents operate inside a real development environment with the complete codebase, terminal access, Git, language-server-backed code intelligence, and the repository's own build and test tooling.

### Independent judgment

The judge reviews the exact candidate commit from a clean checkout. It does not share the coder's conversation or mutable workspace.

### Deterministic engineering controls

Required verification is executed by the harness. A model cannot claim that a build or test passed, override a failed command, or deliver a different commit from the one that was judged.

### Isolated work items, efficient sessions

A repository session may process several backlog items, but each item keeps its own branch, worktree, agent conversations, commit, and change request.

### Provider-neutral source control

Standard Git handles repository operations. Provider adapters handle credentials and collaboration features such as creating a pull request or merge request. GitHub is the first adapter; GitLab and other Git-compatible platforms can be added without changing the core workflow.

### Configurable model provider

The model integration is replaceable. Gemini 3.6 Flash is the initial provider configuration.

### Human-controlled delivery

Akos Fabric prepares, validates, and reviews code changes. It does not autonomously merge them.

## Security and Observability

OAuth 2.0, signed JWT access tokens, scoped authorization, short-lived provider credentials, and strict secret handling protect the control plane and external integrations.

OpenTelemetry captures the operational path from backlog intake through agent execution and change-request delivery. Grafana provides traces, metrics, logs, and dashboards without exporting source code, prompts, model responses, or credentials.

## Prerequisites

| Dependency | What it is used for |
|---|---|
| Docker Desktop | Disposable repository sessions and local supporting services. |
| Git | Repository checkout, worktrees, branches, commits, and delivery. |
| backlog | Source of stories and defects. |
| Source-control provider | Repository hosting and change-request collaboration. |
| PostgreSQL | Repository-session and work-item execution ledger. |
| RabbitMQ | Repository-session dispatch. |
| Grafana / OpenTelemetry | Traces, metrics, logs, and operational dashboards. |
| Gemini API access | Initial model provider. |

## Setup

Clone the repository:

```bash
git clone <repository-url>
cd akos-fabric
```

Configure the required local values outside source control:

```text
GEMINI_API_KEY
backlog site and project settings
source-control provider credentials
local identity secrets
repository-session storage path
```

Start the supporting services from `deploy/`, then launch Akos Fabric using the repository's development launch configuration.

Repository-specific source-control, backlog, container, model, and verification settings live under `repository-profiles/`. The first profile targets Akos Fabric itself and requires no supplemental repositories.

## Usage

1. Mark a backlog story or bug as eligible for agent execution.
2. Akos Fabric creates or groups the work into a repository session.
3. The planner, coder, verifier, and judge process each item independently.
4. Accepted candidates are pushed and opened as change requests.
5. CI and the first-round AI review run against the actual change-request revision.
6. A human reviews and decides whether to merge.

## License

See [LICENSE](LICENSE).
