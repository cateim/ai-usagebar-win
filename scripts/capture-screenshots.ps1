<#
.SYNOPSIS
    Captures the app's windows into screenshots/ for the README.

.DESCRIPTION
    The popup hides as soon as it loses focus, and focusing a terminal to run
    this script is itself a focus loss. Capturing the popup therefore needs the
    app started with the auto-hide disabled:

        $env:AIUSAGEBAR_WIN_PIN_POPUP = "1"
        .\AiUsageBar\bin\x64\Debug\net8.0-windows10.0.19041.0\ai-usagebar-win.exe

    Then click the tray icon and run this script. Without that variable the popup
    closes the moment you switch to the terminal, and only manual capture
    (Win+Shift+S) works.

    Pixels are read from the window's screen rectangle rather than by activating
    it, so nothing here steals focus.

    Start the app, then run this. It captures whatever it finds:
      - the settings window, if open
      - the popup, if open (click the tray icon first)
      - the tray tooltip, which is REDRAWN rather than captured: that tooltip is
        painted by the Windows shell, belongs to no window of this app, and
        disappears on focus loss, so it cannot be grabbed. The image reproduces
        the Windows 11 dark tooltip with the app's real text. Pass -TooltipText
        to change what it says.

.EXAMPLE
    pwsh -File scripts/capture-screenshots.ps1
    pwsh -File scripts/capture-screenshots.ps1 -Only popup
#>

[CmdletBinding()]
param(
    [ValidateSet('all', 'popup', 'settings', 'hover')]
    [string]$Only = 'all',

    # Text drawn into the tray-tooltip image. Defaults to what the app currently
    # produces for a single configured provider.
    [string]$TooltipText = ("cld 3% " + [char]0x00B7 + " Session (5h)"),

    [string]$OutDir = (Join-Path $PSScriptRoot '..\screenshots'),

    # Seconds to wait for a window to appear. The popup hides on focus loss, so
    # it cannot be opened by this script: start it waiting, then click the tray
    # icon. Nothing here steals focus, so the popup stays up.
    [int]$WaitSeconds = 15
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Runtime.InteropServices;

public class WinCapture
{
    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int count);

    // The window rect including the drop shadow is wider than the visible window.
    // DwmGetWindowAttribute(9 = EXTENDED_FRAME_BOUNDS) returns what the user sees.
    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT value, int size);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public static RECT BestRect(IntPtr hWnd)
    {
        RECT r;
        if (DwmGetWindowAttribute(hWnd, 9, out r, Marshal.SizeOf(typeof(RECT))) == 0
            && r.Right > r.Left && r.Bottom > r.Top)
        {
            return r;
        }
        GetWindowRect(hWnd, out r);
        return r;
    }
}
'@

function Get-AppWindows {
    $procs = @(Get-Process -Name ai-usagebar-win -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) { throw "ai-usagebar-win is not running. Start it first." }
    $pids = $procs | ForEach-Object { [uint32]$_.Id }

    $found = New-Object System.Collections.ArrayList

    $callback = [WinCapture+EnumWindowsProc] {
        param($hWnd, $lParam)

        if (-not [WinCapture]::IsWindowVisible($hWnd)) { return $true }

        [uint32]$owner = 0
        [void][WinCapture]::GetWindowThreadProcessId($hWnd, [ref]$owner)
        if ($pids -notcontains $owner) { return $true }

        $len = [WinCapture]::GetWindowTextLength($hWnd)
        $title = ''
        if ($len -gt 0) {
            $sb = New-Object System.Text.StringBuilder ($len + 1)
            [void][WinCapture]::GetWindowText($hWnd, $sb, $sb.Capacity)
            $title = $sb.ToString()
        }

        $cn = New-Object System.Text.StringBuilder 256
        [void][WinCapture]::GetClassName($hWnd, $cn, $cn.Capacity)

        $r = [WinCapture]::BestRect($hWnd)
        $w = $r.Right - $r.Left
        $h = $r.Bottom - $r.Top
        # Skip the invisible 1x1 helper windows WPF keeps around.
        if ($w -gt 60 -and $h -gt 60) {
            [void]$found.Add([pscustomobject]@{
                Handle = $hWnd; Title = $title; Class = $cn.ToString()
                X = $r.Left; Y = $r.Top; Width = $w; Height = $h
            })
        }
        return $true
    }

    [void][WinCapture]::EnumWindows($callback, [IntPtr]::Zero)
    return $found
}

