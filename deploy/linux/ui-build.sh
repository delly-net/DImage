#!/bin/bash
echo "[==========拉取代码==========]"
cd /project/delly-net/DImage
git checkout main
git pull
cd /project/delly-net/DImage/Vue
echo "[==========更新依赖==========]"
npm install
echo "[==========编译代码==========]"
npm run build
echo "[==========复制文件==========]"
mkdir -p /project/delly-net/DImage/publish/ui/files
rm -rf /project/delly-net/DImage/publish/ui/files/*
cp -r ./dist/* /project/delly-net/DImage/publish/ui/files/
echo "[==========编译完成==========]"