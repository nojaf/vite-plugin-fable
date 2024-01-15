#!/bin/bash
dotnet publish ./Fable.Daemon/Fable.Daemon.fsproj -c Release -r linux-x64 -p:PublishReadyToRun=true