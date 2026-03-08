@echo off
dotnet publish ConnectorManager\ConnectorManager.csproj -c Release --self-contained -r win-x64 -o bin\publish
