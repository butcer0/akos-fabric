"""Provider-neutral role-agent construction and typed completion capture."""

from .completion import (
    CompletionCapture,
    CompletionNotSubmittedError,
    DuplicateCompletionError,
)
from .roles import (
    AgentRole,
    MetadataEventSink,
    RoleAgentFactory,
    RoleConstructionRequest,
    RoleConversation,
    RoleEventMetadata,
    RoleRuntimeDependencyError,
    SerenaStdioConfiguration,
)

__all__ = [
    "AgentRole",
    "CompletionCapture",
    "CompletionNotSubmittedError",
    "DuplicateCompletionError",
    "MetadataEventSink",
    "RoleAgentFactory",
    "RoleConstructionRequest",
    "RoleConversation",
    "RoleEventMetadata",
    "RoleRuntimeDependencyError",
    "SerenaStdioConfiguration",
]
