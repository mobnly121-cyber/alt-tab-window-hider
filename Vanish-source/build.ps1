$ErrorActionPreference = 'Stop'

$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}

if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'The .NET Framework C# compiler was not found.'
}

$outputDirectory = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

& $compiler /nologo /target:winexe /optimize+ /platform:anycpu `
    /win32manifest:"$PSScriptRoot\app.manifest" `
    /reference:System.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /out:"$outputDirectory\Vanish.exe" `
    "$PSScriptRoot\Program.cs"

if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}

Write-Host "Built $outputDirectory\Vanish.exe"
