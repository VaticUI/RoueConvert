# Builds RoueConvert with the C# compiler that ships with Windows (.NET Framework 4.8) — nothing to install.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$csc = "$fw\csc.exe"
$out = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $out | Out-Null

$refs = @(
  'System.dll', 'System.Core.dll', 'System.Xml.dll', 'System.Xml.Linq.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll',
  'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll', 'System.Web.Extensions.dll', 'Microsoft.CSharp.dll',
  'System.Xaml.dll', "$fw\WPF\WindowsBase.dll", "$fw\WPF\PresentationCore.dll", "$fw\WPF\PresentationFramework.dll",
  "$fw\WPF\UIAutomationClient.dll", "$fw\WPF\UIAutomationTypes.dll"
) | ForEach-Object { "/r:$_" }

$src = Get-ChildItem "$root\src\*.cs" | ForEach-Object { $_.FullName }
$common = @('/nologo', '/optimize+', '/platform:anycpu', "/win32manifest:$root\app.manifest", '/nowarn:1701,1702') + $refs

& $csc @common /target:winexe "/out:$out\RoueConvert.exe" @src
if ($LASTEXITCODE -ne 0) { throw 'Build failed (RoueConvert.exe)' }
& $csc @common /target:exe /define:CLI "/out:$out\RoueConvert.Cli.exe" @src
if ($LASTEXITCODE -ne 0) { throw 'Build failed (RoueConvert.Cli.exe)' }
Write-Host "OK -> $out\RoueConvert.exe"
