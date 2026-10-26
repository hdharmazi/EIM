@echo off
REM AzureWebJobsStorage=UseDevelopmentStorage=true, so the timer triggers need Azurite on 127.0.0.1:10000-10002.
start "Azurite" cmd /c "azurite --silent --location .azurite --debug .azurite\debug.log"
timeout /t 3 /nobreak >nul
start "EIM API" cmd /c "cd ARM.Integration.Dashboard.API && dotnet run --launch-profile http"
start "EIM Frontend" cmd /c "cd ARM.Integration.Dashboard.Frontend && npm run dev"
start "Wilson Automation" cmd /c "cd Wilson.Integration.Automation && dotnet run --launch-profile Wilson.Integration.Automation"
REM Wilson Automation is pinned to 7071 by --port in its launch profile; the gateway is pinned to 7072 by Host:LocalHttpPort in its local.settings.json.
start "D365FSC Gateway" cmd /c "cd ARM.Integration.D365FSC.Gateway && dotnet run --launch-profile ARM.Integration.D365FSC.Gateway"
