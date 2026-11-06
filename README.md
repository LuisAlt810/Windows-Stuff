# Windows-Stuff

Windows-Stuff is a small, opt-in Windows AppX package manager. It scans installed
and provisioned packages, lets you queue exact packages for removal, shows live
PowerShell output, and minimizes to the notification area. It does not delete
files or edit the registry/services.

## Safety model

- The application manifest requests administrator elevation at startup, and the
	app verifies that its token is elevated before enabling management actions.
- Scanning is read-only. Package changes require checking individual rows,
	clicking **Remove selected**, and confirming the exact package identities.
	Every scanned row can be checked; the worker clearly skips packages Windows
	marks as protected or as frameworks.
- A system restore point is requested before changes by default. If that request
	fails, the worker stops before performing any removals. Windows must have
	System Protection enabled for `Checkpoint-Computer` to succeed.
- Standard removal uses `Remove-AppxPackage -AllUsers` and removes a matching
	provisioned package from the online image. Force Mode is off by default and
	only attempts to stop processes whose executable paths are inside a selected
	package's install directory before normal package servicing.
- Force Mode does not bypass Windows package locks, alter registry ownership,
  or touch files under Windows or WindowsApps. Those approaches can damage
  servicing and updates; protected and framework packages are reported as skipped.
- There are no service, policy, or registry toggles in this initial version.
	Their values and behavior vary by Windows release, so the app does not expose
	speculative system-wide switches as if they were universal.

## Architecture

- **WinForms UI:** Dashboard, Packages, and Activity views; the Packages view has
	three category tabs, package metadata/details, installed/provisioned state,
	search, bulk selection, and queue clearing. Dashboard supports CSV/JSON export,
	category filters, and saved queue profiles. Activity history is persisted under
	`%LOCALAPPDATA%\Windows-Stuff`. Closing the window hides it; Exit is explicit.
- **Elevated process:** `app.manifest` requests `requireAdministrator`. The UI
	verifies the current Windows principal before enabling package operations.
	PowerShell is launched as a child process and inherits the app's elevated
	token; it is not separately re-elevated.
- **Worker and validator:** the embedded `Worker.ps1` accepts one JSON request
	on stdin. The C# side sends a typed action and exact package full names, never
	user-authored PowerShell. The worker validates the action and each identity,
	skips protected/framework packages, and reports removal failures per package
	while continuing the batch. Structured JSON-lines events stream to the UI
	without blocking its thread.

## Build and publish

On Windows with the .NET 10 SDK:

```powershell
dotnet publish .\WindowsStuff\WindowsStuff.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The executable is written under `WindowsStuff\bin\Release\net10.0-windows\win-x64\publish\`.
The app is Windows-only; building its WinForms target on Linux may require
downloading the Windows Desktop reference pack and does not allow running it.

## PowerShell worker functions

The implementation lives in [`WindowsStuff/Worker.ps1`](WindowsStuff/Worker.ps1).
Its core functions are:

- `Get-InstalledPackages`: read-only inventory across users.
- `Remove-SelectedPackages`: preflight all selected exact identities, optionally
	request a restore point, then remove installed and provisioned instances.
- `Remove-SelectedPackagesForce`: stop only processes running from the selected
	package install directories, then use the same supported removal cmdlets.

The force path is intentionally not an unlock/bypass path. `-AllUsers` affects
installed package registrations; `Remove-AppxProvisionedPackage -Online` removes
the image-provisioned copy so it is not reinstalled for newly created users.

## User data

Settings and the rolling activity log are stored under
`%LOCALAPPDATA%\Windows-Stuff`. Inventory exports are written only to a path
chosen by the user.