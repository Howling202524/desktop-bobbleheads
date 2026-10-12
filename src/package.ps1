$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1')
$taskRoot = Split-Path -Parent $PSScriptRoot
$outputDirectory = Join-Path $taskRoot '桌面弹簧玩偶'
$archivePath = Join-Path $taskRoot 'desktop-bobbleheads-windows.zip'
Compress-Archive -LiteralPath (Join-Path $outputDirectory '桌面弹簧玩偶.exe'), (Join-Path $outputDirectory '使用说明.txt') -DestinationPath $archivePath -Force
Get-Item -LiteralPath $archivePath | Select-Object FullName, Length
