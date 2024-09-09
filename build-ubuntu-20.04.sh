#!/bin/bash

(cd src/Miningcore && \
BUILDIR=${1:-../../../poolbackend} && \
echo "Building into $BUILDIR" && \
dotnet publish -c Release --framework net6.0 -o $BUILDIR)