function Save-Window($win, [string]$path) {
    $bmp = New-Object System.Drawing.Bitmap $win.Width, $win.Height
    $gfx = [System.Drawing.Graphics]::FromImage($bmp)
    $gfx.CopyFromScreen($win.X, $win.Y, 0, 0, $bmp.Size)
    $gfx.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host ("saved {0}  ({1}x{2})" -f (Split-Path $path -Leaf), $win.Width, $win.Height) -ForegroundColor Green
}

$OutDir = (Resolve-Path $OutDir).Path

# The tooltip is drawn, not captured, so it needs no running app. Only the window
# grabs do, and asking for -Only hover should not fail on a closed app.
if ($Only -in 'all', 'popup', 'settings') {
    $wanted = if ($Only -eq 'settings') { 'settings window' } elseif ($Only -eq 'popup') { 'popup' } else { 'any window' }
    Write-Host "Waiting up to $WaitSeconds s for the $wanted. Open it now." -ForegroundColor Cyan

    $deadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
    $windows = @()
    while ($true) {
        $windows = @(Get-AppWindows)
        $hasSettings = @($windows | Where-Object { $_.Title -like '*Settings*' }).Count -gt 0
        $hasPopup = @($windows | Where-Object { $_.Title -notlike '*Settings*' }).Count -gt 0

        $ready = switch ($Only) {
            'settings' { $hasSettings }
            'popup'    { $hasPopup }
            default    { $hasSettings -or $hasPopup }
        }
        if ($ready -or [DateTime]::UtcNow -gt $deadline) { break }
        Start-Sleep -Milliseconds 400
    }

    Write-Host "Windows found:"
    $windows | ForEach-Object { Write-Host ("  '{0}' [{1}] {2}x{3}" -f $_.Title, $_.Class, $_.Width, $_.Height) }
    Write-Host ""

    # The settings window carries a title; the popup is the untitled one.
    $settings = $windows | Where-Object { $_.Title -like '*Settings*' } | Select-Object -First 1
    $popup = $windows | Where-Object { $_.Title -notlike '*Settings*' } | Sort-Object Height -Descending | Select-Object -First 1

    if ($Only -in 'all', 'settings') {
        if ($settings) { Save-Window $settings (Join-Path $OutDir 'settings.png') }
        else { Write-Host "settings window not open, skipped" -ForegroundColor Yellow }
    }

    if ($Only -in 'all', 'popup') {
        if ($popup) { Save-Window $popup (Join-Path $OutDir 'click.png') }
        else { Write-Host "popup not open (click the tray icon), skipped" -ForegroundColor Yellow }
    }
}


function New-TooltipImage([string]$text, [string]$path) {
    $scale = 2   # drawn at 2x so it stays sharp on HiDPI screens
    $font = New-Object System.Drawing.Font("Segoe UI", (9 * $scale), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Point)

    $probe = New-Object System.Drawing.Bitmap 1, 1
    $g0 = [System.Drawing.Graphics]::FromImage($probe)
    $g0.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $size = $g0.MeasureString($text, $font)
    $g0.Dispose(); $probe.Dispose()

    $padX = 11 * $scale
    $padY = 7 * $scale
    $w = [int][Math]::Ceiling($size.Width) + ($padX * 2)
    $h = [int][Math]::Ceiling($size.Height) + ($padY * 2)

    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    $radius = 6 * $scale
    $d = $radius * 2
    $path2 = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path2.AddArc(0, 0, $d, $d, 180, 90)
    $path2.AddArc($w - $d - 1, 0, $d, $d, 270, 90)
    $path2.AddArc($w - $d - 1, $h - $d - 1, $d, $d, 0, 90)
    $path2.AddArc(0, $h - $d - 1, $d, $d, 90, 90)
    $path2.CloseFigure()

    $fill = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x2B, 0x2B, 0x2B))
    $g.FillPath($fill, $path2)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 0x45, 0x45, 0x45)), $scale
    $g.DrawPath($pen, $path2)

    $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0xF0, 0xF0, 0xF0))
    $g.DrawString($text, $font, $brush, $padX, $padY)

    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host ("saved {0}  ({1}x{2}, reproduction)" -f (Split-Path $path -Leaf), $w, $h) -ForegroundColor Green
}

if ($Only -in 'all', 'hover') {
    New-TooltipImage $TooltipText (Join-Path $OutDir 'hover.png')
}
