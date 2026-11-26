# Rebuilds OrthoGlobalXP.Core.dll (with its XML documentation) next to this script: the engine of the Build Scenery
# page, which re-textures X-Plane 12's own Global Scenery tiles with photo terrain (src\OrthoStreamXP\Gui\ViewModels\
# BuildSceneryViewModel.cs). Needs the .NET 10 SDK and the OrthoGlobalXP sources, by default beside this repository.
#
#   .\lib\OrthoGlobalXP.Core\build.ps1 [-Source C:\path\to\OrthoGlobalXP]
param(
    [string]$Source = (Join-Path $PSScriptRoot '..\..\..\OrthoGlobalXP')
)
$ErrorActionPreference = 'Stop'

$project = Join-Path $Source 'src\OrthoGlobalXP.Core\OrthoGlobalXP.Core.csproj'
if (-not (Test-Path $project)) { throw "OrthoGlobalXP.Core not found at $project (pass -Source)" }

$out = Join-Path ([IO.Path]::GetTempPath()) "orthoglobalxp-core-$PID"
try {
    # CS1591, CS1573: the engine documents its public surface, not every member or parameter of it.
    dotnet build $project -c Release -nologo -o $out -p:GenerateDocumentationFile=true '-p:NoWarn=CS1591%3BCS1573'
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed ($LASTEXITCODE)" }
    foreach ($file in 'OrthoGlobalXP.Core.dll', 'OrthoGlobalXP.Core.xml') {
        Copy-Item (Join-Path $out $file) $PSScriptRoot -Force
    }
    # Its one package dependency must match SharpCompress's version in src\OrthoStreamXP\OrthoStreamXP.csproj.
    $sharpCompress = Select-Xml -Path $project -XPath "//PackageReference[@Include='SharpCompress']" | ForEach-Object { $_.Node.Version }
    Write-Host "OrthoGlobalXP.Core.dll updated (it references SharpCompress $sharpCompress)."
}
finally {
    Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
}
