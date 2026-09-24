# Builds the self-contained release exe and the Nexus upload zip.
#   powershell -ExecutionPolicy Bypass -File build_release.ps1
# Output: publish\Prismforge.exe, Release\Prismforge_v<version>.zip (for users)
#         and Release\Prismforge_v<version>_source.zip (source code)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (Get-Process Prismforge -ErrorAction SilentlyContinue) {
    throw 'Close Prismforge first; the running exe cannot be overwritten.'
}

[xml]$proj = Get-Content Prismforge.csproj
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version

dotnet publish Prismforge.csproj -c Release -o publish -nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

$stage = Join-Path $env:TEMP "Prismforge_$version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory $stage | Out-Null
Copy-Item publish\Prismforge.exe $stage
Copy-Item README.md (Join-Path $stage 'README.txt')
Copy-Item CHANGELOG.md (Join-Path $stage 'CHANGELOG.txt')
Copy-Item LICENSE.txt $stage

New-Item -ItemType Directory -Force Release | Out-Null
$zip = "Release\Prismforge_v$version.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Remove-Item $stage -Recurse -Force

# Source package: everything needed to build, without build output, logs or IDE state.
$srcZip = "Release\Prismforge_v$version`_source.zip"
if (Test-Path $srcZip) { Remove-Item $srcZip }
$exclude = '^(bin|obj|publish|Release|logs|\.vs|\{.*\})(\\|$)'
$root = (Get-Location).Path
$files = Get-ChildItem -Recurse -File | Where-Object { $_.FullName.Substring($root.Length + 1) -notmatch $exclude }
$srcStage = Join-Path $env:TEMP "Prismforge_src_$version"
if (Test-Path $srcStage) { Remove-Item $srcStage -Recurse -Force }
foreach ($f in $files) {
    $dest = Join-Path $srcStage $f.FullName.Substring($root.Length + 1)
    New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
    Copy-Item $f.FullName $dest
}
Compress-Archive -Path (Join-Path $srcStage '*') -DestinationPath $srcZip
Remove-Item $srcStage -Recurse -Force

$exe = Get-Item publish\Prismforge.exe
"Version  : $version"
"Exe      : $($exe.FullName) ($([math]::Round($exe.Length / 1MB, 1)) MB)"
"Zip      : $((Get-Item $zip).FullName) ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
"Source   : $((Get-Item $srcZip).FullName) ($([math]::Round((Get-Item $srcZip).Length / 1KB)) KB)"
