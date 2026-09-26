# Compile en Release et fabrique dist\SystemPulse.zip (+ somme SHA-256). Utilisé à la main et par GitHub Actions.
# Usage :  powershell -File tools\package.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$stage = Join-Path $root 'dist\SystemPulse'
$zip = Join-Path $root 'dist\SystemPulse.zip'
Remove-Item (Join-Path $root 'dist') -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stage | Out-Null

& dotnet build "$root\SystemPulse.csproj" -c Release -p:Platform=x64 -o $stage --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Compilation échouée.' }

# Fichiers inutiles à l'exécution (WPF, symboles, dossiers de plateformes autres que x64).
Remove-Item "$stage\Microsoft.Web.WebView2.Wpf.dll", "$stage\*.pdb", "$stage\*.xml" -Force -ErrorAction SilentlyContinue
Remove-Item "$stage\runtimes", "$stage\x64" -Recurse -Force -ErrorAction SilentlyContinue

$version = (Get-Item "$stage\SystemPulse.exe").VersionInfo.ProductVersion -replace '\+.*$', ''
(Get-Content "$PSScriptRoot\LISEZ-MOI-partage.txt" -Raw -Encoding UTF8).Replace('{VERSION}', $version) |
  Set-Content "$stage\LISEZ-MOI.txt" -Encoding UTF8
Copy-Item "$root\LICENSE" "$stage\LICENSE.txt"
Copy-Item "$root\vendor\LICENSE-PresentMon.txt" "$stage\vendor\" -Force

Add-Type -AssemblyName System.IO.Compression.FileSystem
# Entrées écrites à la main pour avoir des chemins en « / » (Compress-Archive de PowerShell 5.1 met des «  »).
$archive = [IO.Compression.ZipFile]::Open($zip, 'Create')
foreach ($f in Get-ChildItem $stage -Recurse -File) {
  $entry = 'SystemPulse/' + $f.FullName.Substring($stage.Length + 1).Replace([string][char]92, '/')
  [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal)
}
$archive.Dispose()
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
"$hash  SystemPulse.zip" | Set-Content (Join-Path $root 'dist\SystemPulse.zip.sha256') -Encoding ascii

$z = [IO.Compression.ZipFile]::OpenRead($zip)
$names = @($z.Entries | ForEach-Object { $_.FullName })
$z.Dispose()
foreach ($needed in 'SystemPulse.exe', 'WebView2Loader.dll', 'Microsoft.Web.WebView2.Core.dll', 'ui/index.html', 'vendor/PresentMon-2.6.0-x64.exe') {
  if (-not ($names | Where-Object { $_ -match [regex]::Escape($needed) + '$' })) { throw "Fichier manquant dans le zip : $needed" }
}
"Version $version : $zip = $([math]::Round((Get-Item $zip).Length / 1MB, 2)) Mo ; SHA-256 $hash"
"::VERSION::$version"
