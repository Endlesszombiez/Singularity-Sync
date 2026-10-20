$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try {
    $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
    if ($request.Action -eq 'connections') {
        try { Get-SmbConnection | Select-Object ServerName, ShareName, UserName, Dialect, Encrypted, NumOpens | Format-Table -AutoSize | Out-String | Write-Output }
        catch {
            Write-Output 'Connections in this Windows session (SMB detail requires additional Windows privileges):'
            & "$env:SystemRoot\System32\net.exe" use
            if ($LASTEXITCODE -ne 0) { throw 'Unable to list Windows connections.' }
        }
        Write-Output 'To remove a conflict: close its files, enter that server and share (including IPC$ if listed) in the form, leave drive blank or select its mapped letter, then use Disconnect selected. No other connections will be removed.'
        exit 0
    }
    if ($request.Share -notmatch '\A[a-zA-Z0-9][a-zA-Z0-9_-]{0,39}\z' -or
        $request.User -notmatch '\A[a-zA-Z][a-zA-Z0-9_-]{0,19}\z') { throw 'Invalid share or local account name.' }
    $marker = 'SingularitySync SMB: ' + $request.Share
    $ruleName = 'SingularitySync-SMB-' + $request.Share
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Server setup requires administrator access.' }
    $share = Get-SmbShare -Name $request.Share -ErrorAction SilentlyContinue
    $user = Get-LocalUser -Name $request.User -ErrorAction SilentlyContinue
    if ($request.Action -eq 'remove') {
        if (($share -and $share.Description -ne $marker) -or ($user -and $user.Description -ne $marker)) { throw 'This share/account pair was not created by this app. Nothing was removed.' }
        $path = if ($share) { $share.Path } else { (Get-Item -LiteralPath $request.Folder -Force).FullName }
        $markerPath = Join-Path $path '.singularity-smb'
        if (-not (Test-Path -LiteralPath $markerPath) -or [IO.File]::ReadAllText($markerPath) -ne $marker) { throw 'The managed folder marker does not match. Select the original server folder, share and account. Nothing was removed.' }
        $ancestor = Get-Item -LiteralPath $path -Force
        while ($ancestor) {
            if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The managed folder path is now a link or junction. Nothing was removed.' }
            $ancestor = $ancestor.Parent
        }
        # Refuse removal if any SMB file is open beneath this folder, including on another share.
        $prefix = $path.TrimEnd('\') + '\'
        if (Get-SmbOpenFile | Where-Object { $_.Path -eq $path -or $_.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) }) { throw 'Files are open in this folder. Close CrossLink on all workstations before removing access.' }
        # Keep the marker until all cleanup succeeds, so an interrupted removal can be retried.
        if ($share) { Remove-SmbShare -Name $request.Share -Force }
        if ($user) {
            $acl = Get-Acl -LiteralPath $path
            $acl.PurgeAccessRules($user.SID)
            Set-Acl -LiteralPath $path -AclObject $acl
        }
        Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        if ($user) { Remove-LocalUser -SID $user.SID }
        $markerPath = Join-Path $path '.singularity-smb'
        if ((Test-Path -LiteralPath $markerPath) -and [IO.File]::ReadAllText($markerPath) -eq $marker) { Remove-Item -LiteralPath $markerPath }
        Write-Output 'Managed share, account and firewall rule removed. Folder contents were not deleted.'
        exit 0
    }
    if ($request.Action -ne 'create') { throw 'Unknown action.' }
    if ($share -or $user -or (Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue)) { throw 'That share, account or firewall rule already exists. Existing accounts are never overwritten. Use your existing connection details or choose unused names.' }
    $folder = Get-Item -LiteralPath $request.Folder -Force
    if (-not $folder.PSIsContainer -or $folder.FullName.StartsWith('\\')) { throw 'Choose a local folder on this server.' }
    $path = $folder.FullName.TrimEnd('\')
    $blocked = @([IO.Path]::GetPathRoot($folder.FullName).TrimEnd('\'), $env:USERPROFILE, $env:PUBLIC, $env:ProgramData)
    foreach ($item in $blocked) { if ($item -and $path -eq $item.TrimEnd('\')) { throw 'Choose a dedicated application data folder, not a drive or profile root.' } }
    foreach ($item in @($env:windir, $env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ($item -and ($path -eq $item -or $path.StartsWith($item.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase))) { throw 'Windows and Program Files directories cannot be shared by this setup. Choose the supported CrossLink data folder.' }
    }
    $ancestor = $folder
    while ($ancestor) {
        if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'A folder ancestor is a link or junction. Choose a physical local folder.' }
        if ((Test-Path -LiteralPath (Join-Path $ancestor.FullName '.singularity-sync')) -or (Test-Path -LiteralPath (Join-Path $ancestor.FullName '.singularity-smb'))) { throw 'This folder overlaps an existing sync or direct-access folder.' }
        $ancestor = $ancestor.Parent
    }
    if (Get-ChildItem -LiteralPath $path -Force -Recurse -Attributes ReparsePoint | Select-Object -First 1) { throw 'This folder contains links or junctions; remove them from the selected share scope first.' }
    if (Get-ChildItem -LiteralPath $path -Force -Recurse | Where-Object { $_.Name -in @('.singularity-sync', '.singularity-smb') } | Select-Object -First 1) { throw 'This folder has sync metadata. Use a dedicated, non-synchronized CrossLink folder. Stop sync and migrate using CrossLink-supported procedures first.' }
    if (-not (Get-SmbServerConfiguration).EnableSMB2Protocol) { throw 'SMB2/3 is disabled by server configuration. This app will not override that policy.' }
    if (-not (Get-NetConnectionProfile | Where-Object NetworkCategory -eq 'Private')) { throw 'No Private network is active. Server access is limited to a trusted Private LAN; this app does not reclassify networks automatically.' }
    if ($request.Password.Length -lt 20) { throw 'Use a password of at least 20 characters.' }
    if ($path.Length -gt 220) { throw 'Choose a shorter server folder path.' }
    $oldAcl = Get-Acl -LiteralPath $path
    $oldSddl = $oldAcl.GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Access)
    $madeUser = $false; $madeShare = $false; $changedAcl = $false; $madeRule = $false; $madeMarker = $false
    $markerPath = Join-Path $path '.singularity-smb'
    try {
        $markerFile = [IO.File]::Open($markerPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        $madeMarker = $true
        try { $bytes = [Text.Encoding]::UTF8.GetBytes($marker); $markerFile.Write($bytes, 0, $bytes.Length) } finally { $markerFile.Dispose() }
        $secret = ConvertTo-SecureString $request.Password -AsPlainText -Force
        $user = New-LocalUser -Name $request.User -Password $secret -Description $marker -AccountNeverExpires -PasswordNeverExpires -UserMayNotChangePassword
        $madeUser = $true
        $acl = Get-Acl -LiteralPath $path
        $grant = [Security.AccessControl.FileSystemAccessRule]::new($user.SID, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($grant)
        $changedAcl = $true
        Set-Acl -LiteralPath $path -AclObject $acl
        Start-Service LanmanServer
        $account = $env:COMPUTERNAME + '\' + $request.User
        New-SmbShare -Name $request.Share -Path $path -Description $marker -ChangeAccess $account -EncryptData $true -CachingMode None | Out-Null
        $madeShare = $true
        New-NetFirewallRule -Name $ruleName -DisplayName $marker -Direction Inbound -Action Allow -Protocol TCP -LocalPort 445 -Profile Private -RemoteAddress LocalSubnet | Out-Null
        $madeRule = $true
        Write-Output ('Created encrypted SMB share: \\' + $env:COMPUTERNAME + '\' + $request.Share)
        Write-Output ('Workstation account: ' + $account)
        Write-Output 'Save the password shown in this window before closing. On each workstation enable Direct access, enter these details and connect. Share access continues when the app exits.'
        Write-Output 'Existing protected subfolders may need their permissions reviewed. Run the workstation access test and verify CrossLink from two PCs. No guest access or signing settings were weakened.'
    }
    catch {
        $failure = $_.Exception.Message
        $rollbackErrors = @()
        if ($madeShare) { try { Remove-SmbShare -Name $request.Share -Force } catch { $rollbackErrors += $_.Exception.Message } }
        if ($changedAcl) { try { $acl = Get-Acl -LiteralPath $path; $acl.SetSecurityDescriptorSddlForm($oldSddl, [Security.AccessControl.AccessControlSections]::Access); Set-Acl -LiteralPath $path -AclObject $acl } catch { $rollbackErrors += $_.Exception.Message } }
        if ($madeRule) { try { Remove-NetFirewallRule -Name $ruleName } catch { $rollbackErrors += $_.Exception.Message } }
        if ($madeUser) { try { Remove-LocalUser -Name $request.User } catch { $rollbackErrors += $_.Exception.Message } }
        if ($madeMarker) { try { Remove-Item -LiteralPath $markerPath } catch { $rollbackErrors += $_.Exception.Message } }
        if ($rollbackErrors.Count) { $failure += ' Cleanup needs attention: ' + ($rollbackErrors -join '; ') }
        throw $failure
    }
}
catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
