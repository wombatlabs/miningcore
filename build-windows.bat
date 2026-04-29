@echo off
cd src\Miningcore
dotnet publish -c Release --framework net8.0 -o ../../build
