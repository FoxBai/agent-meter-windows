$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$python = Join-Path $root '.venv-win\Scripts\python.exe'
if (-not (Test-Path -LiteralPath $python)) { throw 'Create .venv-win and install windows/requirements.txt first.' }
$testCases = @(
    'tests.test_windows_port', 'tests.test_sources', 'tests.test_dsh_tokens',
    'tests.test_cli_sessions.CodexTests', 'tests.test_cli_sessions.McodeTests.test_rows_as_is_and_schema_guard',
    'tests.test_local_tokens.ZCodeTests', 'tests.test_local_tokens.WorkBuddyTests',
    'tests.test_local_tokens.OpenCodeTests', 'tests.test_local_tokens.QoderCreditsTests',
    'tests.test_kimi_tokens.KimiTokenTests', 'tests.test_mimocode_tokens', 'tests.test_claude_code'
)
& $python -m unittest @testCases -q
if ($LASTEXITCODE -ne 0) { throw 'Windows collector tests failed.' }
