# Compila tudo sem instalar nada no PC: a UI (Svelte), os testes e o PokeDeck.exe (win-x64, um arquivo só).
#   docker build --target export --output dist .
FROM node:22-alpine AS ui
WORKDIR /ui
COPY ui/package.json ui/package-lock.json ./
RUN npm ci
COPY ui/ .
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/ src/
COPY tests/ tests/
COPY --from=ui /ui/dist src/PokeDeck/ui-dist/
RUN dotnet test tests/PokeDeck.Tests -c Release
# A release passa a versão da tag (v0.2.0 → 0.2.0); o build local fica 0.0.0.
ARG VERSION=0.0.0
RUN dotnet publish src/PokeDeck/PokeDeck.csproj -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:EnableWindowsTargeting=true -p:Version=$VERSION -o /out

FROM scratch AS export
COPY --from=build /out/PokeDeck.exe /
