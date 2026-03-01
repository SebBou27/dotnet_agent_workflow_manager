param(
  [string]$Path = ".metrics/workflow-metrics.jsonl",
  [int]$Last = 200
)

$ErrorActionPreference = "Stop"

if (!(Test-Path $Path)) {
  Write-Host "No metrics file found at: $Path" -ForegroundColor Yellow
  exit 0
}

$lines = Get-Content $Path | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
if ($Last -gt 0 -and $lines.Count -gt $Last) {
  $lines = $lines | Select-Object -Last $Last
}

$rows = $lines | ForEach-Object { $_ | ConvertFrom-Json }
if (-not $rows -or $rows.Count -eq 0) {
  Write-Host "No metric rows to analyze." -ForegroundColor Yellow
  exit 0
}

function Get-Percentile([double[]]$values, [double]$p) {
  if (-not $values -or $values.Count -eq 0) { return 0 }
  $sorted = $values | Sort-Object
  $idx = [Math]::Ceiling(($p / 100.0) * $sorted.Count) - 1
  if ($idx -lt 0) { $idx = 0 }
  if ($idx -ge $sorted.Count) { $idx = $sorted.Count - 1 }
  return [double]$sorted[$idx]
}

$durations = @($rows | ForEach-Object { [double]$_.durationMs })
$total = $rows.Count
$toolCalls = ($rows | Measure-Object -Property toolCalls -Sum).Sum
$toolErr = ($rows | Measure-Object -Property toolErr -Sum).Sum
$toolOk = ($rows | Measure-Object -Property toolOk -Sum).Sum

$avg = if ($durations.Count -gt 0) { [Math]::Round((($durations | Measure-Object -Average).Average), 2) } else { 0 }
$p50 = [Math]::Round((Get-Percentile $durations 50), 2)
$p95 = [Math]::Round((Get-Percentile $durations 95), 2)
$errRate = if ($toolCalls -gt 0) { [Math]::Round((100.0 * $toolErr / $toolCalls), 2) } else { 0 }

Write-Host "=== Workflow Metrics Summary ===" -ForegroundColor Cyan
Write-Host "Rows analyzed : $total"
Write-Host "Duration avg : $avg ms"
Write-Host "Duration p50 : $p50 ms"
Write-Host "Duration p95 : $p95 ms"
Write-Host "Tool calls   : $toolCalls (ok=$toolOk, err=$toolErr, errRate=$errRate%)"

Write-Host "\nPer-agent:" -ForegroundColor Cyan
$rows | Group-Object agent | ForEach-Object {
  $g = $_.Group
  $d = @($g | ForEach-Object { [double]$_.durationMs })
  $calls = ($g | Measure-Object -Property toolCalls -Sum).Sum
  $errs = ($g | Measure-Object -Property toolErr -Sum).Sum
  $rate = if ($calls -gt 0) { [Math]::Round((100.0 * $errs / $calls), 2) } else { 0 }
  $gAvg = if ($d.Count -gt 0) { [Math]::Round((($d | Measure-Object -Average).Average), 2) } else { 0 }
  Write-Host ("- {0}: n={1}, avg={2}ms, p95={3}ms, errRate={4}%" -f $_.Name, $g.Count, $gAvg, [Math]::Round((Get-Percentile $d 95),2), $rate)
}
