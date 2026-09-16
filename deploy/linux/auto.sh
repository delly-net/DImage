#!/bin/bash
echo "[==========拉取代码==========]"
cd /project/delly-net/DImage
git checkout main
git pull

echo "[==========编译后端代码==========]"
mkdir -p /project/delly-net/DImage/publish/api/files
cd /project/delly-net/DImage/Api/DImage.Api
/usr/bin/dotnet build DImage.Api.csproj -c Release -r linux-musl-x64 -p:IsPackable=false -o /project/delly-net/DImage/publish/api/files

echo "[==========更新前端依赖==========]"
cd /project/delly-net/DImage/Vue
pnpm install
echo "[==========编译前端代码==========]"
pnpm build
echo "[==========复制前端文件==========]"
mkdir -p /project/delly-net/DImage/publish/ui/files
rm -rf /project/delly-net/DImage/publish/ui/files/*
cp -r ./dist/* /project/delly-net/DImage/publish/ui/files/

version=$(date +%Y%m%d%H%M%S)

echo "[==========发布后端镜像==========]"
name="dimage-api"
server="docker.sie.net.cn"
server2="docker.jueyun.net"
cd /project/delly-net/DImage/publish/api
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
name="dimage-ui"
server="docker.sie.net.cn"
server2="docker.jueyun.net"
cd /project/delly-net/DImage/publish/ui
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