$ErrorActionPreference = 'Stop'
$compiler = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $compiler)) { $compiler = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $compiler)) { throw '未找到系统 C# 编译器。' }

& $compiler /nologo /target:winexe /optimize+ /win32manifest:app.manifest /out:CodexFloat.Windows.exe /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll CodexFloat.cs
if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
Write-Host '已生成 CodexFloat.Windows.exe'
