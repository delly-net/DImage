#!/bin/bash
echo "[==========拉取代码==========]"
cd /project/eazy-ai/eazy-rag
git checkout main
git pull

echo "[==========编译后端代码==========]"
mkdir -p /project/eazy-ai/publish/eazy-rag-api/files
cd /project/eazy-ai/eazy-rag/api
/usr/bin/dotnet build EazyRag.Api.csproj -c Release -r linux-musl-x64 -p:IsPackable=false -o /project/eazy-ai/publish/eazy-rag-api/files

echo "[==========更新前端依赖==========]"
cd /project/eazy-ai/eazy-rag/ui
npm install
echo "[==========编译前端代码==========]"
npm run build
echo "[==========复制前端文件==========]"
mkdir -p /project/eazy-ai/publish/eazy-rag-ui/files
rm -rf /project/eazy-ai/publish/eazy-rag-ui/files/*
cp -r ./dist/* /project/eazy-ai/publish/eazy-rag-ui/files/

version=$(date +%Y%m%d%H%M%S)

echo "[==========发布后端镜像==========]"
name="eazy-rag-api"
server="docker.sie.net.cn"
server2="docker.jueyun.net"
cd /project/eazy-ai/publish/eazy-rag-api
echo "[+++] docker build -t $name:$version ."
docker build -t $name:$version .
# 推送到docker.sie.net.cn
echo "[>>>] docker tag $name:$version $server/$name:$version"
docker tag $name:$version $server/$name:$version
echo "[>>>] docker push $server/$name:$version"
docker push $server/$name:$version
# 推送到docker.jueyun.net
echo "[>>>] docker tag $name:$version $server2/$name:$version"
docker tag $name:$version $server2/$name:$version
echo "[>>>] docker push $server2/$name:$version"
docker push $server2/$name:$version
# 移除本地镜像
echo "[---] docker rmi $name:$version"
docker rmi $name:$version
echo "[---] docker rmi $server/$name:$version"
docker rmi $server/$name:$version
echo "[---] docker rmi $server2/$name:$version"
docker rmi $server2/$name:$version

echo "[==========发布前端镜像==========]"
name="eazy-rag-ui"
server="docker.sie.net.cn"
server2="docker.jueyun.net"
cd /project/eazy-ai/publish/eazy-rag-ui
echo "[+++] docker build -t $name:$version ."
docker build -t $name:$version .
# 推送到docker.sie.net.cn
echo "[>>>] docker tag $name:$version $server/$name:$version"
docker tag $name:$version $server/$name:$version
echo "[>>>] docker push $server/$name:$version"
docker push $server/$name:$version
# 推送到docker.jueyun.net
echo "[>>>] docker tag $name:$version $server2/$name:$version"
docker tag $name:$version $server2/$name:$version
echo "[>>>] docker push $server2/$name:$version"
docker push $server2/$name:$version
# 移除本地镜像
echo "[---] docker rmi $name:$version"
docker rmi $name:$version
echo "[---] docker rmi $server/$name:$version"
docker rmi $server/$name:$version
echo "[---] docker rmi $server2/$name:$version"
docker rmi $server2/$name:$version

echo "[==========操作完成==========]"