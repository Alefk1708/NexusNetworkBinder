# Nexus Network Binder 21.1.2

Pacote de código-fonte enxuto do aplicativo para Windows. Foram mantidos apenas os arquivos necessários para compilar e executar o Nexus Network Binder, além da licença e deste guia.

## Requisitos

- Windows 10 ou 11 x64
- .NET 8 SDK para compilar
- privilégios de administrador para executar
- duas interfaces de rede ativas para usar os recursos de direcionamento

## Compilar e gerar o executável

Abra o PowerShell nesta pasta e execute:

```powershell
dotnet restore
dotnet publish .\NexusNetworkBinder.csproj -c Release -r win-x64 --self-contained true
```

O executável autocontido será gerado em:

```text
bin\Release\net8.0-windows\win-x64\publish\NexusNetworkBinder.exe
```

O manifesto do projeto solicita elevação de administrador automaticamente ao abrir o programa.

## Conteúdo mantido

- arquivos C# que implementam interface, rotas, firewall, diagnóstico, proxy e persistência;
- `App.xaml` e `MainWindow.xaml` com a interface WPF;
- `app.manifest`, necessário para privilégios e compatibilidade no Windows;
- `NexusNetworkBinder.png` e `NexusNetworkBinder.ico`, usados pela interface e pelo executável;
- `NexusNetworkBinder.csproj`, configuração de compilação;
- licença MIT.

Este pacote não contém um MSI já compilado, arquivos temporários de build, testes ou relatórios. Ele mantém somente o código do instalador e o `.bat` necessário para gerá-lo. Antes de distribuir publicamente, compile e teste em um computador Windows com as duas conexões de rede que serão usadas.

## Criar o instalador MSI

Execute `Publish_e_Build_MSI.bat`. Ele:

1. instala localmente o WiX Toolset 4.0.6 na primeira execução;
2. publica o Nexus para Windows x64 como executável autocontido;
3. gera `NexusNetworkBinder_Setup.msi` nesta pasta.

A primeira execução precisa de internet para baixar o WiX e sua extensão de interface. Os arquivos gerados pelo `dotnet publish` ficam em `dist\win-x64` e podem ser apagados depois que o MSI for criado.
