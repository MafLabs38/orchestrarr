# The image of the FREE agent of the staging world (ADR 0048): Claude Code with the tools of a developer (git, a shell, the docker client), run as root in a container of a
# staging machine, which is isolated from the local network and thrown away after the job. The socket of the machine's own rootless docker is mounted by the Runner, so the agent can
# build and run what it needs. The only secret in it at run time is the token of the subscription. Build it where the Runner runs: docker build -t orchestrarr-agent-free docker/agent-free
FROM node:22-bookworm-slim@sha256:5ec23b7c622b00547753bc6ffd78085dd868a68aeb41c8e0a1418f033e13d96d
ARG CLAUDE_CODE_VERSION=2.1.322
ARG DOCKER_VERSION=27.3.1
# The checksum of the tarball of that version: a new version is a change to both lines, never to one
ARG DOCKER_SHA256=9b4f6fe406e50f9085ee474c451e2bb5adb119a03591f467922d3b4e2ddf31d3
RUN apt-get update \
    && apt-get install -y --no-install-recommends git curl ca-certificates jq procps python3 python3-pip build-essential \
    && rm -rf /var/lib/apt/lists/* \
    && curl -fsSL -o /tmp/docker.tgz "https://download.docker.com/linux/static/stable/x86_64/docker-${DOCKER_VERSION}.tgz" \
    && echo "${DOCKER_SHA256}  /tmp/docker.tgz" | sha256sum -c - \
    && tar -xz --strip-components=1 -C /usr/local/bin -f /tmp/docker.tgz docker/docker && rm -f /tmp/docker.tgz \
    && npm install -g @anthropic-ai/claude-code@${CLAUDE_CODE_VERSION} && npm cache clean --force
WORKDIR /work
