# Axis Print Agent

Agente local de impressão multiplataforma (Windows / Linux / macOS) para o ERP Axis.

Resolve o problema de "imprimir nas impressoras do cliente" quando o sistema roda
em servidor (cloud ou rede): o navegador não consegue falar com impressoras locais,
e o servidor enxerga apenas as próprias impressoras. Este agente é instalado na
máquina do cliente, fica conectado ao servidor via SignalR e recebe trabalhos de
impressão (ZPL ou bytes RAW) para enviar à impressora local.

## Como funciona

```
[ Navegador ] -> [ Axis.WebApp ] --SignalR--> [ Axis.PrintAgent na máquina do cliente ] -> [ Impressora local ]
					   |
				  (banco do tenant: print_agents + print_agent_pairings)
```

- O cliente baixa e executa o agente na máquina onde estão as impressoras.
- Pela tela `Configurações > Agentes de Impressão`, o cliente gera um código `AX-XXXX` (válido por 10 min).
- No terminal da máquina-cliente roda `axis-print-agent pair <url-do-servidor> AX-XXXX`.
- O agente recebe um token opaco, salva em `agent.json` e fica online.
- Na tela de "Imprimir Etiquetas", aparece um optgroup "Agentes locais" com as impressoras de cada máquina pareada online.

## Build / Publish

Por sistema operacional (a partir da raiz do repositório):

```powershell
# Windows x64 (single-file, self-contained, sem .NET instalado no cliente)
dotnet publish Axis.PrintAgent -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/win-x64

# Linux x64
dotnet publish Axis.PrintAgent -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o publish/linux-x64

# macOS Apple Silicon
dotnet publish Axis.PrintAgent -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -o publish/osx-arm64
```

Distribuir o binário gerado (~ 60-80 MB) ao cliente.

## CLI

```bash
axis-print-agent help
axis-print-agent status
axis-print-agent pair https://meutenant.axis.app AX-AB12
axis-print-agent          # sem argumentos: roda como serviço (modo daemon)
```

## Configuração persistida

Arquivo `agent.json` em:

- Windows: `%LOCALAPPDATA%\AxisPrintAgent\agent.json`
- Linux/macOS: `~/.config/AxisPrintAgent/agent.json`

Conteúdo:

```json
{
  "ServerUrl": "https://meutenant.axis.app",
  "Token": "...",
  "AgentId": "agt_...",
  "AgentName": "PC-VENDA-01"
}
```

## Drivers de impressão

- **Windows**: enumera pelo registry e envia bytes RAW via winspool (`OpenPrinter` / `WritePrinter`).
- **Linux/macOS**: usa CUPS — `lpstat -a` para listar, `lp -d "<impressora>" -o raw` (stdin) para enviar.

## Servidor (Axis.WebApp)

- Hub SignalR: `/hubs/print-agent` (auth via `?access_token=` opaco, validado contra `print_agents.token_hash`).
- API REST:
  - `POST /api/print-agent/pair/code`     gera AX-XXXX (auth)
  - `POST /api/print-agent/pair/confirm`  CLI confirma pareamento (anônimo)
  - `GET  /api/print-agent/listar`        lista agentes (auth)
  - `POST /api/print-agent/desvincular`   desativa agente (auth)
  - `POST /api/print-agent/imprimir`      envia job para um agente online (auth)

## Tabelas (Axis.Data/Scripts/2042_agentes_impressao.sql)

- `print_agents`           agente pareado (1 por máquina), com token hash + impressoras cacheadas.
- `print_agent_pairings`   códigos AX-XXXX temporários (10 min de validade).

## Próximos passos opcionais

- Backplane Redis no `IPrintAgentRegistry` para suportar mais de uma instância de WebApp.
- Instaladores: MSI (Windows), .deb / .rpm (Linux), .pkg (macOS).
- Reimpressão automática em fila local quando o servidor cair.
