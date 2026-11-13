param(
    [string]$VcRedistDirectory,
    [string]$Output = "$PSScriptRoot/artifacts/DrawingRenamer-win-x64"
)

$ErrorActionPreference = 'Stop'

if (-not $VcRedistDirectory) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if ($installation) {
            $VcRedistDirectory = Get-ChildItem -Path "$installation/VC/Redist/MSVC/*/x64/Microsoft.VC143.CRT" -Directory |
                Sort-Object { [version]$_.Parent.Parent.Name } -Descending |
                Select-Object -First 1 -ExpandProperty FullName
        }
    }
}
if (-not $VcRedistDirectory) {
    throw 'Укажите папку Microsoft.VC143.CRT для x64 через -VcRedistDirectory.'
}

$crtFiles = 'msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll'
foreach ($file in $crtFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $VcRedistDirectory $file) -PathType Leaf)) {
        throw "В папке Visual C++ Runtime отсутствует $file."
    }
}

dotnet publish "$PSScriptRoot/src/DrawingRenamer" -c Release -p:RestoreLockedMode=true -o $Output
if ($LASTEXITCODE -ne 0) { throw 'Ошибка публикации Drawing Renamer.' }

foreach ($file in $crtFiles) {
    Copy-Item -LiteralPath (Join-Path $VcRedistDirectory $file) -Destination $Output -Force
}
Copy-Item -LiteralPath "$PSScriptRoot/README.md" -Destination $Output -Force
$docs = New-Item -ItemType Directory -Path (Join-Path $Output 'docs') -Force
foreach ($file in 'architecture.md', 'testing.md', 'template-example.json') {
    Copy-Item -LiteralPath "$PSScriptRoot/docs/$file" -Destination $docs.FullName -Force
}
$licenseDocs = New-Item -ItemType Directory -Path (Join-Path $docs.FullName 'licenses') -Force
Copy-Item -LiteralPath "$PSScriptRoot/docs/licenses/README.md" -Destination $licenseDocs.FullName -Force

$required = @('DrawingRenamer.exe', 'DrawingRenamer.pri', 'coreclr.dll', 'pdfium.dll',
    'x64/tesseract55.dll', 'x64/leptonica-1.85.0.dll', 'tessdata/eng.traineddata', 'tessdata/rus.traineddata')
foreach ($file in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $Output $file) -PathType Leaf)) {
        throw "В публикации отсутствует $file."
    }
}
