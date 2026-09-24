# Smoke test: nạp DLL plugin MLAstro (đã build) và kiểm tra manifest + options template.
# Chạy: powershell -STA -NoProfile -File .vscode\probe-runsmoke.ps1
param([string]$NinaDir = '')

$ErrorActionPreference = 'Continue'

$dll = Join-Path $env:LOCALAPPDATA 'NINA\Plugins\3.0.0\MLAstroRPA-TPPA\NINA.Plugins.MLAstroRPA_TPPA.dll'
if (-not (Test-Path $dll)) { $dll = Join-Path (Get-Location) 'bin\Release\net8.0-windows7.0\NINA.Plugins.MLAstroRPA_TPPA.dll' }
Write-Host "DLL      : $dll"
Write-Host "exists   : $(Test-Path $dll)"

$candidates = @($NinaDir, "$env:LOCALAPPDATA\Programs\NINA", 'C:\Program Files\NINA', 'C:\Program Files (x86)\NINA') +
    @(Get-ChildItem 'C:\Program Files', 'C:\Program Files (x86)' -Directory -Filter 'N.I.N.A.*' -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty FullName)
$script:NinaRoot = $candidates | Where-Object { $_ -and (Test-Path (Join-Path $_ 'NINA.exe')) } | Select-Object -First 1
if (-not $script:NinaRoot) { $script:NinaRoot = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1 }
Write-Host "NINA dir : $script:NinaRoot"
$script:DllDir = Split-Path -Parent $dll

[System.AppDomain]::CurrentDomain.add_AssemblyResolve({
    param($s, $e)
    $n = ($e.Name -split ',')[0]
    $dirs = @($script:NinaRoot, $script:DllDir, (Join-Path $script:NinaRoot 'Plugins'))
    foreach ($d in $dirs) {
        if ($d -and (Test-Path $d)) {
            $p = Join-Path $d "$n.dll"
            if (Test-Path $p) { return [System.Reflection.Assembly]::LoadFrom($p) }
        }
    }
    return $null
})

$asm = [System.Reflection.Assembly]::LoadFrom($dll)
Write-Host "assembly : $($asm.GetName().Name) $($asm.GetName().Version)"

$flags = [System.Reflection.BindingFlags]'Instance,NonPublic,Public'

$types = @()
try { $types = $asm.GetTypes() }
catch {
    Write-Host 'GetTypes FAILED - missing references:'
    $_.Exception.LoaderExceptions | Select-Object -First 15 | ForEach-Object { Write-Host ("   " + $_.Message) }
}

$manifest = $asm.GetType('NINA.Plugins.PolarAlignment.MLAstroPlugin')
Write-Host "manifest : $($manifest.FullName)"
if ($manifest) {
    $contracts = $manifest.GetCustomAttributes($true) |
        Where-Object { $_.GetType().Name -eq 'ExportAttribute' } |
        ForEach-Object { $_.ContractType.FullName }
    Write-Host "exports  : $($contracts -join ', ')"
}

$manifestCount = @($types | Where-Object { $_.FullName -eq 'NINA.Plugins.PolarAlignment.MLAstroPlugin' }).Count
Write-Host "manifest types found: $manifestCount (mong doi 1)"

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

$optType = $asm.GetType('NINA.Plugins.PolarAlignment.Options')
$opt = [System.Activator]::CreateInstance($optType, $flags, $null, @(), $null)
Write-Host "Options keys: $(($opt.Keys | ForEach-Object { $_.ToString() }) -join ', ')"

$mlType = $asm.GetType('MLAstro_Robotic_Polar_Alignment.Plugin.MLAstroOptions')
$ml = [System.Activator]::CreateInstance($mlType, $flags, $null, @(), $null)
Write-Host "MLAstroOptions keys: $(($ml.Keys | ForEach-Object { $_.ToString() }) -join ', ')"

# Các control/view chính phải có mặt trong assembly
foreach ($t in @(
        'MLAstro_Robotic_Polar_Alignment.Dockables.PolarAlignmentDockVM',
        'MLAstro_Robotic_Polar_Alignment.Dockables.PolarAlignmentDockable',
        'MLAstro_Robotic_Polar_Alignment.Plugin.MLAstroController',
        'MLAstro_Robotic_Polar_Alignment.Services.SerialConnectionService')) {
    $resolved = $asm.GetType($t)
    Write-Host ("type {0,-70} : {1}" -f $t, ($(if ($resolved) { 'OK' } else { 'MISSING' })))
}
