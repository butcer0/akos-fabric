[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [Uri]$ApiBaseUri,

    [Uri]$IdentityAuthority = $(if ($env:AKOS_FABRIC_IDENTITY_AUTHORITY) {
        [Uri]$env:AKOS_FABRIC_IDENTITY_AUTHORITY
    } else {
        [Uri]"https://localhost:7101"
    }),

    [string]$RepositoryProfile,

    [string[]]$JiraKeys = @(),

    [string]$SessionRoot,

    [string]$ComposeEnvironmentFile,

    [string]$SyntheticImageReference =
        "akos-fabric/synthetic-agent:development",

    [int]$TimeoutSeconds = 120,

    [string]$EvidencePath,

    [switch]$PreflightOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

$repoRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot "..\.."))
$composeFile = Join-Path $repoRoot "deploy\development\compose.yaml"
if ([string]::IsNullOrWhiteSpace($ComposeEnvironmentFile)) {
    $localEnvironmentFile =
        Join-Path $repoRoot "deploy\development\.env"
    $exampleEnvironmentFile =
        Join-Path $repoRoot "deploy\development\.env.example"
    $ComposeEnvironmentFile = if (Test-Path -LiteralPath $localEnvironmentFile) {
        $localEnvironmentFile
    } else {
        $exampleEnvironmentFile
    }
}
$ComposeEnvironmentFile =
    [IO.Path]::GetFullPath($ComposeEnvironmentFile)

$checks = [Collections.Generic.List[object]]::new()
$blockers = [Collections.Generic.List[string]]::new()
$startedAt = [DateTimeOffset]::UtcNow

function Add-Check {
    param(
        [string]$Name,
        [bool]$Passed,
        [string]$Detail
    )

    $checks.Add([ordered]@{
        name = $Name
        passed = $Passed
        detail = $Detail
    })
    $state = if ($Passed) { "PASS" } else { "BLOCKED" }
    Write-Host ("[{0}] {1}: {2}" -f $state, $Name, $Detail)
    if (-not $Passed) {
        $blockers.Add(("{0}: {1}" -f $Name, $Detail))
    }
}

function Get-ExceptionDetail {
    param([Exception]$Exception)

    $current = $Exception
    while ($null -ne $current.InnerException) {
        $current = $current.InnerException
    }
    $current.Message
}

function Invoke-Docker {
    param([string[]]$Arguments)

    $output = @(& docker @Arguments 2>&1)
    [ordered]@{
        ExitCode = $LASTEXITCODE
        Output = ($output -join [Environment]::NewLine).Trim()
    }
}

function Read-DotEnv {
    param([string]$Path)

    $values = @{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match '^\s*#' -or
            $line -notmatch '^\s*([^=]+)=(.*)$') {
            continue
        }

        $values[$Matches[1].Trim()] = $Matches[2].Trim()
    }
    $values
}

function New-HttpClient {
    $handler = [Net.Http.HttpClientHandler]::new()
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(15)
    $client
}

function Invoke-Http {
    param(
        [Net.Http.HttpClient]$Client,
        [string]$Method,
        [Uri]$Uri,
        [string]$BearerToken,
        [string]$JsonBody
    )

    $request = [Net.Http.HttpRequestMessage]::new(
        [Net.Http.HttpMethod]::new($Method),
        $Uri)
    try {
        if (-not [string]::IsNullOrWhiteSpace($BearerToken)) {
            $request.Headers.Authorization =
                [Net.Http.Headers.AuthenticationHeaderValue]::new(
                    "Bearer",
                    $BearerToken)
        }
        if (-not [string]::IsNullOrEmpty($JsonBody)) {
            $request.Content = [Net.Http.StringContent]::new(
                $JsonBody,
                [Text.Encoding]::UTF8,
                "application/json")
        }

        $response = $Client.SendAsync($request).GetAwaiter().GetResult()
        try {
            [ordered]@{
                StatusCode = [int]$response.StatusCode
                Body = $response.Content.ReadAsStringAsync().
                    GetAwaiter().GetResult()
            }
        } finally {
            $response.Dispose()
        }
    } finally {
        $request.Dispose()
    }
}

