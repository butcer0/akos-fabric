namespace AkosFabric.Application.SourceControl.Models;

public sealed record SourceControlCredential(
    string Username,
    string Secret,
    DateTimeOffset? ExpiresAt)
{
    public override string ToString() =>
        $"{nameof(SourceControlCredential)} {{ Username = {Username}, Secret = [REDACTED], ExpiresAt = {ExpiresAt:O} }}";
}
