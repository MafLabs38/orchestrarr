# One container: the API and the built interface (SPEC §5, CLAUDE.md: a single container image, amd64 now).
FROM node:22-alpine@sha256:fe4f6d74e20370acb04ad889077d14d0f64a9c8ea6a42bc60cddf1e844699914 AS frontend
WORKDIR /src/frontend
COPY frontend/package.json frontend/yarn.lock ./
RUN yarn install --frozen-lockfile
COPY frontend/ ./
# vite builds into ../src/Orchestrarr.Host/wwwroot
RUN yarn build

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
COPY --from=frontend /src/src/Orchestrarr.Host/wwwroot src/Orchestrarr.Host/wwwroot
ARG VERSION=0.0.0-dev
RUN dotnet publish src/Orchestrarr.Host --configuration Release --output /app -p:Version=${VERSION}

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee AS runtime
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080 \
    Storage__Provider=Sqlite \
    Storage__Path=/config/orchestrarr.db
# What must survive a restart (the approvals, the audit trail, the global stop) is in this volume
RUN mkdir /config && chown $APP_UID /config && chmod 700 /config
VOLUME ["/config"]
EXPOSE 8080
# The aspnet image ships a non-root user: the process never runs as root.
USER $APP_UID
ENTRYPOINT ["dotnet", "Orchestrarr.dll"]
