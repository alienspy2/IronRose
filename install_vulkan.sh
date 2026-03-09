#!/bin/bash
set -e

echo "=== IronRose: Vulkan 설치 (NVIDIA / Linux Mint) ==="

sudo apt update
sudo apt install -y \
  vulkan-tools \
  libvulkan1 \
  libvulkan-dev \
  vulkan-validationlayers \
  nvidia-driver-$(ubuntu-drivers devices 2>/dev/null | grep -oP 'nvidia-driver-\K\d+' | sort -rn | head -1)

echo ""
echo "=== 설치 확인 ==="
vulkaninfo --summary

echo ""
echo "완료. 드라이버 적용을 위해 재부팅을 권장합니다."
