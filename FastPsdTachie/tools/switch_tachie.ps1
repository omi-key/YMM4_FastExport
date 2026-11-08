# Switch the PSD tachie type inside a .ymmp between the built-in plugin and FastPsdTachie.
# Usage: switch_tachie.ps1 -Path <file.ymmp> -To fast|standard
# A backup "<name>.before_<to>.ymmp" is written next to the project first. Close YMM4 before running.
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [Parameter(Mandatory = $true)][ValidateSet('fast', 'standard')][string]$To,
    [switch]$IgnoreRunningYmm4   # for testing on copies only
)
$ErrorActionPreference = 'Stop'
$standard = 'YukkuriMovieMaker.Plugin.Tachie.Psd.PsdTachiePlugin, YukkuriMovieMaker.Plugin.Tachie.Psd, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null'
# Must match <AssemblyVersion> in FastPsdTachiePlugin.csproj (.ymmp stores the full type name).
$fast = 'FastPsdTachiePlugin.FastPsdTachiePlugin, FastPsdTachiePlugin, Version=0.4.0.0, Culture=neutral, PublicKeyToken=null'
# Also accept any other FastPsdTachie version string when going back to standard.
$fastPattern = 'FastPsdTachiePlugin\.FastPsdTachiePlugin, FastPsdTachiePlugin, Version=[0-9.]+, Culture=neutral, PublicKeyToken=null'

if (-not (Test-Path -LiteralPath $Path)) { throw "File not found: $Path" }
$full = (Resolve-Path -LiteralPath $Path).Path
if ([IO.Path]::GetExtension($full) -ne '.ymmp') { throw "Not a .ymmp file: $full" }
if (-not $IgnoreRunningYmm4 -and (Get-Process | Where-Object { $_.ProcessName -like 'YukkuriMovieMaker*' })) {
    Write-Warning 'YMM4 is running. Close it first, otherwise it may overwrite this change when it saves.'
    throw 'Aborted: YMM4 is running.'
}

$bytes = [IO.File]::ReadAllBytes($full)
$hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
$text = [Text.UTF8Encoding]::new($false).GetString($bytes, $(if ($hasBom) { 3 } else { 0 }), $bytes.Length - $(if ($hasBom) { 3 } else { 0 }))

if ($To -eq 'fast') {
    $count = ([regex]::Matches($text, [regex]::Escape($standard))).Count
    $new = $text.Replace($standard, $fast)
} else {
    $count = ([regex]::Matches($text, $fastPattern)).Count
    $new = [regex]::Replace($text, $fastPattern, [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $standard })
}
if ($count -eq 0) { Write-Host "Nothing to change (no matching tachie type found for -To $To)."; exit 0 }

$backup = [IO.Path]::ChangeExtension($full, $null).TrimEnd('.') + ".before_$To.ymmp"
if (Test-Path -LiteralPath $backup) { throw "Backup already exists, not overwriting: $backup" }
Copy-Item -LiteralPath $full -Destination $backup
if ($hasBom) { $out = [byte[]](0xEF, 0xBB, 0xBF) + [Text.UTF8Encoding]::new($false).GetBytes($new) } else { $out = [Text.UTF8Encoding]::new($false).GetBytes($new) }
[IO.File]::WriteAllBytes($full, $out)
Write-Host "Switched $count character(s) to '$To'."
Write-Host "Backup: $backup"
