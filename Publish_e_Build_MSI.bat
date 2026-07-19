@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "WIX_VERSION=4.0.6"
set "PROJECT=%CD%\NexusNetworkBinder.csproj"
set "WXS=%CD%\NexusNetworkBinder_Setup.wxs"
set "ICON=%CD%\NexusNetworkBinder.ico"
set "PUBLISH_DIR=%CD%\dist\win-x64"
set "MSI_PATH=%CD%\NexusNetworkBinder_Setup.msi"

if not defined LOCALAPPDATA set "LOCALAPPDATA=%TEMP%"
set "WIX_TOOL_DIR=%LOCALAPPDATA%\NexusNetworkBinder\build-tools\wix-%WIX_VERSION%"
set "WIX_EXE=%WIX_TOOL_DIR%\wix.exe"

echo ============================================================
echo  Nexus Network Binder 21.1.2 - Build do EXE e instalador MSI
echo ============================================================
echo.

where dotnet.exe >nul 2>&1
if errorlevel 1 (
    echo [ERRO] O .NET 8 SDK nao foi encontrado.
    echo Instale o SDK em https://dotnet.microsoft.com/download/dotnet/8.0
    goto :failure
)

if not exist "%PROJECT%" (
    echo [ERRO] Arquivo ausente: NexusNetworkBinder.csproj
    goto :failure
)
if not exist "%WXS%" (
    echo [ERRO] Arquivo ausente: NexusNetworkBinder_Setup.wxs
    goto :failure
)
if not exist "%ICON%" (
    echo [ERRO] Arquivo ausente: NexusNetworkBinder.ico
    goto :failure
)

if not exist "%WIX_EXE%" (
    echo [1/4] Instalando WiX Toolset %WIX_VERSION%...
    dotnet tool install wix --tool-path "%WIX_TOOL_DIR%" --version "%WIX_VERSION%"
    if errorlevel 1 (
        echo [ERRO] Nao foi possivel instalar o WiX Toolset.
        goto :failure
    )
) else (
    echo [1/4] WiX Toolset %WIX_VERSION% encontrado.
)

echo [2/4] Preparando a extensao visual do WiX...
"%WIX_EXE%" extension add -g "WixToolset.UI.wixext/%WIX_VERSION%" >nul 2>&1
if errorlevel 1 (
    echo [AVISO] Nao foi possivel atualizar a extensao agora.
    echo         O build continuara caso ela ja esteja no cache.
)

echo [3/4] Publicando o Nexus Network Binder para Windows x64...
dotnet publish "%PROJECT%" ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:PublishTrimmed=false ^
    -o "%PUBLISH_DIR%"
if errorlevel 1 (
    echo [ERRO] A publicacao do aplicativo falhou.
    goto :failure
)

if not exist "%PUBLISH_DIR%\NexusNetworkBinder.exe" (
    echo [ERRO] O executavel publicado nao foi encontrado.
    goto :failure
)

echo [4/4] Gerando NexusNetworkBinder_Setup.msi...
"%WIX_EXE%" build ^
    -arch x64 ^
    -ext "WixToolset.UI.wixext/%WIX_VERSION%" ^
    -culture pt-BR ^
    -d "PublishDir=%PUBLISH_DIR%" ^
    -d "IconPath=%ICON%" ^
    -out "%MSI_PATH%" ^
    "%WXS%"
if errorlevel 1 (
    echo [ERRO] A criacao do MSI falhou.
    goto :failure
)

if not exist "%MSI_PATH%" (
    echo [ERRO] O WiX terminou sem gerar o MSI esperado.
    goto :failure
)

echo.
echo [OK] Instalador criado com sucesso:
echo %MSI_PATH%
echo.
pause
exit /b 0

:failure
echo.
echo O instalador nao foi gerado. Revise as mensagens acima.
echo.
pause
exit /b 1
