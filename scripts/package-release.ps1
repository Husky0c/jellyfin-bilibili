[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('10.10', '10.11', '12')]
    [string]$JellyfinAbi,
    [string]$DotNet = 'dotnet',
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'Jellyfin.Plugin.BiliArchive/Jellyfin.Plugin.BiliArchive.csproj'
$manifestPath = Join-Path $repoRoot 'meta.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$projectText = Get-Content -LiteralPath $project -Raw
$projectVersion = [regex]::Match($projectText, '<Version>([^<]+)</Version>').Groups[1].Value
if ($manifest.version -ne $projectVersion) {
    throw "Plugin version mismatch: meta.json=$($manifest.version), project=$projectVersion"
}

$targetAbi = switch ($JellyfinAbi) {
    '10.10' { '10.10.7.0' }
    '10.11' { '10.11.0.0' }
    '12' { '12.0.0.0' }
}
$publishDir = Join-Path $repoRoot "dist/publish-jf$JellyfinAbi"
$packageDir = Join-Path $repoRoot 'dist/packages'
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null

if (-not $NoRestore) {
    & $DotNet restore $project --configfile (Join-Path $repoRoot 'NuGet.Config') "-p:JellyfinAbi=$JellyfinAbi"
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed for Jellyfin $JellyfinAbi" }
}
& $DotNet publish $project -c Release --no-restore -o $publishDir "-p:JellyfinAbi=$JellyfinAbi"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for Jellyfin $JellyfinAbi" }

$manifest.targetAbi = $targetAbi
$generatedMeta = Join-Path $publishDir 'meta.json'
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $generatedMeta -Encoding utf8
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $publishDir 'LICENSE') -Force

# Only managed, architecture-independent assemblies are packaged. Never copy
# the publish directory's runtimes/ tree into Jellyfin's plugin loader.
$names = @(
    'Jellyfin.Plugin.BiliArchive.dll',
    'Jellyfin.Plugin.BiliArchive.deps.json',
    'QRCoder.dll',
    'System.Drawing.Common.dll',
    'Microsoft.Win32.SystemEvents.dll',
    'meta.json',
    'LICENSE'
)
$files = foreach ($name in $names) {
    $file = Join-Path $publishDir $name
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing package file: $file" }
    $file
}
Add-Type -AssemblyName System.Reflection.Metadata
foreach ($name in @($names | Where-Object { $_.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase) })) {
    $stream = [System.IO.File]::OpenRead((Join-Path $publishDir $name))
    $pe = $null
    try {
        $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        $cor = $pe.PEHeaders.CorHeader
        if ($null -eq $cor -or
            $pe.PEHeaders.CoffHeader.Machine -ne [System.Reflection.PortableExecutable.Machine]::I386 -or
            ($cor.Flags -band [System.Reflection.PortableExecutable.CorFlags]::ILOnly) -eq 0 -or
            ($cor.Flags -band [System.Reflection.PortableExecutable.CorFlags]::Requires32Bit) -ne 0) {
            throw "Not an AnyCPU managed assembly: $name"
        }
    } finally {
        if ($null -ne $pe) { $pe.Dispose() }
        $stream.Dispose()
    }
}
$deps = Get-Content -LiteralPath (Join-Path $publishDir 'Jellyfin.Plugin.BiliArchive.deps.json') -Raw | ConvertFrom-Json
if ($deps.runtimeTarget.name -match '[/\\]') {
    throw "RID-specific output is not suitable for the AnyCPU package: $($deps.runtimeTarget.name)"
}

$archiveName = "BiliArchive_$($manifest.version)_Jellyfin-$JellyfinAbi-anycpu.zip"
$archive = Join-Path $packageDir $archiveName
Compress-Archive -LiteralPath $files -DestinationPath $archive -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($archive)
try {
    $actual = @($zip.Entries | ForEach-Object FullName | Sort-Object)
    $expected = @($names | Sort-Object)
    if (@(Compare-Object $expected $actual).Count -ne 0) {
        throw "Unexpected ZIP entries: $($actual -join ', ')"
    }
} finally {
    $zip.Dispose()
}
$sha = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash.ToLowerInvariant()
"$sha  $archiveName" | Set-Content -LiteralPath "$archive.sha256" -Encoding ascii
Write-Host "Created $archive (targetAbi $targetAbi; SHA256 $sha)"