function Get-Token {
    param([string[]]$Scopes)

    $tokenScript = Join-Path $repoRoot "scripts\get-dev-agent-token.ps1"
    $token = & $tokenScript `
        -Authority $IdentityAuthority.AbsoluteUri `
        -Scopes $Scopes
    if ([string]::IsNullOrWhiteSpace([string]$token)) {
        throw "Development token acquisition failed."
    }
    ([string]$token).Trim()
}

function Get-RabbitPublishCount {
    param(
        [Net.Http.HttpClient]$Client,
        [hashtable]$Environment
    )

    $user = $Environment["AKOS_RABBITMQ_USER"]
    $password = $Environment["AKOS_RABBITMQ_PASSWORD"]
    $port = $Environment["AKOS_RABBITMQ_MANAGEMENT_PORT"]
    if ([string]::IsNullOrWhiteSpace($user) -or
        [string]::IsNullOrWhiteSpace($password) -or
        [string]::IsNullOrWhiteSpace($port)) {
        throw "RabbitMQ settings are incomplete in the Compose environment file."
    }

    $bytes = [Text.Encoding]::UTF8.GetBytes(("{0}:{1}" -f $user, $password))
    $token = [Convert]::ToBase64String($bytes)
    $request = [Net.Http.HttpRequestMessage]::new(
        [Net.Http.HttpMethod]::Get,
        "http://127.0.0.1:$port/api/exchanges/%2F/agent.work")
    try {
        $request.Headers.Authorization =
            [Net.Http.Headers.AuthenticationHeaderValue]::new("Basic", $token)
        $response = $Client.SendAsync($request).GetAwaiter().GetResult()
        try {
            if (-not $response.IsSuccessStatusCode) {
                throw "RabbitMQ management API returned HTTP $([int]$response.StatusCode)."
            }
            $body = $response.Content.ReadAsStringAsync().
                GetAwaiter().GetResult() | ConvertFrom-Json
            if ($null -eq $body.message_stats -or
                $null -eq $body.message_stats.publish_in) {
                return [long]0
            }
            [long]$body.message_stats.publish_in
        } finally {
            $response.Dispose()
        }
    } finally {
        $request.Dispose()
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
}

function Invoke-PostgresJson {
    param(
        [hashtable]$Environment,
        [string]$Query
    )

    $user = $Environment["AKOS_POSTGRES_USER"]
    $database = $Environment["AKOS_POSTGRES_DB"]
    if ([string]::IsNullOrWhiteSpace($user) -or
        [string]::IsNullOrWhiteSpace($database)) {
        throw "PostgreSQL settings are incomplete in the Compose environment file."
    }

    $result = Invoke-Docker @(
        "compose",
        "--env-file", $ComposeEnvironmentFile,
        "--file", $composeFile,
        "exec", "-T",
        "postgres",
        "psql",
        "--username", $user,
        "--dbname", $database,
        "--tuples-only",
        "--no-align",
        "--command", $Query)
    if ($result.ExitCode -ne 0) {
        throw "PostgreSQL evidence query failed: $($result.Output)"
    }
    $result.Output | ConvertFrom-Json
}

function Write-Evidence {
    param(
        [string]$Outcome,
        [object]$Session,
        [object]$Database,
        [object]$Result,
        [object]$DockerEvents,
        [Nullable[long]]$RabbitBefore,
        [Nullable[long]]$RabbitAfter
    )

    if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
        return
    }

    $absoluteEvidencePath = [IO.Path]::GetFullPath($EvidencePath)
    $parent = Split-Path -Parent $absoluteEvidencePath
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }
    [ordered]@{
        schemaVersion = 1
        stage = "Stage 1 - Control spine"
        outcome = $Outcome
        startedAt = $startedAt.ToString("O")
        completedAt = [DateTimeOffset]::UtcNow.ToString("O")
        apiBaseUri = $ApiBaseUri.AbsoluteUri
        identityAuthority = $IdentityAuthority.AbsoluteUri
        repositoryProfile = $RepositoryProfile
        jiraKeys = $JiraKeys
        checks = $checks
        blockers = $blockers
        session = $Session
        database = $Database
        result = $Result
        rabbitMq = [ordered]@{
            publishCountBefore = $RabbitBefore
            publishCountAfter = $RabbitAfter
        }
        dockerEvents = $DockerEvents
    } | ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath $absoluteEvidencePath -Encoding UTF8
}

