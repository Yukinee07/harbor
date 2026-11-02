# Builds dist\Harbor-Setup-<version>.exe: one file that installs Harbor on a PC with nothing else on it.
#
#   powershell -ExecutionPolicy Bypass -File build\make-installer.ps1
#
# The version and the credit come from src\Directory.Build.props.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$out = Join-Path $PSScriptRoot 'out'
$app = Join-Path $out 'app'
$stub = Join-Path $out 'stub'
$setup = Join-Path $out 'setup'
$zip = Join-Path $out 'Harbor.zip'
$dist = Join-Path $repo 'dist'
$setupProject = Join-Path $repo 'src\HarborSetup\HarborSetup.csproj'
$appProject = Join-Path $repo 'src\Harbor\Harbor.csproj'

[xml]$props = Get-Content (Join-Path $repo 'src\Directory.Build.props')
$version = $props.Project.PropertyGroup.Version
"Building Harbor $version"

function Run($exe) {
    & $exe @args
    if ($LASTEXITCODE -ne 0) { throw "$exe failed with exit code $LASTEXITCODE" }
}

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory $out | Out-Null

# 1. The app, carrying its own copy of .NET so friends don't need to install anything.
Run dotnet publish $appProject -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:DebugType=None -p:DebugSymbols=false -o $app -v q -nologo

# Only used to debug crashes with developer tools, and never loaded in normal use.
foreach ($unused in 'createdump.exe', 'mscordaccore.dll', 'mscordbi.dll', 'Microsoft.DiaSymReader.Native.amd64.dll') {
    $p = Join-Path $app $unused
    if (Test-Path $p) { Remove-Item -Force $p }
}
Get-ChildItem $app -Filter 'mscordaccore_*.dll' | Remove-Item -Force

# 2. License texts for the .NET copy being shipped, from the exact runtime packs used.
$licenses = Join-Path $app 'licenses'
New-Item -ItemType Directory $licenses | Out-Null
$packs = Join-Path $env:USERPROFILE '.nuget\packages'
foreach ($pack in 'microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64') {
    $folder = Get-ChildItem (Join-Path $packs $pack) | Sort-Object { [version]$_.Name } | Select-Object -Last 1
    foreach ($file in 'LICENSE', 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT') {
        $source = Join-Path $folder.FullName $file
        if (Test-Path $source) { Copy-Item $source (Join-Path $licenses ($pack.Split('.')[1] + '-' + [IO.Path]::GetFileNameWithoutExtension($file) + '.txt')) }
    }
}

# 3. The installer with nothing inside it is the uninstaller.
Run dotnet build $setupProject -c Release -o $stub -v q -nologo --no-incremental
Copy-Item (Join-Path $stub 'Harbor-Setup.exe') (Join-Path $app 'Uninstall Harbor.exe')

# 4. Zip it all and build the real installer around that.
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($app, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
Run dotnet build $setupProject -c Release -o $setup -v q -nologo --no-incremental "-p:PayloadZip=$zip"

$final = Join-Path $dist "Harbor-Setup-$version.exe"
New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item (Join-Path $setup 'Harbor-Setup.exe') $final -Force

$appMb = (Get-ChildItem $app -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
"App folder: {0:N0} MB" -f $appMb
"Installer:  {0:N0} MB  {1}" -f ((Get-Item $final).Length / 1MB), $final

Remove-Item -Recurse -Force $out
