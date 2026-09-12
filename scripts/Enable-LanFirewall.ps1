#Requires -RunAsAdministrator
param([string]$ProgramPath = (Join-Path $PSScriptRoot 'SingularitySync.exe'))
$ErrorActionPreference = 'Stop'
$resolvedProgram = (Resolve-Path -LiteralPath $ProgramPath).Path
if ([IO.Path]::GetFileName($resolvedProgram) -ne 'SingularitySync.exe') { throw 'Select the published SingularitySync.exe.' }
foreach ($rule in @(
    @{ Name = 'SingularitySync-LAN-TCP'; Protocol = 'TCP'; Port = 45831 },
    @{ Name = 'SingularitySync-LAN-UDP'; Protocol = 'UDP'; Port = 45832 }
)) {
    $existing = Get-NetFirewallRule -Name $rule.Name -ErrorAction SilentlyContinue
    if ($existing) { $existing | Remove-NetFirewallRule }
    New-NetFirewallRule -Name $rule.Name -DisplayName $rule.Name -Direction Inbound -Action Allow -Profile Private -Protocol $rule.Protocol -LocalPort $rule.Port -RemoteAddress LocalSubnet -Program $resolvedProgram | Out-Null
}
Write-Host 'Private-network, local-subnet rules created for this executable. Keep the executable at this location.'