foreach ($command in @("docker", "dotnet")) {
    $found = $null -ne (Get-Command $command -ErrorAction SilentlyContinue)
    $detail = if ($found) { "available" } else { "not found on PATH" }
    Add-Check "command:$command" $found $detail
}

foreach ($file in @(
    $composeFile,
    $ComposeEnvironmentFile,
    (Join-Path $repoRoot "schemas\agent-session-manifest-v1.schema.json"),
    (Join-Path $repoRoot "schemas\agent-session-result-v1.schema.json"),
    (Join-Path $repoRoot "scripts\get-dev-agent-token.ps1")
)) {
    Add-Check "file:$([IO.Path]::GetFileName($file))" `
        (Test-Path -LiteralPath $file -PathType Leaf) $file
}

$environment = @{}
if (Test-Path -LiteralPath $ComposeEnvironmentFile -PathType Leaf) {
    $environment = Read-DotEnv $ComposeEnvironmentFile
}

$dockerVersion = Invoke-Docker @("version", "--format", "{{.Server.Version}}")
$dockerDetail = if ($dockerVersion.ExitCode -eq 0) {
    "server $($dockerVersion.Output)"
} else {
    $dockerVersion.Output
}
Add-Check "docker-engine" ($dockerVersion.ExitCode -eq 0) $dockerDetail

$compose = Invoke-Docker @(
    "compose",
    "--env-file", $ComposeEnvironmentFile,
    "--file", $composeFile,
    "ps",
    "--format", "json")
$healthyServices = @()
if ($compose.ExitCode -eq 0 -and $compose.Output) {
    try {
        $healthyServices = @(
            $compose.Output -split "\r?\n" |
                Where-Object { $_ } |
                ForEach-Object { $_ | ConvertFrom-Json } |
                Where-Object {
                    $_.Service -in @("postgres", "rabbitmq", "otel-lgtm") -and
                    $_.State -eq "running" -and
                    $_.Health -eq "healthy"
                } |
                Select-Object -ExpandProperty Service)
    } catch {
        $healthyServices = @()
    }
}
$servicesDetail = if ($healthyServices.Count -eq 3) {
    "PostgreSQL, RabbitMQ, and LGTM are running and healthy"
} else {
    "expected three healthy Compose services; found $($healthyServices.Count)"
}
Add-Check "development-services" `
    ($healthyServices.Count -eq 3) `
    $servicesDetail

$image = Invoke-Docker @(
    "image", "inspect", $SyntheticImageReference,
    "--format", "{{index .RepoDigests 0}}")
$syntheticImageDigest = $null
if ($image.ExitCode -eq 0 -and
    $image.Output -match '@(sha256:[0-9a-f]{64})$') {
    $syntheticImageDigest = $Matches[1]
}
$imageDetail = if ($image.ExitCode -eq 0) {
    "$SyntheticImageReference is present ($($image.Output))"
} else {
    "build deploy/synthetic-agent before running acceptance"
}
Add-Check "synthetic-image" (
    $image.ExitCode -eq 0 -and
    -not [string]::IsNullOrWhiteSpace($syntheticImageDigest)
) $imageDetail

$http = New-HttpClient
try {
    try {
        $identity = Invoke-Http $http "GET" (
            [Uri]::new($IdentityAuthority, ".well-known/openid-configuration")
        ) $null $null
        Add-Check "identity-discovery" ($identity.StatusCode -eq 200) (
            "HTTP $($identity.StatusCode)")
    } catch {
        Add-Check "identity-discovery" $false (
            Get-ExceptionDetail $_.Exception)
    }

    try {
        $live = Invoke-Http $http "GET" (
            [Uri]::new($ApiBaseUri, "health/live")
        ) $null $null
        Add-Check "agent-control-live" ($live.StatusCode -eq 200) (
            "HTTP $($live.StatusCode)")
    } catch {
        Add-Check "agent-control-live" $false (
            Get-ExceptionDetail $_.Exception)
    }

    try {
        $migration = Invoke-PostgresJson $environment @"
SELECT json_build_object(
    'repositorySessionTable', to_regclass('public.repository_session'),
    'workItemRunTable', to_regclass('public.work_item_run'),
    'ledgerEntryTable', to_regclass('public.ledger_entry')
);
"@
        $migrationReady =
            $migration.repositorySessionTable -eq "repository_session" -and
            $migration.workItemRunTable -eq "work_item_run" -and
            $migration.ledgerEntryTable -eq "ledger_entry"
        Add-Check "postgres-migration" $migrationReady (
            $migration | ConvertTo-Json -Compress)
    } catch {
        Add-Check "postgres-migration" $false (
            Get-ExceptionDetail $_.Exception)
    }

    try {
        $rabbitPreflightCount = Get-RabbitPublishCount $http $environment
        Add-Check "rabbitmq-topology" $true (
            "agent.work exchange is queryable; publish_in=$rabbitPreflightCount")
    } catch {
        Add-Check "rabbitmq-topology" $false (
            Get-ExceptionDetail $_.Exception)
    }

    if ($PreflightOnly) {
        $outcome = if ($blockers.Count -eq 0) {
            "preflight_passed"
        } else {
            "blocked"
        }
        Write-Evidence $outcome $null $null $null $null $null $null
        if ($blockers.Count -gt 0) {
            Write-Host (
                "Stage-1 preflight is blocked:`n - " +
                ($blockers -join "`n - ")) -ForegroundColor Red
            exit 2
        }
        Write-Host "Stage-1 preflight passed."
        exit 0
    }

    if ([string]::IsNullOrWhiteSpace($RepositoryProfile)) {
        throw "RepositoryProfile is required unless PreflightOnly is used."
    }
    if ($JiraKeys.Count -eq 0) {
        throw "At least one JiraKeys value is required unless PreflightOnly is used."
    }
    if ([string]::IsNullOrWhiteSpace($SessionRoot) -or
        -not [IO.Path]::IsPathFullyQualified($SessionRoot)) {
        throw "SessionRoot must be the absolute root configured for the API host."
    }
    if ($TimeoutSeconds -lt 10) {
        throw "TimeoutSeconds must be at least 10."
    }
    if ($blockers.Count -gt 0) {
        throw (
            "Stage-1 execution cannot start because preflight is blocked:`n - " +
            ($blockers -join "`n - "))
    }

    $sessionEndpoint =
        [Uri]::new($ApiBaseUri, "api/repository-sessions")
    $requestBody = @{
        repositoryProfile = $RepositoryProfile
        jiraKeys = $JiraKeys
    } | ConvertTo-Json -Compress

    $unauthenticated = Invoke-Http $http "POST" $sessionEndpoint $null $requestBody
    Add-Check "api-rejects-unauthenticated" `
        ($unauthenticated.StatusCode -eq 401) `
        "HTTP $($unauthenticated.StatusCode)"

    $readToken = Get-Token @("agent.sessions.read")
    $forbidden = Invoke-Http $http "POST" $sessionEndpoint $readToken $requestBody
    Add-Check "api-rejects-missing-create-scope" `
        ($forbidden.StatusCode -eq 403) `
        "HTTP $($forbidden.StatusCode)"
    $readToken = $null

    $token = Get-Token @(
        "agent.sessions.read",
        "agent.sessions.create",
        "agent.sessions.operate")
    $rabbitBefore = Get-RabbitPublishCount $http $environment
    $apiRequestedAt = [DateTimeOffset]::UtcNow
    $created = Invoke-Http $http "POST" $sessionEndpoint $token $requestBody
    if ($created.StatusCode -ne 201) {
        $safeBody = if ($created.Body.Length -gt 1000) {
            $created.Body.Substring(0, 1000)
        } else {
            $created.Body
        }
        throw "Authenticated session creation returned HTTP $($created.StatusCode): $safeBody"
    }
    $session = $created.Body | ConvertFrom-Json
    $sessionId = [Guid]$session.id
    Add-Check "authenticated-session-create" $true (
        "HTTP 201; repositorySessionId=$sessionId")
    Add-Check "synthetic-image-selected" `
        ($session.imageReference -eq $SyntheticImageReference) `
        "API selected '$($session.imageReference)'"
    Add-Check "synthetic-image-digest-selected" `
        ($session.imageDigest -eq $syntheticImageDigest) `
        "API selected '$($session.imageDigest)'; local digest is '$syntheticImageDigest'"

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 500
        $read = Invoke-Http $http "GET" (
            [Uri]::new($ApiBaseUri, "api/repository-sessions/$sessionId")
        ) $token $null
        if ($read.StatusCode -ne 200) {
            throw "Session read returned HTTP $($read.StatusCode)."
        }
        $session = $read.Body | ConvertFrom-Json
    } while (
        $session.status -notin @("completed", "failed", "cancelled") -and
        [DateTimeOffset]::UtcNow -lt $deadline)
    $token = $null

    Add-Check "session-completed" ($session.status -eq "completed") (
        "terminal status '$($session.status)'")
    Add-Check "container-identity-recorded" (
        -not [string]::IsNullOrWhiteSpace([string]$session.containerName) -and
        -not [string]::IsNullOrWhiteSpace([string]$session.containerId)
    ) "container name and ID are present in the API record"

    $rabbitAfter = Get-RabbitPublishCount $http $environment
    Add-Check "rabbitmq-published" ($rabbitAfter -ge ($rabbitBefore + 1)) (
        "agent.work publish_in changed from $rabbitBefore to $rabbitAfter")

    $database = Invoke-PostgresJson $environment @"
