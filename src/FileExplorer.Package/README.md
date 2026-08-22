# FileExplorer.Package

Windows Application Packaging Project (`.wapproj`) that wraps the unpackaged `FileExplorer`
app as an MSIX, for Microsoft Store submission (and local sideload testing). This does **not**
change how `FileExplorer.csproj` itself builds or ships — the plain self-contained
`enfyl-explorer.exe` distribution described in the top-level README is untouched; this is a
second, additional packaging output that lives entirely in this folder.

## Current status: placeholder identity

`Package.appxmanifest` currently has placeholder values:

```xml
<Identity
  Name="AndrewRigney.arExxPro"
  Publisher="CN=arExxProDevTest"
  Version="1.0.0.0" />
```

These **must** be replaced with the real values once a Partner Center account exists and the
app name is reserved:

1. In Visual Studio, right-click `FileExplorer.Package` → **Publish → Associate App with the
   Store...**, sign in, and pick the reserved app name. This rewrites `Name`, `Publisher`, and
   `Properties/DisplayName`/`PublisherDisplayName` for you.
2. Or edit `Package.appxmanifest` by hand — the exact `Name` and `Publisher` (a `CN=...`
   string) are both shown on the app's Identity page in Partner Center after reservation.

`Version` (a 4-part `Major.Minor.Build.Revision`) is unrelated to the app's own
`major.minor.build` version shown in Control Centre → About (that one is stamped into the exe
itself via `FileExplorer.csproj`'s `SetBuildNumber` target). The Store requires each submitted
package's `Identity/Version` to strictly increase between submissions — bump it by hand before
each Store upload.

## Local test certificate

MSIX packages must be signed to build or install at all — this is separate from what happens
during Store certification, where Partner Center re-signs the package with its own certificate
regardless of what it was signed with on upload. A self-signed certificate is exactly what's
needed here; it does not need to be a "real" one.

`FileExplorer.Package_TemporaryKey.pfx` (gitignored — never commit a `.pfx`) was generated
locally with:

```powershell
New-SelfSignedCertificate -Type Custom -Subject "CN=arExxProDevTest" -KeyUsage DigitalSignature `
    -FriendlyName "arExx Pro Dev Test Certificate" -CertStoreLocation "Cert:\CurrentUser\My" `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}Subject Type:End Entity")
```

then exported with password `arExxDevTest1` (thumbprint referenced in the `.wapproj`'s
`PackageCertificateThumbprint`). If this file is ever lost, regenerate it the same way — the
`Subject` just needs to keep matching `Package.appxmanifest`'s `Identity/Publisher`.

**When the real Publisher arrives from Partner Center:** generate a new cert with that exact
`Subject`, update `Identity/Publisher` to match, and re-point `PackageCertificateKeyFile`/
`PackageCertificateThumbprint` at it (or clear both and let VS's Store-association step handle
it — Store-associated packages don't need a local signing cert at all for the final
`StoreUpload` build).

## Building

From this folder's parent, via VS's own MSBuild (see the top-level README for why):

```powershell
& "F:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" `
    "src\FileExplorer.Package\FileExplorer.Package.wapproj" `
    /p:Configuration=Release /p:Platform=x64 /p:AppxBundle=Always `
    /p:PackageCertificatePassword=arExxDevTest1 `
    /restore /t:Publish
```

**Use `Release`, not `Debug`, for anything you intend to actually sideload/run.** A Debug-config
MSIX depends on the *Debug* variant of the VCLibs framework, which only exists inside Visual
Studio's own deployment tooling — sideloading it standalone fails with `0x80073CF3` ("depends on
a framework that could not be found... Microsoft.VCLibs.140.00.Debug"). `F5`-ing from inside VS
works fine in Debug since VS supplies that framework itself; a plain MSBuild/PowerShell build for
manual install needs Release.

`UapAppxPackageBuildMode` defaults to `SideloadOnly` (baked into the `.wapproj`); pass
`/p:UapAppxPackageBuildMode=StoreUpload` to override for a real Store submission build, or just
use VS's **Publish → Create App Packages...** wizard, which walks through both Store-association
and version bumping.

Output lands in `AppPackages\FileExplorer.Package_<version>_Test\` (gitignored): the
`.msixbundle`, a `.cer` (the public half of the signing cert), and `Add-AppDevPackage.ps1`. That
script's own "developer license" check is broken on modern Windows 10/11 (it predates the
Developer Mode toggle replacing that whole concept) and fails with *"Could not acquire a
developer license"* even when everything it actually needs is fine. Skip it and do the three
steps by hand instead, as **Administrator**:

```powershell
# 1. Turn on Developer Mode (the modern replacement for the old "developer license")
reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock" /v AllowDevelopmentWithoutDevLicense /t REG_DWORD /d 1 /f

# 2. Trust the signing cert
Import-Certificate -FilePath "AppPackages\FileExplorer.Package_1.0.0.0_Test\FileExplorer.Package_1.0.0.0_x64.cer" -CertStoreLocation Cert:\LocalMachine\TrustedPeople

# 3. Install (VCLibs first if this machine doesn't already have it - Add-AppxPackage will say so if it's missing)
Add-AppxPackage -Path "AppPackages\FileExplorer.Package_1.0.0.0_Test\FileExplorer.Package_1.0.0.0_x64.msixbundle"
```

If step 3 fails with `0x80073D02` ("resources it modifies are currently in use" naming Widgets/
Web Experience/etc.), Explorer is holding a lock on the existing VCLibs version — restart it
(`Stop-Process -Name explorer -Force`, wait a couple seconds, `Start-Process explorer.exe`) and
retry.

### Two non-obvious fixes already baked into this project

Both cost real debugging time to track down, so they're recorded here in case anything ever needs
to touch them again:

1. **`Package.appxmanifest` declares a `PackageDependency` on `Microsoft.WindowsAppRuntime.1.6`.**
   Without it, the packaged app hard-crashes on launch (`0xc000027b` / `CLASS_E_CLASSNOTAVAILABLE`
   inside `Microsoft.UI.Xaml.dll`, before any managed exception handler can run) — WinRT
   activation for the Windows App SDK's own runtime classes has nowhere to resolve to. Visual
   Studio's WAP project wizard normally adds this automatically; a hand-authored manifest (this
   one) doesn't get it for free.
2. **The `FileExplorer.csproj` `ProjectReference` carries
   `AdditionalProperties=WindowsAppSDKSelfContained=false;WindowsAppSDKBootstrapAutoInitializeOptions_OnPackageIdentity_NoOp=true`.**
   Self-contained deployment doesn't get correct MSIX WinRT registrations either (same crash as
   above). Framework-dependent fixes that, but then the Windows App SDK's auto-injected
   `[ModuleInitializer]` — which calls the Bootstrapper API meant for *unpackaged* apps to locate
   the runtime manually — correctly refuses to run inside an already-packaged process
   (`ERROR_NOT_SUPPORTED`, `0x80070032`) and calls `Environment.Exit()` immediately: no window, no
   exception, nothing to catch, the app just silently does nothing on launch. The
   `OnPackageIdentity_NoOp` option makes that initializer a no-op instead once it detects package
   identity, deferring entirely to the `PackageDependency` above. `AdditionalProperties` (not a
   plain `PropertyGroup` value in this file) is required to forward these into the
   `FileExplorer.csproj` build specifically — a same-file `PropertyGroup` value does not
   automatically flow into a `ProjectReference`'s build the way a command-line global property
   does. Neither of these touches `FileExplorer.csproj` itself, so the plain unpackaged `.exe`
   build is unaffected either way.

## What still needs Partner Center (not code)

- Registering the developer account and paying the one-time fee
- Reserving the app name
- Store listing: description, screenshots, age rating questionnaire, privacy policy URL,
  pricing/availability
- Submitting the built `.msixbundle` for certification

None of that can be done from here — it's all on
[partner.microsoft.com](https://partner.microsoft.com/dashboard).
