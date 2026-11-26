# The image of the agent the Runner starts to repair a pull request: Claude Code, nothing else of the host.
# The Runner runs it as a non-root user with every capability dropped, the folder of the run mounted, and the token of the subscription in its
# environment. The base is pinned by digest (a tag moves, and this image holds the token at run time): Dependabot proposes the bump (see .github/dependabot.yml).
# Build it where the Runner runs: docker build -t orchestrarr-agent docker/agent
FROM node:22-bookworm-slim@sha256:e9f565d32859f0cbfb52125eafe8be39390f24dac302dc3cdf566e8ba3ab21ee
ARG CLAUDE_CODE_VERSION=2.1.322
RUN npm install -g @anthropic-ai/claude-code@${CLAUDE_CODE_VERSION} && npm cache clean --force
USER node
WORKDIR /work
