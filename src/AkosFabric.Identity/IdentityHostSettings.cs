namespace AkosFabric.Identity;

public sealed record IdentityHostSettings(
    string Mode,
    string IssuerUri,
    bool RequireHttpsMetadata,
    string? DevelopmentClientSecret,
    string? DevelopmentSigningKeyPath,
    string? DevelopmentDataProtectionKeyPath)
{
    public const string DevelopmentMode = "Development";
    public const string ProductionMode = "Production";

    public static IdentityHostSettings FromConfiguration(IConfiguration configuration) =>
        new(
            configuration["Identity:Mode"] ?? string.Empty,
            configuration["Identity:IssuerUri"] ?? string.Empty,
            configuration.GetValue<bool>("Identity:RequireHttpsMetadata"),
            configuration["Identity:Development:ClientSecret"],
            configuration["Identity:Development:SigningKeyPath"],
            configuration["Identity:Development:DataProtectionKeyPath"]);
}
