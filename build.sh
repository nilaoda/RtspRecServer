#!/usr/bin/env bash
set -e
dotnet publish src/backend/Server/Server.csproj -c Release
