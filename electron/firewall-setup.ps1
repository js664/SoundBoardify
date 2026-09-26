param(
    [Parameter(Mandatory = $true)][string]$Program,
    [Parameter(Mandatory = $true)][ValidateRange(1024, 65535)][int]$Port,
    [Parameter(Mandatory = $true)][bool]$LanAccess,
    [Parameter(Mandatory = $true)][bool]$TailscaleAccess
)

$ErrorActionPreference = 'Stop'
$group = 'SimplySound Web UI'
if (-not $LanAccess -and -not $TailscaleAccess) {
    throw 'Enable Wi-Fi/LAN or Tailscale access before creating a firewall rule.'
}
$remoteAddresses = @()
if ($LanAccess) {
    $remoteAddresses += 'LocalSubnet'
}
if ($TailscaleAccess) {
    $remoteAddresses += '100.64.0.0/10'
}

Get-NetFirewallRule -Group $group -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule `
    -DisplayName 'SimplySound Web UI' `
    -Group $group `
    -Direction Inbound `
    -Action Allow `
    -Protocol TCP `
    -LocalPort $Port `
    -Program $Program `
    -RemoteAddress $remoteAddresses `
    -Profile Any `
    -EdgeTraversalPolicy Block | Out-Null
