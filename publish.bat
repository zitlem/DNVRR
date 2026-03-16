@echo off
dotnet publish DNVRR -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
copy "DNVRR\bin\Release\net9.0-windows\win-x64\publish\DNVRR.exe" "%~dp0"
echo.
echo DNVRR.exe copied to %~dp0
echo SDK DLLs are embedded and will auto-extract on first run.
pause
