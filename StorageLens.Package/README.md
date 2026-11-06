# Storage Lens packaging project

`StorageLens.Package.wapproj` is the Windows Application Packaging Project for
the app. It references the WinUI executable, keeps the package manifest and
visual assets in the packaging payload, and supports x86, x64, and ARM64.

Build it from a Visual Studio Developer PowerShell:

```powershell
msbuild .\StorageLens.Package.wapproj `
  -restore -t:Build `
  -p:Configuration=Release `
  -p:Platform=x64 `
  -p:GenerateAppxPackageOnBuild=true `
  -p:AppxBundle=Never `
  -p:UapAppxPackageBuildMode=SideloadOnly
```

The default build deliberately does not sign the package. Use your release
certificate and signing pipeline at distribution time.
