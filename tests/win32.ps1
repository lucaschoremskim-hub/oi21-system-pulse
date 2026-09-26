# Aides de test pour l'overlay natif (fenêtre Windows réelle) : position, style, capture d'écran, glissement à la vraie souris.
# Usage : win32.ps1 rect | shot <fichier> | drag <x1> <y1> <x2> <y2>
param([string]$Op, [string]$A1, [string]$A2, [string]$A3, [string]$A4)
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public class Win {
  [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern IntPtr FindWindow(string c, string t);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  public struct RECT { public int L, T, R, B; }
}
"@
$h = [Win]::FindWindow([NullString]::Value, 'System Pulse Overlay')
if ($Op -eq 'rect') {
  if ($h -eq [IntPtr]::Zero) { '{"found":false}'; exit }
  $r = New-Object Win+RECT; [Win]::GetWindowRect($h, [ref]$r) | Out-Null
  $ex = [int64][Win]::GetWindowLongPtr($h, -20)
  $o = @{ found = $true; x = $r.L; y = $r.T; w = ($r.R - $r.L); h = ($r.B - $r.T); visible = [Win]::IsWindowVisible($h); clickThrough = (($ex -band 0x20) -ne 0); topmost = (($ex -band 0x8) -ne 0); layered = (($ex -band 0x80000) -ne 0); noActivate = (($ex -band 0x08000000) -ne 0) }
  $o | ConvertTo-Json -Compress
}
elseif ($Op -eq 'shot') {
  $r = New-Object Win+RECT; [Win]::GetWindowRect($h, [ref]$r) | Out-Null
  $w = $r.R - $r.L; $hh = $r.B - $r.T
  # fond gris moyen derrière l'overlay pour voir la transparence : capture directe de l'écran
  $bmp = New-Object System.Drawing.Bitmap($w, $hh)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size($w, $hh)))
  $bmp.Save($A1); 'ok'
}
elseif ($Op -eq 'drag') {
  $orig = [System.Windows.Forms.Cursor]::Position
  $x1 = [int]$A1; $y1 = [int]$A2; $x2 = [int]$A3; $y2 = [int]$A4
  [Win]::SetCursorPos($x1, $y1) | Out-Null; Start-Sleep -Milliseconds 150
  [Win]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 120
  for ($i = 1; $i -le 12; $i++) { [Win]::SetCursorPos([int]($x1 + ($x2 - $x1) * $i / 12), [int]($y1 + ($y2 - $y1) * $i / 12)) | Out-Null; Start-Sleep -Milliseconds 35 }
  Start-Sleep -Milliseconds 120
  [Win]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 200
  [Win]::SetCursorPos($orig.X, $orig.Y) | Out-Null
  'ok'
}
