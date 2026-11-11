# The image of the agent the executor starts to repair a pull request: Claude Code, nothing else of the host.
# The executor runs it as a non-root user with every capability dropped, the folder of the run mounted, and the token of the subscription in its
# environment. Build it where the executor runs: docker build -t orchestrarr-agent docker/agent
FROM node:22-bookworm-slim
ARG CLAUDE_CODE_VERSION=2.1.322
RUN npm install -g @anthropic-ai/claude-code@${CLAUDE_CODE_VERSION} && npm cache clean --force
USER node
WORKDIR /work
