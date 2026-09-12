#!/usr/bin/env pwsh
param([switch]$DryRun)
& (Join-Path $PSScriptRoot 'Release-Version.ps1') -Bump Minor -DryRun:$DryRun
