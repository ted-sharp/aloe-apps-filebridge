@echo off

cd /d %~dp0

REM Completely delete publish folder (Warning: all contents will be deleted)
rmdir /s /q publish

REM Publish FileBridge related projects

echo Publishing FileBridgeServer...
dotnet publish .\Aloe\Apps\FileBridge\Aloe.Apps.FileBridgeServer\Aloe.Apps.FileBridgeServer.csproj -c Release -r win-x64 --self-contained true -o .\publish\FileBridgeServer

echo Publishing FileBridgeClient...
dotnet publish .\Aloe\Apps\FileBridge\Aloe.Apps.FileBridge\Aloe.Apps.FileBridgeClient\Aloe.Apps.FileBridgeClient.csproj -c Release -r win-x64 --self-contained true -o .\publish\FileBridgeClient

echo Publishing DummyService...
dotnet publish .\Aloe\Apps\FileBridge\Aloe.Apps.DummyService\Aloe.Apps.DummyService.csproj -c Release -r win-x64 --self-contained true -o .\publish\DummyService

echo.
echo Completed.
pause
