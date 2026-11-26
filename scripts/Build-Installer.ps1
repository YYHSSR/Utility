[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $InnoCompiler,

    [Parameter(Mandatory)]
    [string] $HardwareLauncherCompiler
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$inno = (Get-Item -LiteralPath $InnoCompiler).FullName
$gcc = (Get-Item -LiteralPath $HardwareLauncherCompiler).FullName
$appProject = Join-Path $repo 'src\Utility.App\Utility.App.csproj'
[xml] $app = Get-Content -LiteralPath $appProject
$version = $app.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
$output = Join-Path $repo 'publish'
$staging = Join-Path $output ('setup-staging-' + [guid]::NewGuid().ToString('N'))

try {
    New-Item -Path $staging -ItemType Directory | Out-Null
    $publishArgs = @('publish', $appProject, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        "-p:Version=$version", "-p:HardwareLauncherCompiler=$gcc", '-o', $staging)
    dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Utility publish failed with exit code $LASTEXITCODE."
    }

    foreach ($file in @('Utility.exe', 'Utility.ElevatedWorker.exe', 'Utility.HardwareLauncher.exe', 'hostfxr.dll', 'coreclr.dll', 'PresentationFramework.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $staging $file) -PathType Leaf)) {
            throw "Missing installer payload file $file."
        }
    }

    $dependencies = Get-Content -Raw -LiteralPath (Join-Path $staging 'Utility.deps.json') | ConvertFrom-Json
    $assetsPath = Join-Path (Split-Path -Parent $appProject) 'obj\project.assets.json'
    $assets = Get-Content -Raw -LiteralPath $assetsPath | ConvertFrom-Json
    $notices = Join-Path $staging 'ThirdPartyNotices'
    foreach ($dependency in $dependencies.libraries.PSObject.Properties) {
        if ($dependency.Value.type -notin @('package', 'runtimepack')) {
            continue
        }

        $packageId, $packageVersion = $dependency.Name -replace '^runtimepack\.' -split '/', 2
        $packagePath = Join-Path $packageId.ToLowerInvariant() $packageVersion
        $packageDir = $assets.packageFolders.PSObject.Properties.Name | ForEach-Object {
            Join-Path $_ $packagePath
        } | Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
        if (-not $packageDir) {
            throw "Could not locate NuGet package $packageId $packageVersion for its redistribution notices."
        }

        $files = Get-ChildItem -LiteralPath $packageDir -File |
            Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)(\.[^.]+)?$' }
        if ($files) {
            $noticeDir = Join-Path $notices $packageId
            New-Item -Path $noticeDir -ItemType Directory -Force | Out-Null
            $files | Copy-Item -Destination $noticeDir
        }
    }

    $definition = Join-Path $repo 'installer\Utility.iss'
    & $inno /Q "/DAppVersion=$version" "/DPayloadDir=$staging" "/O$output" $definition
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
    }

    $installer = Join-Path $output "Utility-$version-win-x64-Setup.exe"
    $hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path -Leaf $installer)" | Set-Content -LiteralPath "$installer.sha256" -Encoding ascii
    Get-Item -LiteralPath $installer, "$installer.sha256"
}
finally {
    if (Test-Path -LiteralPath $staging) {
        $resolvedStaging = (Resolve-Path -LiteralPath $staging).ProviderPath
        $resolvedOutput = (Resolve-Path -LiteralPath $output).ProviderPath
        if ((Split-Path -Parent $resolvedStaging) -eq $resolvedOutput -and
            (Split-Path -Leaf $resolvedStaging) -match '^setup-staging-[0-9a-f]{32}$') {
            Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
        }
    }
}
