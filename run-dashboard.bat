@echo off
start "EIM API" cmd /c "cd ARM.Integration.Dashboard.API && dotnet run --launch-profile http"
start "EIM Web" cmd /c "cd ARM.Integration.Dashboard.WEB && dotnet run --launch-profile http"