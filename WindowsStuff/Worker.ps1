$ErrorActionPreference = 'Stop'

function Write-Event {
    param(
        [Parameter(Mandatory)] [string] $Type,
        [Parameter(Mandatory)] [string] $Message,
        [object] $Data
    )

    $event = @{ type = $Type; message = $Message }
    if ($PSBoundParameters.ContainsKey('Data')) {
        $event.data = $Data
    }
    [Console]::Out.WriteLine((ConvertTo-Json -InputObject $event -Depth 8 -Compress))
}

function Get-InstalledPackages {
    Write-Event -Type log -Message 'Executing: Get-AppxPackage -AllUsers | Sort-Object PackageFullName -Unique'
    $installedPackages = @(Get-AppxPackage -AllUsers -ErrorAction Stop |
        Sort-Object -Property PackageFullName -Unique)
    Write-Event -Type log -Message 'Executing: Get-AppxProvisionedPackage -Online'
    $provisionedPackages = @(Get-AppxProvisionedPackage -Online -ErrorAction Stop)
    $provisionedByName = @{}
    foreach ($provisionedPackage in $provisionedPackages) {
        $provisionedByName[$provisionedPackage.DisplayName] = $provisionedPackage
    }

    $installedNames = @{}
    $packages = [System.Collections.Generic.List[object]]::new()
    foreach ($package in $installedPackages) {
        $installedNames[$package.Name] = $true
        $installedUsers = @($package.PackageUserInformation |
            Where-Object { $_.InstallState -eq 'Installed' }).Count
        $isProvisioned = $provisionedByName.ContainsKey($package.Name)
        $packages.Add([pscustomobject]@{
            displayName = $package.PackageFamilyName
            name = $package.Name
            packageFullName = $package.PackageFullName
            packageFamilyName = $package.PackageFamilyName
            publisher = $package.Publisher
            version = $package.Version.ToString()
            installLocation = $package.InstallLocation
            userScope = "Installed for $installedUsers user(s)"
            state = if ($isProvisioned) { 'Installed + provisioned' } else { 'Installed only' }
            isInstalled = $true
            isProvisioned = $isProvisioned
            isFramework = [bool]$package.IsFramework
            nonRemovable = [bool]$package.NonRemovable
        })
    }

    foreach ($package in $provisionedPackages) {
        if (-not $installedNames.ContainsKey($package.DisplayName)) {
            $packages.Add([pscustomobject]@{
                displayName = $package.DisplayName
                name = $package.DisplayName
                packageFullName = $package.PackageName
                packageFamilyName = $package.DisplayName
                publisher = $package.PublisherId
                version = $package.Version.ToString()
                installLocation = ''
                userScope = 'Provisioned for new users'
                state = 'Provisioned only'
                isInstalled = $false
                isProvisioned = $true
                isFramework = $false
                nonRemovable = $false
            })
        }
    }

    Write-Event -Type result -Message 'Package scan completed.' -Data $packages
}

function Get-ValidatedRemovalPlan {
    param([Parameter(Mandatory)] $Request)

    $identities = @($Request.packageFullNames | Select-Object -Unique)
    if ($identities.Count -eq 0) {
        throw 'No packages were selected.'
    }

    $plan = @()
    foreach ($identity in $identities) {
        if ($identity -isnot [string] -or $identity -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{1,250}$') {
            throw 'A selected package identity is invalid.'
        }

        Write-Event -Type log -Message "Executing: Get-AppxPackage -AllUsers | Where-Object PackageFullName -eq '$identity'"
        $blockedReason = $null
        $installed = @()
        try {
            $installed = @(Get-AppxPackage -AllUsers -ErrorAction Stop |
                Where-Object { $_.PackageFullName -ceq $identity })
        }
        catch {
            $blockedReason = $_.Exception.Message
        }
        if ($installed.Count -gt 0 -and @($installed | Where-Object { $_.NonRemovable -or $_.IsFramework }).Count -gt 0) {
            $blockedReason = 'Windows marks this package as protected or as a framework, so the worker will not attempt removal.'
        }

        $provisionedNames = @()
        try {
            Write-Event -Type log -Message "Executing: Get-AppxProvisionedPackage -Online | Where-Object PackageName -eq '$identity'"
            $provisionedNames = @(Get-AppxProvisionedPackage -Online -ErrorAction Stop |
                Where-Object { $_.PackageName -ceq $identity } |
                ForEach-Object { $_.PackageName })
        }
        catch {
            Write-Event -Type log -Message "Provisioned package scan unavailable for '$identity': $($_.Exception.Message)"
        }
        if ($installed.Count -eq 0 -and $provisionedNames.Count -eq 0 -and -not $blockedReason) {
            $blockedReason = 'Package is no longer installed or provisioned. Scan again before making changes.'
        }
        $plan += [pscustomobject]@{
            Identity = $identity
            InstallLocation = if ($installed.Count -gt 0) { $installed[0].InstallLocation } else { $null }
            IsInstalled = $installed.Count -gt 0
            ProvisionedNames = $provisionedNames
            BlockedReason = $blockedReason
        }
    }

    return $plan
}

