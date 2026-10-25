# UI Automation driver for manual-test scripting of the Windows client (development aid).
# Dot-source it:  . scripts/uia.ps1 ; $w = Get-SlothWindow ; Invoke-Element $w "Add sample pages"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms

function Get-SlothWindow([int]$TimeoutSec = 10) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, (Get-Process SlothBoss | Select-Object -First 1).Id)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
        $w = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
        if ($w) { return $w }
        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $deadline)
    throw "SlothBoss window not found"
}

function Find-Element($Parent, [string]$Name, [int]$TimeoutSec = 8) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
        $e = $Parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($e) { return $e }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    throw "Element '$Name' not found"
}

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class UiaMouse {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, UIntPtr e);
    public static void Click(int x, int y) { SetCursorPos(x, y); mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero); }
}
"@

function Click-Element($Element) {
    $r = $Element.Current.BoundingRectangle
    [UiaMouse]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
}

function Invoke-Element($Parent, [string]$Name, [switch]$Click) {
    Find-Element $Parent $Name | Out-Null
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    $all = $Parent.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    $p = $null
    if (-not $Click) {
        foreach ($e in $all) { if ($e.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke(); return } }
        foreach ($e in $all) { if ($e.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { $p.Toggle(); return } }
    }
    # Tree items and other elements: click like a user would.
    $target = $all | Where-Object { -not $_.Current.IsOffscreen } | Select-Object -First 1
    if (-not $target) { $target = $all[0] }
    Click-Element $target
}

function Set-ElementText($Parent, [string]$Name, [string]$Text) {
    $e = Find-Element $Parent $Name
    $p = $null
    if ($e.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$p)) { $p.SetValue($Text); return }
    $e.SetFocus(); Start-Sleep -Milliseconds 150
    [System.Windows.Forms.SendKeys]::SendWait($Text)
}

function Focus-Element($Parent, [string]$Name) { (Find-Element $Parent $Name).SetFocus() }

function Send-Keys([string]$Keys) { [System.Windows.Forms.SendKeys]::SendWait($Keys) }

function Get-Texts($Parent) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    $Parent.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond) | ForEach-Object { $_.Current.Name } | Where-Object { $_ }
}
