# The image of the agent the Runner starts to repair a pull request: Claude Code, nothing else of the host.
# The Runner runs it as a non-root user with every capability dropped, the folder of the run mounted, and the token of the subscription in its
# environment. The base is pinned by digest (a tag moves, and this image holds the token at run time): Dependabot proposes the bump (see .github/dependabot.yml).
# Build it where the Runner runs: docker build -t orchestrarr-agent docker/agent
FROM node:22-bookworm-slim@sha256:5ec23b7c622b00547753bc6ffd78085dd868a68aeb41c8e0a1418f033e13d96d
ARG CLAUDE_CODE_VERSION=2.1.322
RUN npm install -g @anthropic-ai/claude-code@${CLAUDE_CODE_VERSION} && npm cache clean --force
USER node
WORKDIR /work
