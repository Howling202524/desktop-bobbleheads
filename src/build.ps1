$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$wpf = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF'
$outputDirectory = Join-Path $taskRoot '桌面弹簧玩偶'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$arguments = @('/nologo', '/target:winexe', '/optimize+', '/platform:anycpu',
    "/out:$taskRoot\桌面弹簧玩偶\桌面弹簧玩偶.exe",
    "/win32manifest:$PSScriptRoot\app.manifest",
    "/resource:$taskRoot\assets\sprite-parts.png,sprite-parts.png",
    "/resource:$taskRoot\assets\sprites.xml,sprites.xml",
    "/reference:$wpf\PresentationFramework.dll", "/reference:$wpf\PresentationCore.dll", "/reference:$wpf\WindowsBase.dll",
    '/reference:System.Xaml.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll',
    "$PSScriptRoot\Bobbleheads.cs", "$PSScriptRoot\RuntimeSupport.cs", "$PSScriptRoot\AssemblyInfo.cs")
& $compiler $arguments
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item -LiteralPath (Join-Path $taskRoot 'docs\使用说明.txt') -Destination $outputDirectory -Force
Get-Item -LiteralPath "$taskRoot\桌面弹簧玩偶\桌面弹簧玩偶.exe" | Select-Object FullName, Length
