@echo off
start "EIM API" cmd /c "cd ARM.Integration.Dashboard.API && dotnet run --launch-profile http"
start "EIM Frontend" cmd /c "cd ARM.Integration.Dashboard.Frontend && npm run dev"