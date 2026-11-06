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
    $packages = @(
        Get-AppxPackage -AllUsers -ErrorAction Stop |
            Sort-Object -Property PackageFullName -Unique |
            ForEach-Object {
                [pscustomobject]@{
                    displayName = $_.PackageFamilyName
                    name = $_.Name
                    packageFullName = $_.PackageFullName
                    packageFamilyName = $_.PackageFamilyName
                    isFramework = [bool]$_.IsFramework
                    nonRemovable = [bool]$_.NonRemovable
                }
            }
    )
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
        $installed = @(Get-AppxPackage -AllUsers -ErrorAction Stop |
            Where-Object { $_.PackageFullName -ceq $identity })
        if ($installed.Count -eq 0) {
            throw "Package '$identity' is no longer installed. Scan again before making changes."
        }
        if (@($installed | Where-Object { $_.NonRemovable -or $_.IsFramework }).Count -gt 0) {
            throw "Package '$identity' is a Windows-protected or framework package. No changes were made."
        }

        Write-Event -Type log -Message "Executing: Get-AppxProvisionedPackage -Online | Where-Object PackageName -eq '$identity'"
        $provisionedNames = @(Get-AppxProvisionedPackage -Online -ErrorAction Stop |
            Where-Object { $_.PackageName -ceq $identity } |
            ForEach-Object { $_.PackageName })
        $plan += [pscustomobject]@{
            Identity = $identity
            InstallLocation = $installed[0].InstallLocation
            ProvisionedNames = $provisionedNames
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

    foreach ($target in $plan) {
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

        Write-Event -Type log -Message "Executing: Remove-AppxPackage -Package '$($target.Identity)' -AllUsers"
        Remove-AppxPackage -Package $target.Identity -AllUsers -ErrorAction Stop
        Write-Event -Type log -Message "Remove-AppxPackage completed for '$($target.Identity)'."

        foreach ($packageName in $target.ProvisionedNames) {
            Write-Event -Type log -Message "Executing: Remove-AppxProvisionedPackage -Online -PackageName '$packageName'"
            $result = Remove-AppxProvisionedPackage -Online -PackageName $packageName -ErrorAction Stop
            Write-Event -Type log -Message "Provisioned removal completed. RestartNeeded=$($result.RestartNeeded)"
        }

        Write-Event -Type log -Message "Removal completed for '$($target.Identity)'."
    }
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
    Write-Event -Type log -Message "ERROR: $($_.Exception.Message)"
    exit 1
}