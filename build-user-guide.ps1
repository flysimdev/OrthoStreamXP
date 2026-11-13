# Builds the user guide PDF from the pages in docs: docs\OrthoStreamXP-UserGuide.pdf, attached to every GitHub Release.
# Run it after changing the docs and commit the PDF with them.
# Needs pandoc (winget install JohnMacFarlane.Pandoc) and Google Chrome or Microsoft Edge (prints the PDF).
#
#   .\build-user-guide.ps1              the version in src\OrthoStreamXP\OrthoStreamXP.csproj
#   .\build-user-guide.ps1 -Version 0.1.0
param([string]$Version)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$project = 'src\OrthoStreamXP\OrthoStreamXP.csproj'
if (-not $Version) { $Version = (dotnet msbuild $project -getProperty:Version).Trim() }

$pandoc = (Get-Command pandoc -ErrorAction SilentlyContinue).Source
if (-not $pandoc) { $pandoc = "$env:LOCALAPPDATA\Pandoc\pandoc.exe" }
if (-not (Test-Path $pandoc)) { throw 'pandoc was not found: winget install JohnMacFarlane.Pandoc' }

$browser = @(
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $browser) { throw 'Google Chrome or Microsoft Edge is needed to print the PDF' }

# The order of the guide's contents (docs\README.md). The pages keep their names so that pandoc's --file-scope
# turns the links between them (settings.md#quality) into links inside the PDF.
$pages = 'README', 'installation', 'first-setup', 'flying', 'simbrief', 'settings', 'cache', 'packages', 'troubleshooting', 'faq'
$work = 'artifacts\user-guide'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory $work | Out-Null

$texts = @{}
$titles = @{}
foreach ($page in $pages) {
    # Drop the "[User guide](README.md) › Page" breadcrumb: the PDF has a table of contents instead.
    $texts[$page] = (Get-Content "docs\$page.md" -Raw -Encoding utf8) -replace '(?m)^\[User guide\]\(README\.md\).*\r?\n', ''
    # The id pandoc gives the page's title heading ("# First setup" -> first-setup).
    $title = [regex]::Match($texts[$page], '(?m)^# (.+)$').Groups[1].Value.Trim().ToLowerInvariant()
    $titles[$page] = ($title -replace '[^\w\s-]', '' -replace '\s+', '-')
}
# The PDF's cover and table of contents replace the README's title and contents list.
$texts['README'] = $texts['README'] -replace '(?m)^# .+\r?\n', '' -replace '(?ms)^## Contents\r?\n.*?(?=^## )', ''
foreach ($page in $pages) {
    # A link to a whole page goes to its title: --file-scope only resolves links with an anchor.
    $text = [regex]::Replace($texts[$page], '\]\(([\w-]+)\.md\)', {
        param($m) $name = $m.Groups[1].Value
        if ($titles.ContainsKey($name)) { "]($name.md#$($titles[$name]))" } else { $m.Value }
    })
    Set-Content "$work\$page.md" $text -Encoding utf8
}

# From inside the work folder: --file-scope names the ids after the files (settings.md__quality), which is
# what it matches the links against.
Push-Location $work
try {
    & $pandoc ($pages | ForEach-Object { "$_.md" }) --file-scope --standalone --embed-resources `
        --resource-path="$PSScriptRoot\docs" --css "$PSScriptRoot\docs\user-guide.css" --toc --toc-depth=2 --metadata toc-title=Contents `
        --metadata title="OrthoStreamXP user guide" --metadata subtitle="Version $Version" `
        --metadata pagetitle="OrthoStreamXP $Version user guide" -o user-guide.html
    if ($LASTEXITCODE -ne 0) { throw 'pandoc failed' }
}
finally { Pop-Location }
$html = "$work\user-guide.html"

$pdf = "$PSScriptRoot\docs\OrthoStreamXP-UserGuide.pdf"
if (Test-Path $pdf) { Remove-Item $pdf }
Start-Process $browser -Wait -ArgumentList '--headless=new', '--disable-gpu', '--no-pdf-header-footer',
    "--print-to-pdf=`"$pdf`"", "`"$PSScriptRoot\$html`""
if (-not (Test-Path $pdf)) { throw 'the browser did not print the PDF' }
Write-Host "User guide: $pdf"
