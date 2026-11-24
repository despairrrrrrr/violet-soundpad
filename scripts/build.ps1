param(
    [string]$InnoCompiler,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/Soundpad/Soundpad.csproj'
$artifacts = Join-Path $root 'artifacts'
$output = Join-Path $artifacts 'app'
$packageCache = Join-Path $root '.packages'

if (-not $InnoCompiler) {
    $candidates = @(
        (Join-Path $artifacts 'tools/InnoSetup/ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7/ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 7/ISCC.exe'),
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Path)
    )
    $InnoCompiler = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
}
if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler)) {
    throw 'Inno Setup compiler ISCC.exe was not found. Pass its path via -InnoCompiler.'
}

if (-not $NoRestore) {
    dotnet restore $project --locked-mode --packages $packageCache
    if ($LASTEXITCODE -ne 0) { throw 'NuGet restore failed.' }
}
dotnet build $project -c Release --no-restore --output $output
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }

$version = ([xml](Get-Content -Raw -LiteralPath $project)).Project.PropertyGroup.Version
& $InnoCompiler "/DAppVersion=$version" (Join-Path $PSScriptRoot 'VioletSoundpad.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }

$installer = Join-Path $artifacts "VioletSoundpad-Setup-$version.exe"
Get-FileHash -Algorithm SHA256 -LiteralPath $installer | Select-Object Hash, Path
