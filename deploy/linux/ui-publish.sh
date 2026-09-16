#/bin/bash
name="eazy-rag-ui"
server="docker.sie.net.cn"
server2="docker.jueyun.net"
version=$(date +%Y%m%d%H%M%S)
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
echo "done"