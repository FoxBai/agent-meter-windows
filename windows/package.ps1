$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$app = 'windows\dist\AgentMeter'
foreach ($required in @('AgentMeter.Windows.exe', 'backend\AgentMeterBackend.exe', 'LICENSE', 'licenses\Python-LICENSE.txt', 'QUICK_START.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $app $required) -PathType Leaf)) { throw "Missing build output: $required" }
}
New-Item -ItemType Directory -Path 'windows\release' -Force | Out-Null
$archive = 'windows\release\AgentMeter-Windows-x64.zip'
Compress-Archive -Path $app -DestinationPath $archive -CompressionLevel Optimal -Force
$digest = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath 'windows\release\SHA256SUMS.txt' -Encoding ascii -Value "$digest  AgentMeter-Windows-x64.zip"
Write-Host "Packaged $archive"
