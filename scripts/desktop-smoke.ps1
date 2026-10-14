<#
Normal published app, official read-only refreshes, owned-tree sampling,
single instance, graceful WM_CLOSE and app-window-only capture. No global screenshots,
pointer injection, memory trimming or changes to user settings/startup values.
Use -StartupSeconds 135 to observe two default 120-second cycles.
#>
[CmdletBinding()]
param([string]$ExePath = 'artifacts/win-x64/CodexQuota.Desktop.exe', [int]$StartupSeconds = 18,
      [int]$IdleSeconds = 30, [string]$OutputDirectory = 'artifacts/measurements', [switch]$ResourceProbe,
      [switch]$AvoidPointerOverlap)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
function Resolve-WorkspacePath([string]$Path) {
    $resolved = [IO.Path]::GetFullPath((Join-Path $root $Path))
    if (-not $resolved.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Path must stay inside the workspace.' }
    return $resolved
}
$exe = Resolve-WorkspacePath $ExePath
$output = Resolve-WorkspacePath $OutputDirectory
if (-not (Test-Path -LiteralPath $exe)) { throw 'Publish the app first.' }
if (Get-Process -Name CodexQuota.Desktop -ErrorAction SilentlyContinue) { throw 'Stop the existing widget before the isolated smoke measurement.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
Add-Type -AssemblyName System.Drawing
Add-Type -Namespace Smoke -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, System.IntPtr lParam);
public delegate bool EnumWindowsProc(System.IntPtr hWnd, System.IntPtr lParam);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr hWnd, out uint pid);
[DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr hWnd, out RECT rect);
[DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] public static extern System.IntPtr GetWindowLongPtr(System.IntPtr hWnd, int index);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool PrintWindow(System.IntPtr hWnd, System.IntPtr hdc, uint flags);
[DllImport("user32.dll")] public static extern bool PostMessage(System.IntPtr hWnd, uint message, System.IntPtr wParam, System.IntPtr lParam);
[DllImport("user32.dll")] public static extern System.IntPtr SetThreadDpiAwarenessContext(System.IntPtr context);
[DllImport("user32.dll")] public static extern uint GetDpiForWindow(System.IntPtr hWnd);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(System.IntPtr hWnd, System.Text.StringBuilder text, int capacity);
[DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
[DllImport("user32.dll")] public static extern System.IntPtr MonitorFromWindow(System.IntPtr hWnd, uint flags);
[DllImport("user32.dll")] public static extern bool GetMonitorInfo(System.IntPtr monitor, ref MONITORINFO info);
[DllImport("user32.dll")] public static extern bool SetWindowPos(System.IntPtr hWnd, System.IntPtr after, int x, int y, int width, int height, uint flags);
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] public struct POINT { public int X, Y; }
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] public struct MONITORINFO { public uint Size; public RECT Monitor; public RECT Work; public uint Flags; }
'@
function Get-WidgetWindow([int]$ProcessId) {
    $script:found = $null
    $script:ownedWindowTitles = @()
    $callback = [Smoke.Native+EnumWindowsProc]{
        param($hWnd, $lParam)
        $ownerId = 0
        [void][Smoke.Native]::GetWindowThreadProcessId($hWnd, [ref]$ownerId)
        if ($ownerId -eq $ProcessId) {
            $title = [Text.StringBuilder]::new(120)
            [void][Smoke.Native]::GetWindowText($hWnd,$title,120)
            if ($title.Length -gt 0) { $script:ownedWindowTitles += $title.ToString() }
        }
        if ($ownerId -eq $ProcessId -and [Smoke.Native]::IsWindowVisible($hWnd)) {
            $rect = New-Object Smoke.Native+RECT
            [void][Smoke.Native]::GetWindowRect($hWnd, [ref]$rect)
            if (($rect.Right - $rect.Left) -le 200 -and ($rect.Bottom - $rect.Top) -le 200) {
                $script:found = [pscustomobject]@{ Handle = $hWnd.ToInt64(); Width = $rect.Right - $rect.Left; Height = $rect.Bottom - $rect.Top; Left = $rect.Left; Top = $rect.Top; Dpi = [Smoke.Native]::GetDpiForWindow($hWnd); Topmost = (([Smoke.Native]::GetWindowLongPtr($hWnd, -20).ToInt64() -band 8) -ne 0) }
            }
        }
        return $true
    }
    $priorDpiContext = [Smoke.Native]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
    try { [void][Smoke.Native]::EnumWindows($callback, [IntPtr]::Zero) }
    finally { [void][Smoke.Native]::SetThreadDpiAwarenessContext($priorDpiContext) }
    return $script:found
}
$ownedIds = @{}
$helperCpu = @{}
function Get-OwnedHelpers([int]$WidgetId) {
    # Never read command lines or credentials.
    $metadata = @(Get-CimInstance Win32_Process -Property ProcessId,ParentProcessId,Name)
    $descendants = @{}
    $descendants[$WidgetId] = $true
    do {
        $added = $false
        foreach ($entry in $metadata) {
            $id = [int]$entry.ProcessId
            if ($descendants.ContainsKey([int]$entry.ParentProcessId) -and -not $descendants.ContainsKey($id)) {
                $descendants[$id] = $true; $ownedIds[$id] = $true; $added = $true
            }
        }
    } while ($added)
    $processes = @()
    foreach ($id in @($ownedIds.Keys)) {
        $process = Get-Process -Id $id -ErrorAction SilentlyContinue
        if ($process) { $processes += $process }
    }
    return $processes
}
$samples = @(); $idle = @(); $widget = $null; $second = $null; $gracefulExit = $false
$logicalProcessors = [Environment]::ProcessorCount
try {
    Write-Host 'Launching normal live widget; measuring only its owned process tree.'
    $startedAt = Get-Date
    if ($ResourceProbe) {
        $probePath = Join-Path $output 'resource-normal.json'
        $widget = Start-Process -FilePath $exe -ArgumentList '--resource-probe', ('"' + $probePath + '"') -WindowStyle Hidden -PassThru
    } else {
        $widget = Start-Process -FilePath $exe -WindowStyle Hidden -PassThru
    }
    $initialWindow = $null
    if ($AvoidPointerOverlap) {
        $windowDeadline = (Get-Date).AddSeconds(10)
        while (-not $initialWindow -and (Get-Date) -lt $windowDeadline) {
            $initialWindow = Get-WidgetWindow $widget.Id
            if (-not $initialWindow) { Start-Sleep -Milliseconds 20 }
        }
        if (-not $initialWindow) { throw 'Ball did not appear for app-only measurement placement.' }
        $priorDpiContext = [Smoke.Native]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
        try {
            $cursor = New-Object Smoke.Native+POINT
            $monitor = New-Object Smoke.Native+MONITORINFO
            $monitor.Size = [Runtime.InteropServices.Marshal]::SizeOf($monitor)
            $handle = [IntPtr]::new($initialWindow.Handle)
            if (-not [Smoke.Native]::GetCursorPos([ref]$cursor) -or -not [Smoke.Native]::GetMonitorInfo([Smoke.Native]::MonitorFromWindow($handle,2),[ref]$monitor)) { throw 'Could not choose an app-only measurement position.' }
            $margin = [int](16 * $initialWindow.Dpi / 96)
            $candidates = @(
                @{X=$monitor.Work.Left+$margin;Y=$monitor.Work.Top+$margin},
                @{X=$monitor.Work.Right-$initialWindow.Width-$margin;Y=$monitor.Work.Top+$margin},
                @{X=$monitor.Work.Left+$margin;Y=$monitor.Work.Bottom-$initialWindow.Height-$margin},
                @{X=$monitor.Work.Right-$initialWindow.Width-$margin;Y=$monitor.Work.Bottom-$initialWindow.Height-$margin}
            )
            $position = $candidates | Sort-Object { [Math]::Pow($_.X+$initialWindow.Width/2-$cursor.X,2)+[Math]::Pow($_.Y+$initialWindow.Height/2-$cursor.Y,2) } -Descending | Select-Object -First 1
            if (-not [Smoke.Native]::SetWindowPos($handle,[IntPtr]::Zero,$position.X,$position.Y,0,0,0x0015)) { throw 'App-only measurement placement failed.' }
        } finally { [void][Smoke.Native]::SetThreadDpiAwarenessContext($priorDpiContext) }
    }
    $previousCpu = 0.0; $previousTime = $startedAt
    $startupDeadline = (Get-Date).AddSeconds($StartupSeconds)
    $idleDeadline = $startupDeadline.AddSeconds($IdleSeconds)
    while ((Get-Date) -lt $idleDeadline) {
        $widget.Refresh()
        if ($widget.HasExited) { throw 'Widget exited during the resource measurement.' }
        $helpers = @(Get-OwnedHelpers $widget.Id)
        $helperWs = 0L; $helperPrivate = 0L
        foreach ($helper in $helpers) {
            $helperWs += $helper.WorkingSet64; $helperPrivate += $helper.PrivateMemorySize64
            $helperCpu[$helper.Id] = $helper.TotalProcessorTime.TotalSeconds
        }
        $now = Get-Date
        $cpu = $widget.TotalProcessorTime.TotalSeconds + ($helperCpu.Values | Measure-Object -Sum).Sum
        $cpuPercent = (($cpu - $previousCpu) / [Math]::Max(0.01, ($now - $previousTime).TotalSeconds)) / $logicalProcessors * 100
        $previousCpu = $cpu; $previousTime = $now
        $sample = [pscustomobject]@{
            time = $now.ToString('o'); widgetWs = $widget.WorkingSet64; widgetPrivate = $widget.PrivateMemorySize64
            helperCount = $helpers.Count; helperWs = $helperWs; helperPrivate = $helperPrivate
            combinedWs = $widget.WorkingSet64 + $helperWs; combinedPrivate = $widget.PrivateMemorySize64 + $helperPrivate
            cpuPercentOfMachine = [Math]::Round($cpuPercent, 5)
        }
        $samples += $sample
        if ($now -ge $startupDeadline) { $idle += $sample }
        Start-Sleep -Milliseconds $(if ($now -lt $startupDeadline) { 200 } else { 1000 })
    }
    $window = Get-WidgetWindow $widget.Id
    if (-not $window) { throw 'Ball window not found.' }
    $second = Start-Process -FilePath $exe -WindowStyle Hidden -PassThru
    if (-not $second.WaitForExit(3000)) { throw 'Second widget instance did not exit.' }
    $capture = New-Object Drawing.Bitmap($window.Width, $window.Height)
    $graphics = [Drawing.Graphics]::FromImage($capture)
    $hdc = $graphics.GetHdc()
    $captureSucceeded = [Smoke.Native]::PrintWindow([IntPtr]$window.Handle, $hdc, 2)
    $graphics.ReleaseHdc($hdc); $graphics.Dispose()
    $capture.Save((Join-Path $output 'ball-window.png'), [Drawing.Imaging.ImageFormat]::Png)
    $capture.Dispose()
    Write-Host 'Requesting graceful widget shutdown through its own WM_CLOSE handler.'
    [void][Smoke.Native]::PostMessage([IntPtr]$window.Handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    $gracefulExit = $widget.WaitForExit(10000)
    Start-Sleep -Milliseconds 300
    $remaining = @(Get-OwnedHelpers $widget.Id)
    if (-not $gracefulExit -or $remaining.Count -gt 0) { throw 'Graceful shutdown/process-tree cleanup failed.' }
    $activeSamples = @($samples | Where-Object helperCount -gt 0)
    $helperFreeIdle = @($idle | Where-Object helperCount -eq 0)
    $probe = $null
    if ($ResourceProbe) {
        if (-not (Test-Path -LiteralPath $probePath) -or (Get-Item -LiteralPath $probePath).LastWriteTime -lt $startedAt) { throw 'Fresh normal resource probe was not written.' }
        $probe = Get-Content -LiteralPath $probePath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    $report = [pscustomobject]@{
        os = [Environment]::OSVersion.VersionString; logicalProcessors = $logicalProcessors
        executable = $exe; desktopDllSha256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path $exe) 'CodexQuota.Desktop.dll')).Hash
        coreDllSha256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path $exe) 'CodexQuota.Core.dll')).Hash
        startupSeconds = $StartupSeconds; idleSeconds = $IdleSeconds
        refreshCycle = [pscustomobject]@{
            ownedHelperPidsObserved = $ownedIds.Count; activeSampleCount = $activeSamples.Count
            widgetPeakWorkingSetMiB = [Math]::Round(($samples.widgetWs | Measure-Object -Maximum).Maximum / 1MB, 2)
            helperPeakWorkingSetMiB = [Math]::Round(($samples.helperWs | Measure-Object -Maximum).Maximum / 1MB, 2)
            combinedSimultaneousPeakWorkingSetMiB = [Math]::Round(($samples.combinedWs | Measure-Object -Maximum).Maximum / 1MB, 2)
            combinedSimultaneousPeakPrivateMiB = [Math]::Round(($samples.combinedPrivate | Measure-Object -Maximum).Maximum / 1MB, 2)
            cpuPeakPercentOfMachine = ($activeSamples.cpuPercentOfMachine | Measure-Object -Maximum).Maximum
            activeSamples = $activeSamples
        }
        widgetIdleOnly = [pscustomobject]@{
            sampleCount = $helperFreeIdle.Count
            workingSetMinMiB = [Math]::Round(($helperFreeIdle.widgetWs | Measure-Object -Minimum).Minimum / 1MB, 2)
            workingSetMaxMiB = [Math]::Round(($helperFreeIdle.widgetWs | Measure-Object -Maximum).Maximum / 1MB, 2)
            workingSetAvgMiB = [Math]::Round(($helperFreeIdle.widgetWs | Measure-Object -Average).Average / 1MB, 2)
            privateAvgMiB = [Math]::Round(($helperFreeIdle.widgetPrivate | Measure-Object -Average).Average / 1MB, 2)
            cpuPercentMaxOfMachine = ($helperFreeIdle.cpuPercentOfMachine | Measure-Object -Maximum).Maximum
            cpuPercentAvgOfMachine = [Math]::Round(($helperFreeIdle.cpuPercentOfMachine | Measure-Object -Average).Average, 6)
            idleSamplesWithHelper = @($idle | Where-Object helperCount -gt 0).Count
        }
        ballWindow = $window; initialBallWindow = $initialWindow; ballTemporarilyMovedAwayFromPointer = [bool]$AvoidPointerOverlap
        ballCaptureSucceeded = $captureSucceeded; ownedWindowTitles = $script:ownedWindowTitles
        resourceProbe = $probe
        singleInstanceSecondExited = $second.HasExited
        gracefulExit = $gracefulExit; helpersAfterExit = $remaining.Count
        note = 'Normal live binary; no verification panels/screenshots constructed by this script. AvoidPointerOverlap temporarily moves only the app ball to a work-area corner far from the unchanged pointer, without persisting position or activating another app. ResourceProbe adds one opt-in metadata snapshot at 6 seconds. CIM ancestry captures Node/official descendants and excludes unrelated codex processes. Peaks are simultaneous sample maxima; sampling can miss brief peaks. CPU is machine-normalized and helper CPU sampled before exit is a lower bound. No memory trimming. Capture includes only this app.'
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'desktop-smoke.json') -Encoding UTF8
    $report | Select-Object widgetIdleOnly,gracefulExit,helpersAfterExit | Format-List
}
finally {
    if ($second -and -not $second.HasExited) { Stop-Process -Id $second.Id -Force -ErrorAction SilentlyContinue }
    if ($widget -and -not $widget.HasExited) { Stop-Process -Id $widget.Id -Force -ErrorAction SilentlyContinue }
}