function Remove-SelectedPackages {
    param([Parameter(Mandatory)] $Request)

    Invoke-SelectedPackageRemoval -Request $Request -ForceMode $false
}

function Remove-SelectedPackagesForce {
    param([Parameter(Mandatory)] $Request)

    Invoke-SelectedPackageRemoval -Request $Request -ForceMode $true
}

function Invoke-SelectedPackageRemoval {
    param(
        [Parameter(Mandatory)] $Request,
        [Parameter(Mandatory)] [bool] $ForceMode
    )

    $plan = @(Get-ValidatedRemovalPlan -Request $Request)

    if ($Request.createRestorePoint) {
        Write-Event -Type log -Message "Executing: Checkpoint-Computer -Description 'Windows-Stuff package removal' -RestorePointType MODIFY_SETTINGS"
        $null = Checkpoint-Computer -Description 'Windows-Stuff package removal' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop
        Write-Event -Type log -Message 'System restore point created.'
    }

    $removed = 0
    $failed = 0
    foreach ($target in $plan) {
        if ($target.BlockedReason) {
            $failed++
            Write-Event -Type log -Message "Skipped '$($target.Identity)': $($target.BlockedReason)"
            continue
        }

        try {
            if ($ForceMode -and $target.InstallLocation -and (Test-Path -LiteralPath $target.InstallLocation)) {
                Write-Event -Type log -Message "Executing: Get-Process | filter executable paths under '$($target.InstallLocation)'"
                $packagePath = [IO.Path]::GetFullPath($target.InstallLocation).TrimEnd('\') + '\'
                $processes = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
                    $_.Path -and $_.Path.StartsWith($packagePath, [StringComparison]::OrdinalIgnoreCase)
                })
                foreach ($process in $processes) {
                    Write-Event -Type log -Message "Executing: Stop-Process -Id $($process.Id) -Force"
                    try {
                        Stop-Process -Id $process.Id -Force -ErrorAction Stop
                        Write-Event -Type log -Message "Stopped process $($process.Id)."
                    }
                    catch {
                        Write-Event -Type log -Message "Could not stop process $($process.Id): $($_.Exception.Message)"
                    }
                }
            }

            if ($target.IsInstalled) {
                Write-Event -Type log -Message "Executing: Remove-AppxPackage -Package '$($target.Identity)' -AllUsers"
                Remove-AppxPackage -Package $target.Identity -AllUsers -ErrorAction Stop
                Write-Event -Type log -Message "Remove-AppxPackage completed for '$($target.Identity)'."
            }

            foreach ($packageName in $target.ProvisionedNames) {
                Write-Event -Type log -Message "Executing: Remove-AppxProvisionedPackage -Online -PackageName '$packageName'"
                $result = Remove-AppxProvisionedPackage -Online -PackageName $packageName -ErrorAction Stop
                Write-Event -Type log -Message "Provisioned removal completed. RestartNeeded=$($result.RestartNeeded)"
            }

            $removed++
            Write-Event -Type log -Message "Removal completed for '$($target.Identity)'."
        }
        catch {
            $failed++
            Write-Event -Type log -Message "Failed '$($target.Identity)': $($_.Exception.Message)"
        }
    }

    Write-Event -Type result -Message "Removal run complete. Removed: $removed. Skipped or failed: $failed." -Data @{ removed = $removed; failed = $failed }
}

try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'The PowerShell worker is not running with administrator privileges.'
    }

    $requestLine = [Console]::In.ReadLine()
    if ([string]::IsNullOrWhiteSpace($requestLine)) {
        throw 'Worker request was empty.'
    }
    $request = $requestLine | ConvertFrom-Json -ErrorAction Stop

    switch ($request.action) {
        'scan' {
            Get-InstalledPackages
        }
        'remove' {
            if ($request.forceMode -eq $true) {
                Remove-SelectedPackagesForce -Request $request
            }
            else {
                Remove-SelectedPackages -Request $request
            }
        }
        default { throw 'Unsupported worker action.' }
    }
}
catch {
    Write-Event -Type error -Message $_.Exception.Message
    exit 1
}