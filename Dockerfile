FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json MCP.RAG.StarTrek.sln ./
COPY src/StarTrek.Indexer/StarTrek.Indexer.csproj src/StarTrek.Indexer/
RUN dotnet restore src/StarTrek.Indexer/StarTrek.Indexer.csproj
COPY src/StarTrek.Indexer/ src/StarTrek.Indexer/
RUN dotnet publish src/StarTrek.Indexer/StarTrek.Indexer.csproj -c Release -o /publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /opt/startrek
COPY MCP.RAG.StarTrek.sln /opt/startrek/MCP.RAG.StarTrek.sln
WORKDIR /opt/startrek/app
COPY --from=build /publish .
ENV LANG=C.UTF-8
ENTRYPOINT ["dotnet", "StarTrek.Indexer.dll"]
CMD ["health-check"]
