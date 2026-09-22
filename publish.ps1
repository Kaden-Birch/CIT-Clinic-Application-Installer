$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
dotnet test tests/CITDeploy.Tests/CITDeploy.Tests.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
dotnet publish src/CITDeploy.Windows/CITDeploy.Windows.csproj -c Release -r win-x64 --self-contained true -o artifacts/CITDeploy -p:PublishSingleFile=false
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
Copy-Item README.md artifacts/CITDeploy/README.md
Compress-Archive -Path artifacts/CITDeploy -DestinationPath artifacts/CITDeploy-win-x64.zip -Force
Write-Host 'Copy the complete artifacts/CITDeploy directory to USB. Launch CITDeploy.exe on Windows 11 Pro/Enterprise x64.'
