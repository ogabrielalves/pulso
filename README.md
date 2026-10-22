# Pulso

Diagnóstico, monitoramento e teste de hardware para Windows 10 e 11.

## Executar

`dist\Pulso.exe` é um executável único e portátil: não precisa instalar nem ter .NET na máquina.
Ao abrir, ele pede permissão de administrador, necessária para temperaturas, SMART, timings da RAM e FPS.
Se você recusar, ele roda em modo limitado.

Para temperatura, consumo e voltagem da CPU, sensores da placa-mãe e timings da RAM, instale o driver
gratuito **PawnIO** em *Configurações → Sensores*. O app instala via winget e reinicia sozinho.

## Compilar

Requer o .NET SDK 10.

```powershell
cd src\Pulso
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o ..\..\dist
```

## Estrutura

| Pasta | Conteúdo |
|---|---|
| `Hardware/` | `SensorHub`: uma thread lê todos os sensores (LibreHardwareMonitor + NVML) e publica leituras |
| `Info/` | Informações estáticas: CPU (CPUID), GPU (DXGI/D3D12/Vulkan/OpenGL/PCIe), RAM (SMBIOS/SPD), discos (SMART), placa-mãe, rede, Windows |
| `Bench/` | Benchmarks de CPU/RAM/disco/GPU (Direct3D 11) e motor do teste de estresse |
| `Features/` | FPS via ETW, overlay, alertas, log CSV, detector de gargalo, teste de internet, relatórios TXT/HTML/PDF, inicialização com o Windows |
| `Views/` | Telas (design "Pulso": grafite + limão, Manrope + JetBrains Mono) |

Dados do usuário: `%AppData%\Pulso` (configurações e históricos) e `Documentos\Pulso` (logs CSV e relatórios).

Parâmetros: `--minimized` (inicia na bandeja), `--no-elevate` (não pede administrador), `--page <id>` (abre numa tela).
