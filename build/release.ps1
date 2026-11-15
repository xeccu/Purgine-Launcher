<#
  Genera la versión de lanzamiento para Windows:
    dist\Purgine-Setup-Windows.exe   instalador (el propio Purgine.exe autocontenido de un solo archivo)
    dist\SHA256SUMS.txt              suma de comprobación
  El nombre del instalador es fijo en todas las versiones, así el enlace
  https://github.com/xeccu/Purgine-Launcher/releases/latest/download/Purgine-Setup-Windows.exe siempre lleva a la última.

  Versión: la de -Version (p. ej. "v0.1.1-alpha", la usa GitHub Actions con la etiqueta) o, si no se indica, la de
  Directory.Build.props (VersionPrefix + VersionSuffix).

  Uso:  powershell -ExecutionPolicy Bypass -File build\release.ps1 [-Version v0.1.1-alpha]
#>
param([string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$publishArgs = @()
if ($Version) {
    $v = $Version.TrimStart('v', 'V')
    if ($v -notmatch '^(\d+\.\d+\.\d+)(?:-([0-9A-Za-z.-]+))?$') { throw "Versión no válida: $Version (se espera v1.2.3 o v1.2.3-alpha)" }
    $prefix = $Matches[1]; $suffix = $Matches[2]
    $publishArgs += "-p:VersionPrefix=$prefix"
    $publishArgs += "-p:VersionSuffix=$suffix"
    $version = $v
} else {
    [xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
    $group = $props.Project.PropertyGroup
    $version = $group.VersionPrefix
    if ($group.VersionSuffix) { $version = "$version-$($group.VersionSuffix)" }
}
Write-Host "Purgine $version"

$project = Join-Path $root 'src\Purgine\Purgine.csproj'
$publishDir = Join-Path $root 'src\Purgine\bin\publish\win-x64'
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $project -p:PublishProfile=win-x64 @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falló ($LASTEXITCODE)" }

$exe = Join-Path $publishDir 'Purgine.exe'
$built = (Get-Item $exe).VersionInfo.ProductVersion
if ($built -ne $version) { throw "El ejecutable dice ser la versión '$built' y se esperaba '$version'." }

$dist = Join-Path $root 'dist'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force $dist | Out-Null
$setup = Join-Path $dist 'Purgine-Setup-Windows.exe'
Copy-Item $exe $setup -Force

$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  Purgine-Setup-Windows.exe" | Set-Content (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii
$size = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host "Instalador: $setup ($size MB, versión $version)"
Write-Host "SHA-256:    $hash"
