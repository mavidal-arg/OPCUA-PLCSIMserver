<#
.SYNOPSIS
    Trae al repo el flows.json editado en Node-RED (C:\dev\nodered-plcsim\flows.json).
#>
param([string]$Target = 'C:\dev\nodered-plcsim')
$ErrorActionPreference = 'Stop'
Copy-Item (Join-Path $Target 'flows.json') (Join-Path $PSScriptRoot 'flows.json') -Force
Write-Host "flows.json copiado al repo. Revisar con 'git diff' antes de commitear."
