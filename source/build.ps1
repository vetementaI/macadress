$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$target = Join-Path (Split-Path -Parent $PSScriptRoot) 'MacRandomizer.exe'
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ "/out:$target" "/win32manifest:$PSScriptRoot\app.manifest" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Management.dll (Join-Path $PSScriptRoot 'MacRandomizer.cs')
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Write-Output "Built: $target"
