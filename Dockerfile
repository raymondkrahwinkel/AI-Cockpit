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
    && apt-get install -y --no-install-recommends ca-certificates git gh openssh-client ripgrep sudo \
    && rm -rf /var/lib/apt/lists/* \
    && npm install --global "@anthropic-ai/claude-code@${CLAUDE_CODE_VERSION}" "@openai/codex@${CODEX_VERSION}" \
    && npm cache clean --force
COPY --from=build /out /app
# `app` is the base image's non-root user and runs the server. AC-1464: the CLIs and git run as `agent`, whose group
# `app` is in. /state stays app's alone (the server makes it 0700); /work holds the clones and worktrees both users write.
# The claude and codex volumes mount at both homes, so the server reads what the agent's CLI writes. Git trusts every
# repository: the two users share them by design, and this git (2.43) has no `/work/*` form.
RUN groupadd --gid 1700 agent \
    && useradd --uid 1700 --gid agent --home-dir /home/agent --create-home --shell /usr/sbin/nologin agent \
    && usermod --append --groups agent app \
    && mkdir -p /state /home/app/.ssh /home/agent/.ssh /work/clones /work/worktrees \
        /home/agent/.claude /home/agent/.codex /home/app/.claude /home/app/.codex \
    && chown -R app:app /state /home/app \
    && chown -R agent:agent /home/agent /home/app/.claude /home/app/.codex \
    && chmod 2770 /home/agent/.claude /home/agent/.codex /home/app/.claude /home/app/.codex \
    && chmod 700 /home/app/.ssh /home/agent/.ssh \
    && chmod 750 /home/app /home/agent \
    && chown -R app:agent /work \
    && chmod 2770 /work /work/clones /work/worktrees \
    && git config --system --add safe.directory '*'
COPY --chmod=0755 deploy/agent-wrapper.sh /opt/cockpit/bin/claude
COPY --chmod=0755 deploy/agent-wrapper.sh /opt/cockpit/bin/codex
COPY --chmod=0755 deploy/agent-wrapper.sh /opt/cockpit/bin/git
COPY --chmod=0440 deploy/sudoers-agent /etc/sudoers.d/cockpit-agent
COPY --chmod=0755 deploy/entrypoint.sh /opt/cockpit/entrypoint.sh
RUN visudo --check --file=/etc/sudoers.d/cockpit-agent
ENV HOME=/home/app
ENV PATH=/opt/cockpit/bin:$PATH
WORKDIR /work
EXPOSE 20383
# Starts as root, hands the server its secrets and drops to `app` (deploy/entrypoint.sh).
ENTRYPOINT ["/opt/cockpit/entrypoint.sh"]

FROM base AS dev
COPY --from=build /usr/share/dotnet/sdk /usr/share/dotnet/sdk
COPY --from=build /usr/share/dotnet/packs /usr/share/dotnet/packs
COPY --from=build /usr/share/dotnet/templates /usr/share/dotnet/templates

FROM base AS runtime
