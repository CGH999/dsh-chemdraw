param(
    [string]$ChemDrawHome = $env:CHEMDRAW_HOME,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot   # bridge/ 的上一级，也就是包根目录
$src = Join-Path $PSScriptRoot 'src'
$bin = Join-Path $root 'bin'
New-Item -ItemType Directory -Force -Path $bin | Out-Null

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }

if ([string]::IsNullOrEmpty($ChemDrawHome)) {
    throw "ChemDraw not found: set CHEMDRAW_HOME to the folder containing ChemScript and ChemDraw"
}
$frameworkDir = Split-Path $csc
$chemScriptNet = Join-Path $ChemDrawHome 'ChemScript\Lib\Net\CambridgeSoft.ChemScript.dll'
if (-not (Test-Path $chemScriptNet)) { throw "ChemScript .NET assembly not found: $chemScriptNet" }

function Find-FrameworkDll([string]$name) {
    $candidates = @(
        (Join-Path $frameworkDir $name),
        (Join-Path $env:WINDIR "assembly\GAC_MSIL\$($name -replace '\.dll$','')\*\*\$name"),
        (Join-Path $env:WINDIR "Microsoft.NET\assembly\GAC_MSIL\$($name -replace '\.dll$','')\*\*\$name")
    )
    foreach ($c in $candidates) {
        $hit = Get-Item $c -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    return $null
}

$refs = @($chemScriptNet)
foreach ($name in 'System.Web.Extensions.dll', 'System.Drawing.dll', 'System.Xml.dll') {
    $p = Find-FrameworkDll $name
    if ($p) { $refs += $p }
    elseif (-not $Quiet) { Write-Warning "reference not located: $name" }
}

$out = Join-Path $bin 'ChemBridge.exe'
$sources = Get-ChildItem -Path $src -Filter *.cs | Select-Object -ExpandProperty FullName
if (-not $sources) { throw "no sources in $src" }

$cscArgs = @('/nologo', '/target:exe', '/platform:x64', '/optimize+', '/warn:2', "/out:$out", '/utf8output')
foreach ($r in $refs) { $cscArgs += "/r:$r" }
$cscArgs += $sources

if (-not $Quiet) { & $csc @cscArgs }
else { & $csc @cscArgs 2>&1 | Out-Null }

if ($LASTEXITCODE -ne 0) { throw "compile failed (exit $LASTEXITCODE)" }

# The managed wrapper must sit next to the executable so CLR probing finds it.
Copy-Item -LiteralPath $chemScriptNet -Destination (Join-Path $bin 'CambridgeSoft.ChemScript.dll') -Force

if (-not $Quiet) {
    $info = Get-Item $out
    "built: $($info.FullName)  ($([math]::Round($info.Length/1024,1)) KB)"
}
