# One container: the API and the built interface (SPEC §5, CLAUDE.md: a single container image, amd64 now).
FROM node:22-alpine AS frontend
WORKDIR /src/frontend
COPY frontend/package.json frontend/yarn.lock ./
RUN yarn install --frozen-lockfile
COPY frontend/ ./
# vite builds into ../src/Orchestrarr.Host/wwwroot
RUN yarn build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
COPY --from=frontend /src/src/Orchestrarr.Host/wwwroot src/Orchestrarr.Host/wwwroot
ARG VERSION=0.0.0-dev
RUN dotnet publish src/Orchestrarr.Host --configuration Release --output /app -p:Version=${VERSION}

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
# The aspnet image ships a non-root user: the process never runs as root.
USER $APP_UID
ENTRYPOINT ["dotnet", "Orchestrarr.dll"]
