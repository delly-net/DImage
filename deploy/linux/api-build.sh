#!/bin/bash
echo "[==========拉取代码==========]"
cd /project/delly-net/DImage
git checkout main
git pull
echo "[==========编译代码==========]"
mkdir -p /project/delly-net/DImage/publish/api/files
cd /project/delly-net/DImage/Api/DImage.Api
/usr/bin/dotnet build DImage.Api.csproj -c Release -r linux-musl-x64 -p:IsPackable=false -o /project/delly-net/DImage/publish/api/files
echo "[==========编译完成==========]"