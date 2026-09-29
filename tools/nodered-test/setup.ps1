<#
.SYNOPSIS
    Instala y arranca el flow de prueba de Node-RED del PLC Sim Server.

.DESCRIPTION
    Copia package.json, settings.js y flows.json a C:\dev\nodered-plcsim, instala las dependencias ahí
    (node_modules NUNCA dentro de Google Drive) y arranca Node-RED en http://localhost:1880.
    Dashboard: http://localhost:1880/dashboard

    Si ya existe un flows.json en C:\dev\nodered-plcsim distinto del del repo, no lo pisa salvo con -Force
    (para no perder cambios hechos en el editor; usar export.ps1 para traerlos al repo).

.PARAMETER NoStart
    Solo instala, no arranca Node-RED.

.PARAMETER Endpoint
    Endpoint OPC UA del PLC Sim Server (por defecto opc.tcp://localhost:4840/plc-sim).

.PARAMETER Force
    Sobrescribe el flows.json de C:\dev\nodered-plcsim con el del repo.
#>
param(
    [switch]$NoStart,
    [string]$Endpoint = 'opc.tcp://localhost:4840/plc-sim',
    [switch]$Force,
    [string]$Target = 'C:\dev\nodered-plcsim'
)
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot

if ($Target -like 'G:\*') { throw "No instalar en Google Drive (G:). Ver G:\My Drive\Trabajo\LEEME-Compilar-fuera-de-Google-Drive.md" }
New-Item -ItemType Directory -Force $Target | Out-Null

Copy-Item "$src\package.json", "$src\settings.js" $Target -Force

$flowDst = Join-Path $Target 'flows.json'
$flow = Get-Content "$src\flows.json" -Raw -Encoding UTF8
$flow = $flow.Replace('opc.tcp://localhost:4840/plc-sim', $Endpoint)
if ((Test-Path $flowDst) -and -not $Force -and ((Get-Content $flowDst -Raw -Encoding UTF8) -ne $flow)) {
    Write-Warning "C:\dev ya tiene un flows.json distinto (cambios del editor?). Se conserva. Usar -Force para reemplazarlo, o export.ps1 para llevarlo al repo."
} else {
    [IO.File]::WriteAllText($flowDst, $flow, (New-Object Text.UTF8Encoding $false))
}

Push-Location $Target
# npm escribe advertencias en stderr; en Windows PowerShell 5.1 eso cortaria el script con 'Stop'.
$ErrorActionPreference = 'Continue'
try {
    npm install --no-fund --no-audit
    if ($LASTEXITCODE -ne 0) { throw "npm install fallo" }
    if (-not $NoStart) {
        Write-Host "Node-RED: editor http://localhost:1880  |  dashboard http://localhost:1880/dashboard  (Ctrl+C para salir)"
        npm start
    }
} finally {
    Pop-Location
}
