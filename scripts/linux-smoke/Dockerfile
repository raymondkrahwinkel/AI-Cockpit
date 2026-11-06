FROM ubuntu:24.04
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends \
    xvfb xdotool imagemagick x11-utils python3 ca-certificates curl procps \
    libgl1 libgl1-mesa-dri libegl1 libglx-mesa0 libgbm1 libnss3 libnspr4 libasound2t64 libatk1.0-0 libatk-bridge2.0-0 \
    libxkbcommon0 libxkbcommon-x11-0 libgtk-3-0t64 libxcomposite1 libxdamage1 libxrandr2 libxfixes3 libxext6 libxi6 libxtst6 libxcursor1 \
    libice6 libsm6 libfontconfig1 libicu74 libssl3t64 fonts-dejavu-core xz-utils \
    && rm -rf /var/lib/apt/lists/*
