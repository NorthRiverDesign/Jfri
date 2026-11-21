# syntax=docker/dockerfile:1
# Jfri itself: the web app plus the git and OpenSSH clients it drives.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Jfri.slnx ./
COPY src/ src/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish src/Jfri.Web/Jfri.Web.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends git openssh-client ca-certificates \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    JFRI_DATA_DIR=/data
RUN mkdir -p /data && chown "$APP_UID" /data
USER $APP_UID
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "Jfri.Web.dll"]
