[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string] $RootDirectory,

    [ValidateRange(1, 36500)]
    [int] $RetentionDays = 7,

    [switch] $Delete
)

$ErrorActionPreference = 'Stop'

function Test-ReparsePoint {
    param([Parameter(Mandatory = $true)] [System.IO.FileSystemInfo] $Entry)

    return ($Entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
}

function Assert-SafeTree {
    param(
        [Parameter(Mandatory = $true)] [System.IO.DirectoryInfo] $Directory,
        [Parameter(Mandatory = $true)] [string] $RootPrefix,
        [Parameter(Mandatory = $true)] [System.StringComparison] $Comparison
    )

    $pending = [System.Collections.Generic.Stack[System.IO.DirectoryInfo]]::new()
    $pending.Push($Directory)
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        foreach ($entry in $current.EnumerateFileSystemInfos()) {
            $fullPath = [System.IO.Path]::GetFullPath($entry.FullName)
            if (-not $fullPath.StartsWith($RootPrefix, $Comparison)) {
                throw "Session entry escapes the configured root: $fullPath"
            }

            if (Test-ReparsePoint -Entry $entry) {
                throw "Session cleanup refuses to traverse a reparse point: $fullPath"
            }

            if ($entry -is [System.IO.DirectoryInfo]) {
                $pending.Push($entry)
            }
        }
    }
}

function Remove-CredentialFile {
    param([Parameter(Mandatory = $true)] [string] $SessionDirectory)

    $credentialPath = [System.IO.Path]::Combine(
        $SessionDirectory,
        'source-control-credential.json')
    if (-not [System.IO.File]::Exists($credentialPath)) {
        return
    }

    $credential = [System.IO.FileInfo]::new($credentialPath)
    if (Test-ReparsePoint -Entry $credential) {
        throw "Credential deletion refuses a reparse point: $credentialPath"
    }

    $stream = [System.IO.FileStream]::new(
        $credentialPath,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None,
        4096,
        [System.IO.FileOptions]::WriteThrough)
    try {
        $zeros = [byte[]]::new([Math]::Min(81920, [Math]::Max(1, $stream.Length)))
        $remaining = $stream.Length
        while ($remaining -gt 0) {
            $count = [int][Math]::Min($zeros.Length, $remaining)
            $stream.Write($zeros, 0, $count)
            $remaining -= $count
        }

        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }

    [System.IO.File]::Delete($credentialPath)
}

$isWindowsPlatform =
    [System.Environment]::OSVersion.Platform -eq
    [System.PlatformID]::Win32NT
$isFullyQualified = if ($isWindowsPlatform) {
    $RootDirectory -match '^(?:[A-Za-z]:[\\/]|\\\\[^\\/]+[\\/][^\\/]+)'
}
else {
    $RootDirectory.StartsWith('/', [System.StringComparison]::Ordinal)
}
if (-not $isFullyQualified) {
    throw 'RootDirectory must be an absolute path.'
}

$trimCharacters = [char[]]@(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar)
$root = [System.IO.Path]::GetFullPath($RootDirectory).TrimEnd(
    $trimCharacters)
$filesystemRoot = [System.IO.Path]::GetPathRoot($root).TrimEnd(
    $trimCharacters)
if ($root -eq $filesystemRoot) {
    throw 'RootDirectory cannot be a filesystem root.'
}

if (-not [System.IO.Directory]::Exists($root)) {
    return
}

$rootInfo = [System.IO.DirectoryInfo]::new($root)
if (Test-ReparsePoint -Entry $rootInfo) {
    throw "Session root cannot be a reparse point: $root"
}

$comparison = if ($isWindowsPlatform) {
    [System.StringComparison]::OrdinalIgnoreCase
}
else {
    [System.StringComparison]::Ordinal
}
$separator = [System.IO.Path]::DirectorySeparatorChar
$rootPrefix = "$root$separator"
$evaluatedAt = [DateTimeOffset]::UtcNow
$retention = [TimeSpan]::FromDays($RetentionDays)

foreach ($directory in $rootInfo.EnumerateDirectories()) {
    $sessionId = [Guid]::Empty
    if (-not [Guid]::TryParseExact(
            $directory.Name,
            'D',
            [ref] $sessionId) -or
        $sessionId -eq [Guid]::Empty) {
        continue
    }

    $canonicalName = $sessionId.ToString('D')
    $nameMatches = [string]::Equals(
        $directory.Name,
        $canonicalName,
        $comparison)
    if (-not $nameMatches) {
        continue
    }

    $candidatePath = [System.IO.Path]::GetFullPath($directory.FullName)
    if (-not $candidatePath.StartsWith($rootPrefix, $comparison)) {
        throw "Session directory escapes the configured root: $candidatePath"
    }

    if (Test-ReparsePoint -Entry $directory) {
        throw "Session directory cannot be a reparse point: $candidatePath"
    }

    Assert-SafeTree `
        -Directory $directory `
        -RootPrefix $rootPrefix `
        -Comparison $comparison

    $retentionStartedAt = [DateTimeOffset]::new(
        $directory.LastWriteTimeUtc,
        [TimeSpan]::Zero)
    $deleteAfter = $retentionStartedAt.Add($retention)
    $isDue = $deleteAfter -le $evaluatedAt
    $action = if (-not $isDue) {
        'retain'
    }
    elseif (-not $Delete) {
        'would-delete'
    }
    else {
        'delete'
    }

    [pscustomobject]@{
        repositorySessionId = $sessionId
        directoryPath = $candidatePath
        retentionStartedAt = $retentionStartedAt
        deleteAfter = $deleteAfter
        isDue = $isDue
        action = $action
    }

    if ($isDue -and $Delete -and $PSCmdlet.ShouldProcess(
            $candidatePath,
            'Delete expired session artifacts')) {
        Remove-CredentialFile -SessionDirectory $candidatePath
        Assert-SafeTree `
            -Directory $directory `
            -RootPrefix $rootPrefix `
            -Comparison $comparison
        Remove-Item -LiteralPath $candidatePath -Recurse -Force
    }
}
