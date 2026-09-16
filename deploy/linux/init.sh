#!/bin/bash
mkdir -p /project/delly-net
cd /project/delly-net
git clone git@github.com:delly-net/DImage.git
cd /project/delly-net/DImage
git checkout --track origin/main