# AC-1356: the headless Cockpit (Cockpit.Server) as a container. `runtime` is what runs and is the last stage, so a
# plain build gives it; `dev` adds the .NET SDK for agents that build and test. Base tags are build args, so a bump
# is one reviewed line. No secret is ever a build arg or an ENV: they reach the container as files (deploy/compose.yaml).
ARG DOTNET_TAG=10.0
ARG NODE_TAG=22-slim

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_TAG} AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Cockpit.Server/Cockpit.Server.csproj \
    --configuration Release --runtime linux-x64 --self-contained false --output /out

FROM node:${NODE_TAG} AS node

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_TAG} AS base
ARG CLAUDE_CODE_VERSION=2.1.288
ARG CODEX_VERSION=0.160.0
USER root
COPY --from=node /usr/local/ /usr/local/
# No docker CLI and no UI libraries: the server needs neither (teardown without docker only logs a notice).
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates git gh openssh-client ripgrep \
    && rm -rf /var/lib/apt/lists/* \
    && npm install --global "@anthropic-ai/claude-code@${CLAUDE_CODE_VERSION}" "@openai/codex@${CODEX_VERSION}" \
    && npm cache clean --force
COPY --from=build /out /app
# `app` is the base image's non-root user. State and the provider homes are chowned so a new named volume inherits it.
RUN mkdir -p /state /home/app/.claude /home/app/.codex /home/app/.ssh \
    && chmod 700 /home/app/.ssh \
    && chown -R app:app /state /home/app
ENV HOME=/home/app
WORKDIR /state
EXPOSE 20383
ENTRYPOINT ["/app/Cockpit.Server"]

FROM base AS dev
COPY --from=build /usr/share/dotnet/sdk /usr/share/dotnet/sdk
COPY --from=build /usr/share/dotnet/packs /usr/share/dotnet/packs
COPY --from=build /usr/share/dotnet/templates /usr/share/dotnet/templates
USER app

FROM base AS runtime
USER app
