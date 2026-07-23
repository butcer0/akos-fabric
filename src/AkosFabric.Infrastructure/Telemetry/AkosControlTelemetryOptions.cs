namespace AkosFabric.Infrastructure.Telemetry;

public sealed class AkosControlTelemetryOptions
{
    public Uri OtlpEndpoint { get; init; } =
        new("http://127.0.0.1:4317");

    public AkosOtlpProtocol Protocol { get; init; } =
        AkosOtlpProtocol.Grpc;

    public string? OtlpHeaders { get; init; }

    public string ServiceName { get; init; } =
        "akos-fabric-agent-control";

    public string ServiceVersion { get; init; } = "1.4.0";

    public void Validate()
    {
        if (!OtlpEndpoint.IsAbsoluteUri ||
            OtlpEndpoint.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(OtlpEndpoint.UserInfo) ||
            !string.IsNullOrEmpty(OtlpEndpoint.Query) ||
            !string.IsNullOrEmpty(OtlpEndpoint.Fragment))
        {
            throw new ArgumentException(
                "The OTLP endpoint must be an absolute HTTP or HTTPS URI without user information, query, or fragment.",
                nameof(OtlpEndpoint));
        }

        ValidateIdentifier(ServiceName, nameof(ServiceName));
        ValidateIdentifier(ServiceVersion, nameof(ServiceVersion));

        if (OtlpHeaders is not null &&
            (string.IsNullOrWhiteSpace(OtlpHeaders) ||
             OtlpHeaders.Any(character =>
                 character is '\0' or '\r' or '\n')))
        {
            throw new ArgumentException(
                "OTLP headers must use a non-empty single-line value when configured.",
                nameof(OtlpHeaders));
        }
    }

    public override string ToString() =>
        $"{nameof(AkosControlTelemetryOptions)} {{ OtlpEndpoint = {OtlpEndpoint}, Protocol = {Protocol}, OtlpHeaders = [REDACTED], ServiceName = {ServiceName}, ServiceVersion = {ServiceVersion} }}";

    private static void ValidateIdentifier(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 128 ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"{name} must be a non-empty telemetry identifier of at most 128 characters.",
                name);
        }
    }
}

public enum AkosOtlpProtocol
{
    Grpc,
    HttpProtobuf,
}
