FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/Ascendra.Agents.Web/Ascendra.Agents.Web.csproj", "src/Ascendra.Agents.Web/"]
RUN dotnet restore "src/Ascendra.Agents.Web/Ascendra.Agents.Web.csproj"

COPY . .
WORKDIR "/src/src/Ascendra.Agents.Web"
RUN dotnet publish "Ascendra.Agents.Web.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
COPY --from=build /src/data ./data
COPY --from=build /src/assets ./assets
# Las imágenes oficiales .NET 8+ ya incluyen el usuario no privilegiado app.
# APP_UID evita depender de adduser/addgroup, que no existe en todas las bases.
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:10000
ENV Data__InputPath=data/input
ENV Data__GeneratedPath=data/generated
EXPOSE 10000
ENTRYPOINT ["dotnet", "Ascendra.Agents.Web.dll"]
