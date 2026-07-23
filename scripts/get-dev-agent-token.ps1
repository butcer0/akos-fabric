[CmdletBinding()]
param(
    [string]$Authority = $(if ($env:AKOS_FABRIC_IDENTITY_AUTHORITY) {
        $env:AKOS_FABRIC_IDENTITY_AUTHORITY
    } else {
        "https://localhost:7101"
    }),
    [string]$ClientId = "agent-control-development-operator",
    [string[]]$Scopes = @(
        "agent.sessions.read",
        "agent.sessions.create",
        "agent.sessions.operate"
    ),
    [switch]$AllowHttp
)

$ErrorActionPreference = "Stop"
$ClientSecret = if ($env:AKOS_FABRIC_DEVELOPMENT_CLIENT_SECRET) {
    $env:AKOS_FABRIC_DEVELOPMENT_CLIENT_SECRET
} else {
    $env:Identity__Development__ClientSecret
}

if ([string]::IsNullOrWhiteSpace($ClientSecret)) {
    throw "Set AKOS_FABRIC_DEVELOPMENT_CLIENT_SECRET or Identity__Development__ClientSecret."
}

$authorityUri = [Uri]$Authority
if (-not $authorityUri.IsAbsoluteUri) {
    throw "Authority must be an absolute URI."
}

if ($authorityUri.Scheme -ne "https" -and -not $AllowHttp) {
    throw "Refusing non-HTTPS discovery. Pass -AllowHttp only for a trusted local development endpoint."
}

$discoveryUri = "$($Authority.TrimEnd('/'))/.well-known/openid-configuration"
$discovery = Invoke-RestMethod -Method Get -Uri $discoveryUri
if ([string]::IsNullOrWhiteSpace($discovery.token_endpoint)) {
    throw "IdentityServer discovery metadata did not contain a token endpoint."
}

$tokenResponse = Invoke-RestMethod `
    -Method Post `
    -Uri $discovery.token_endpoint `
    -ContentType "application/x-www-form-urlencoded" `
    -Body @{
        grant_type    = "client_credentials"
        client_id     = $ClientId
        client_secret = $ClientSecret
        scope         = ($Scopes -join " ")
    }

if ([string]::IsNullOrWhiteSpace($tokenResponse.access_token)) {
    throw "IdentityServer did not return an access token."
}

$tokenResponse.access_token
