# Validates that every locale file's format placeholders match the argument counts
# used by LocalizationManager.Format call sites. A bad placeholder used to crash the
# app from a background thread; the runtime now degrades gracefully, and this script
# fails the build instead so broken locales never ship.
#
# When a Format call site is added or its argument count changes, update $formatArgs
# below to match. Unknown Format keys found in C# fail the build to force the update.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$localeDir = Join-Path $repoRoot 'WandEnhancer\Locale'
$clientDir = Join-Path $repoRoot 'WandEnhancer'

# Format key -> Format() argument count, mirroring the C# call sites.
$formatArgs = @{
    log_install_found     = 2
    log_invalid_directory = 1
    log_restore_failed    = 1
    log_patch_failed      = 1
    log_open_link_failed  = 1
    log_exported          = 1
    log_export_failed     = 1
    log_copy_failed       = 1
    pv_scripts_need_panel = 1
}

$failures = @()

$localeFiles = Get-ChildItem -Path $localeDir -Filter 'lang.*.xaml'
if ($localeFiles.Count -eq 0) {
    throw "No locale files found in $localeDir"
}

foreach ($file in $localeFiles) {
    $content = Get-Content -Path $file.FullName -Raw
    foreach ($key in $formatArgs.Keys) {
        $pattern = '<s:String x:Key="' + $key + '">(.*?)</s:String>'
        $match = [regex]::Match(
            $content,
            $pattern,
            [System.Text.RegularExpressions.RegexOptions]::Singleline)
        if (-not $match.Success) {
            # Other locales fall back to English at runtime; only the base file must be complete.
            if ($file.Name -eq 'lang.en-US.xaml') {
                $failures += "$($file.Name): Format key '$key' is missing."
            }
            continue
        }

        $template = $match.Groups[1].Value
        $argCount = $formatArgs[$key]
        try {
            # Explicit overloads: passing an array would bind as a single argument.
            switch ($argCount) {
                1 { [string]::Format($template, '__arg0__') | Out-Null }
                2 { [string]::Format($template, '__arg0__', '__arg1__') | Out-Null }
                3 { [string]::Format($template, '__arg0__', '__arg1__', '__arg2__') | Out-Null }
                default { throw "Format key '$key' takes $argCount args; extend the overload switch." }
            }
        }
        catch {
            $failures += "$($file.Name): Format key '$key' is broken: $($_.Exception.Message)"
        }
    }
}

$formatCallPattern = 'LocalizationManager\.Format\("([^"]+)"'
$usedKeys = Get-ChildItem -Path $clientDir -Filter '*.cs' -Recurse |
    Select-String -Pattern $formatCallPattern |
    ForEach-Object { $_.Matches[0].Groups[1].Value } |
    Sort-Object -Unique
foreach ($usedKey in $usedKeys) {
    if (-not $formatArgs.ContainsKey($usedKey)) {
        $failures += "Format key '$usedKey' is used in C# but missing from the validator table in $($MyInvocation.MyCommand.Name)."
    }
}

if ($failures.Count -gt 0) {
    throw "Locale validation failed:`n- $($failures -join "`n- ")"
}

Write-Host "Validated $($formatArgs.Count) format key(s) across $($localeFiles.Count) locale file(s)."
