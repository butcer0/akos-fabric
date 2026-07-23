[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = "High")]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet(
        "List",
        "Inspect",
        "Publish",
        "Retry",
        "Cancel",
        "ReprocessResult",
        "ContainerLogs",
        "RemoveOrphanContainer"
    )]
    [string] $Action,

    [Parameter(Mandatory = $true)]
    [ValidateNotNull()]
    [uri] $ApiBaseUri,

    [guid] $SessionId = [guid]::Empty,

    [ValidateRange(1, 200)]
    [int] $Limit = 50
)

$ErrorActionPreference = "Stop"

function Assert-SessionId {
    if ($SessionId -eq [guid]::Empty) {
        throw "-SessionId is required for action '$Action'."
    }
}

function Get-AuthorizationHeaders {
    $token = [Environment]::GetEnvironmentVariable(
        "AKOS_AGENT_CONTROL_TOKEN",
        "Process"
    )
    if ([string]::IsNullOrWhiteSpace($token)) {
        throw "Set AKOS_AGENT_CONTROL_TOKEN in the process environment."
    }

    return @{
        Authorization = "Bearer $token"
    }
}

function Invoke-AgentControl {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet("Get", "Post")]
        [string] $Method,

        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    $base = $ApiBaseUri.AbsoluteUri.TrimEnd("/")
    return Invoke-RestMethod `
        -Method $Method `
        -Uri "$base$Path" `
        -Headers (Get-AuthorizationHeaders)
}

function Get-ManagedContainerName {
    Assert-SessionId
    $name = "agent-$($SessionId.ToString('D'))"
    $labelJson = & docker inspect `
        --type container `
        --format "{{json .Config.Labels}}" `
        $name
    if ($LASTEXITCODE -ne 0) {
        throw "Docker could not inspect container '$name'."
    }

    $labels = $labelJson | ConvertFrom-Json -AsHashtable
    if (
        $labels["agent.system"] -ne "autonomous-engineering" -or
        $labels["agent.repository-session-id"] -ne $SessionId.ToString("D")
    ) {
        throw "Container '$name' is not the managed container for this session."
    }

    return $name
}

switch ($Action) {
    "List" {
        Invoke-AgentControl `
            -Method Get `
            -Path "/api/repository-sessions?limit=$Limit"
    }
    "Inspect" {
        Assert-SessionId
        $session = Invoke-AgentControl `
            -Method Get `
            -Path "/api/repository-sessions/$($SessionId.ToString('D'))"
        $items = Invoke-AgentControl `
            -Method Get `
            -Path "/api/repository-sessions/$($SessionId.ToString('D'))/items"
        [pscustomobject]@{
            Session = $session
            Items = $items
        }
    }
    "Publish" {
        Assert-SessionId
        Invoke-AgentControl `
            -Method Post `
            -Path "/api/repository-sessions/$($SessionId.ToString('D'))/publish"
    }
    "Retry" {
        Assert-SessionId
        Invoke-AgentControl `
            -Method Post `
            -Path "/api/repository-sessions/$($SessionId.ToString('D'))/retry"
    }
    "Cancel" {
        Assert-SessionId
        Invoke-AgentControl `
            -Method Post `
            -Path "/api/repository-sessions/$($SessionId.ToString('D'))/cancel"
    }
    "ReprocessResult" {
        Assert-SessionId
        Invoke-AgentControl `
            -Method Post `
            -Path (
                "/api/repository-sessions/" +
                "$($SessionId.ToString('D'))/reprocess-result"
            )
    }
    "ContainerLogs" {
        $containerName = Get-ManagedContainerName
        & docker logs $containerName
        if ($LASTEXITCODE -ne 0) {
            throw "Docker could not read logs for '$containerName'."
        }
    }
    "RemoveOrphanContainer" {
        $containerName = Get-ManagedContainerName
        if (
            $PSCmdlet.ShouldProcess(
                $containerName,
                "Force-remove the labeled repository-session container"
            )
        ) {
            & docker rm --force $containerName
            if ($LASTEXITCODE -ne 0) {
                throw "Docker could not remove '$containerName'."
            }
        }
    }
}
