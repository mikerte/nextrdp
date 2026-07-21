@{
    RootModule = 'NextRdp.psm1'
    ModuleVersion = '0.2.0'
    GUID = 'd75eb7df-a032-47fe-b903-e998b43ba0fd'
    Author = 'NextRDP'
    CompanyName = 'NextRDP'
    Copyright = '(c) 2026 NextRDP. All rights reserved.'
    Description = 'Credential-safe Phase 2 automation for the NextRDP control plane.'
    PowerShellVersion = '5.1'
    FunctionsToExport = @('Connect-NextRdpControlPlane','Disconnect-NextRdpControlPlane','Get-NextRdpOrganization','Get-NextRdpWorkspace','Get-NextRdpConnection','Sync-NextRdpWorkspace','Export-NextRdpInventory','Invoke-NextRdpBulkProvision','Test-NextRdpGateway')
    CmdletsToExport = @()
    VariablesToExport = @()
    AliasesToExport = @()
    PrivateData = @{ PSData = @{ Tags = @('RDP','NextRDP','Automation') } }
}
