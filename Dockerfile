# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src

COPY RinhaFraud.slnx ./
COPY src/ ./src/
COPY tools/ ./tools/

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates \
    && rm -rf /var/lib/apt/lists/*

RUN curl -fL -o /tmp/references.json.gz \
    https://github.com/zanfranceschi/rinha-de-backend-2026/raw/main/resources/references.json.gz

RUN dotnet publish tools/RinhaRefConverter/RinhaRefConverter.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained false \
    -o /app/conv

RUN mkdir -p /app/data && /app/conv/RinhaRefConverter /tmp/references.json.gz /app/data/references.bin

RUN dotnet publish src/RinhaFraudApi/RinhaFraudApi.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained false \
    -o /app/publish \
    /p:InvariantGlobalization=true

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS final
WORKDIR /app

COPY --from=build /app/publish .
COPY --from=build /app/data/references.bin ./data/references.bin

ENV REFERENCES_PATH=/app/data/references.bin
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "RinhaFraudApi.dll"]