SELECT json_build_object(
    'sessionStatus', rs.status,
    'requestedBySubject', rs.requested_by_subject,
    'requestedByClientId', rs.requested_by_client_id,
    'requestedByTokenId', rs.requested_by_token_id,
    'requestPayload', rs.request_payload,
    'itemCount', (
        SELECT count(*) FROM work_item_run wi
        WHERE wi.repository_session_id = rs.id
    ),
    'jiraKeys', (
        SELECT json_agg(wi.jira_key ORDER BY wi.sequence_number)
        FROM work_item_run wi
        WHERE wi.repository_session_id = rs.id
    ),
    'ledgerTypes', (
        SELECT json_agg(le.entry_type ORDER BY le.id)
        FROM ledger_entry le
        WHERE le.repository_session_id = rs.id
    )
)
FROM repository_session rs
WHERE rs.id = '$($sessionId.ToString("D"))'::uuid;
"@
    $expectedLedger = @(
        "SESSION_CREATED",
        "SESSION_PUBLISHED",
        "SESSION_STARTING",
        "SESSION_STARTED",
        "SESSION_COMPLETED")
    $actualLedger = @($database.ledgerTypes)
    Add-Check "postgres-session-and-items" (
        $database.sessionStatus -eq "completed" -and
        [int]$database.itemCount -eq $JiraKeys.Count -and
        (@($database.jiraKeys) -join ",") -eq ($JiraKeys -join ",")
    ) "completed session and $($database.itemCount) ordered work-item rows"
    Add-Check "ledger-lifecycle" (
        @($expectedLedger | Where-Object { $_ -notin $actualLedger }).Count -eq 0
    ) ($actualLedger -join ",")
    Add-Check "caller-identity-persisted" (
        -not [string]::IsNullOrWhiteSpace(
            [string]$database.requestedBySubject) -and
        -not [string]::IsNullOrWhiteSpace(
            [string]$database.requestedByClientId) -and
        -not [string]::IsNullOrWhiteSpace(
            [string]$database.requestedByTokenId)
    ) "subject, client_id, and jti are persisted from the access token"

    $sessionDirectory = Join-Path $SessionRoot $sessionId.ToString("D")
    $manifestPath = Join-Path $sessionDirectory "manifest.json"
    $resultPath = Join-Path $sessionDirectory "result.json"
    $credentialPath =
        Join-Path $sessionDirectory "source-control-credential.json"
    Add-Check "session-artifacts-retained" (
        (Test-Path -LiteralPath $manifestPath -PathType Leaf) -and
        (Test-Path -LiteralPath $resultPath -PathType Leaf)
    ) $sessionDirectory
    Add-Check "credential-removed" `
        (-not (Test-Path -LiteralPath $credentialPath)) `
        "source-control credential file is absent after cleanup"

    $result = $null
    if (Test-Path -LiteralPath $resultPath -PathType Leaf) {
        $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
        Add-Check "synthetic-result-identity" (
            [Guid]$result.repositorySessionId -eq $sessionId -and
            $result.status -eq "completed" -and
            @($result.items).Count -eq $JiraKeys.Count -and
            @($result.items | Where-Object { $_.status -ne "blocked" }).Count -eq 0
        ) "valid result was accepted by the host; all synthetic items are blocked"
    }

    $apiCompletedAt = [DateTimeOffset]::UtcNow
    $events = Invoke-Docker @(
        "events",
        "--since", $apiRequestedAt.ToString("O"),
        "--until", $apiCompletedAt.ToString("O"),
        "--filter", "label=agent.system=autonomous-engineering",
        "--filter", "label=agent.repository-session-id=$($sessionId.ToString("D"))",
        "--format", "{{json .}}")
    $eventLines = @(
        $events.Output -split "\r?\n" |
            Where-Object { $_ -and $_ -match $sessionId.ToString("D") })
    Add-Check "docker-lifecycle-events" (
        $events.ExitCode -eq 0 -and
        @($eventLines | Where-Object { $_ -match '"Action":"start"' }).Count -gt 0 -and
        @($eventLines | Where-Object { $_ -match '"Action":"die"' }).Count -gt 0
    ) "$($eventLines.Count) session-scoped Docker events captured"

    $remainingContainer = Invoke-Docker @(
        "ps", "--all", "--quiet",
        "--filter", "name=^agent-$($sessionId.ToString("D"))$")
    Add-Check "container-disposed" (
        $remainingContainer.ExitCode -eq 0 -and
        [string]::IsNullOrWhiteSpace($remainingContainer.Output)
    ) "deterministic session container is absent after result processing"

    $outcome = if ($blockers.Count -eq 0) { "passed" } else { "failed" }
    Write-Evidence $outcome $session $database $result $eventLines `
        $rabbitBefore $rabbitAfter
    if ($blockers.Count -gt 0) {
        throw (
            "Stage-1 acceptance failed:`n - " +
            ($blockers -join "`n - "))
    }
    Write-Host "Stage-1 control-spine acceptance passed."
} catch {
    Write-Evidence "blocked_or_failed" $null $null $null $null $null $null
    throw
} finally {
    $http.Dispose()
}
