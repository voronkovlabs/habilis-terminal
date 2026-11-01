# Stops every habi process started from %TEMP%\habi-tests\ (the test copies made by New-TestCopy.ps1) and prints what it
# stopped. Any other habi, such as the owner's own, is never touched.
$root = (Join-Path $env:TEMP 'habi-tests') + '\'
$stopped = 0
foreach ($p in Get-Process -Name habi -ErrorAction SilentlyContinue) {
    $path = $p.Path
    if ($path -and $path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        Stop-Process -Id $p.Id -Force
        Write-Output "Stopped pid $($p.Id): $path"
        $stopped++
    }
}
if ($stopped -eq 0) { Write-Output 'No test copies running.' }
