<#
  Genera la versión de lanzamiento para Windows:
    dist\Purgine-Setup-v<versión>.exe   instalador (es el propio Purgine.exe autocontenido de un solo archivo)
    dist\SHA256SUMS.txt                 suma de comprobación
  La versión sale de Directory.Build.props (VersionPrefix + VersionSuffix).
  Uso:  powershell -ExecutionPolicy Bypass -File build\release.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$group = $props.Project.PropertyGroup
$version = $group.VersionPrefix
if ($group.VersionSuffix) { $version = "$version-$($group.VersionSuffix)" }
Write-Host "Purgine $version"

$project = Join-Path $root 'src\Purgine\Purgine.csproj'
$publishDir = Join-Path $root 'src\Purgine\bin\publish\win-x64'
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $project -p:PublishProfile=win-x64
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falló ($LASTEXITCODE)" }

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
Get-ChildItem $dist -Filter 'Purgine-Setup-*.exe' | Remove-Item -Force
$setup = Join-Path $dist "Purgine-Setup-v$version.exe"
Copy-Item (Join-Path $publishDir 'Purgine.exe') $setup -Force

$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path -Leaf $setup)" | Set-Content (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii
$size = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host "Instalador: $setup ($size MB)"
Write-Host "SHA-256:    $hash"
