param([switch]$FrameworkDependent)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$python = Join-Path $root '.venv-win\Scripts\python.exe'
if (-not (Test-Path -LiteralPath $python)) { throw 'Create .venv-win and install windows/requirements.txt first.' }
& $python -m PyInstaller --noconfirm --clean --onedir --name AgentMeterBackend --paths . --collect-all tzdata --collect-all zstandard windows\backend.py --distpath windows\dist --workpath windows\build\pyinstaller --specpath windows\build
if ($LASTEXITCODE -ne 0) { throw 'Backend build failed.' }
$args = @('publish', 'windows\AgentMeter.Windows', '-c', 'Release', '-r', 'win-x64', '-o', 'windows\dist\AgentMeter')
if ($FrameworkDependent) { $args += @('--self-contained', 'false') } else { $args += @('--self-contained', 'true') }
& dotnet @args
if ($LASTEXITCODE -ne 0) { throw 'Windows application build failed.' }
New-Item -ItemType Directory -Path 'windows\dist\AgentMeter\backend' -Force | Out-Null
Copy-Item -Path 'windows\dist\AgentMeterBackend\*' -Destination 'windows\dist\AgentMeter\backend' -Recurse -Force
Copy-Item -LiteralPath LICENSE,THIRD_PARTY_NOTICES.md -Destination 'windows\dist\AgentMeter' -Force
Write-Host 'Built windows\dist\AgentMeter\AgentMeter.Windows.exe'
