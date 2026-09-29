# PLC Sim Server — Taller OPC UA ICSH (Etapa 1)

Servidor OPC UA con ventana (WinForms, .NET 10) que simula un PLC: dos motores, un lazo de
temperatura, dos interruptores de media tensión y un inversor solar. Todo lo que se cambia en la
ventana (o lo que escribe un cliente OPC UA) se publica por OPC UA, y Node-RED lo lleva por MQTT
al HMI del servidor Tier0.

Construido sobre el SDK oficial de la OPC Foundation, **usado desde el código fuente del
repositorio de GitHub** [OPCFoundation/UA-.NETStandard](https://github.com/OPCFoundation/UA-.NETStandard),
tag `1.5.378.176`. El código del servidor está adaptado de los ejemplos del propio repo
(`Applications/ConsoleReferenceServer` y `Applications/Quickstarts.Servers/ReferenceServer`).

```
 PC Windows del laboratorio                                   VPS Tier0 (Hostinger)
 ┌───────────────────────────┐   opc.tcp:4840   ┌───────────────────────┐   MQTT (saliente)   ┌────────────────────┐
 │ PlcSimServer.exe (ventana)│◀────────────────▶│ nodered-lab-cliente   │────────────────────▶│ emqx               │
 │  simulación + OPC UA      │  subscribe/write │ (Docker Desktop / npm)│◀────────────────────│ nodered-lab-servidor│
 └───────────────────────────┘                  └───────────────────────┘   comandos          │ (dashboard HMI)    │
                                                                                               └────────────────────┘
```
La PC solo abre conexiones **salientes** hacia el VPS: no hace falta abrir puertos ni VPN.

## Requisitos

- Windows 10/11 con **.NET SDK 10** (`winget install Microsoft.DotNet.SDK.10`).
- git (para clonar el SDK).

## Obtener el SDK y compilar

> **El SDK y la salida de compilación van en `C:\dev`, no en Google Drive.** Compilar sobre G: tardaba
> más de 40 min y dejaba procesos colgados. En C: tarda ~1 min. Ver
> `G:\My Drive\Trabajo\LEEME-Compilar-fuera-de-Google-Drive.md`.

```bash
git clone https://github.com/mavidal-arg/OPCUA-PLCSIMserver C:/dev/PlcSimServer
```

```bash
git clone --depth 1 --branch 1.5.378.176 https://github.com/OPCFoundation/UA-.NETStandard C:/dev/UA-.NETStandard
```

```bash
dotnet build PlcSimServer.sln -nodeReuse:false
```

```bash
dotnet run --project src/PlcSimServer
```

El ejecutable queda en `C:\dev\build\PlcSimServer\bin\PlcSimServer\debug\PlcSimServer.exe`
(`ArtifactsPath` en `Directory.Build.props`). En Drive solo queda el código fuente.

Notas de compilación:
- `Directory.Build.rsp` pasa dos opciones globales a MSBuild: `NBGV_GitEngine=Disabled` (el SDK
  usa Nerdbank.GitVersioning, que falla con clones `--depth 1`) y `CustomTestTarget=net10.0`
  (compila el SDK solo para net10.0 en lugar de net48/net8/net9/net10). Visual Studio no lee ese
  archivo: compilar desde la línea de comandos, o hacer un clon completo del SDK.
- La ruta del SDK se define en `Directory.Build.props` (`OpcUaSdkPath`); para usar otra copia:
  `dotnet build -p:OpcUaSdkPath=C:\ruta\UA-.NETStandard`.
- `-nodeReuse:false` evita que queden procesos MSBuild residentes después de compilar.

## Conexión

| Parámetro | Valor |
|---|---|
| Endpoint | `opc.tcp://<nombre-PC>:4840/plc-sim` (desde la misma PC: `opc.tcp://localhost:4840/plc-sim`) |
| Desde Docker Desktop | `opc.tcp://host.docker.internal:4840/plc-sim` |
| Seguridad | `None` (Etapa 1). También disponibles `Basic256Sha256` Sign y SignAndEncrypt (Etapa 4) |
| Usuario | Anonymous, o `lab` / `lab2026` |
| Namespace URI | `urn:icsh:lab:plc-sim` (índice `ns=2`) |
| Certificados | `%LocalAppData%\PlcSimServer\pki` (self-signed, se crea solo; clientes aceptados automáticamente) |
| Log del SDK | `%LocalAppData%\PlcSimServer\Logs` |

Si otra máquina de la red se conecta directo al 4840, abrir el firewall de Windows (PowerShell como admin):

```bash
netsh advfirewall firewall add rule name="PLC Sim OPC UA" dir=in action=allow protocol=TCP localport=4840
```

## Mapa de NodeIds

Árbol: `Objects / PlcSim / <Equipo> / <Variable>`. NodeId = `ns=2;s=<Equipo>.<Variable>`.

| NodeId | Tipo | Acceso | Descripción |
|---|---|---|---|
| `ns=2;s=Motor1.Status` | String | R | `Run` / `Stop` / `Falla` |
| `ns=2;s=Motor1.RPM` | Double | R | rpm (nominal 1480) |
| `ns=2;s=Motor1.Corriente_A` | Double | R | A (pico de arranque mientras acelera) |
| `ns=2;s=Motor1.Falla` | Boolean | R | falla inyectada desde la ventana |
| `ns=2;s=Motor1.Comando_Run` | Boolean | **RW** | `true` = marcha, `false` = parada. Una falla lo baja a `false` |
| `ns=2;s=Motor2.*` | | | igual que Motor1 (nominal 1780 rpm) |
| `ns=2;s=LazoTemp.PV_Temp_C` | Double | R | temperatura medida, °C |
| `ns=2;s=LazoTemp.Modo` | String | R | modo actual `Auto` / `Manual` |
| `ns=2;s=LazoTemp.SP_Temp_C` | Double | **RW** | setpoint, 0..150 °C |
| `ns=2;s=LazoTemp.Salida_Valvula_Pct` | Double | **RW** | 0..100 %. Solo escribible en Manual (en Auto la mueve el PI) |
| `ns=2;s=LazoTemp.Modo_Cmd` | String | **RW** | `Auto` / `Manual` |
| `ns=2;s=MT1.Posicion` | String | R | `Open` / `Closed` |
| `ns=2;s=MT1.kV` | Double | R | tensión, ~23 kV |
| `ns=2;s=MT1.A` | Double | R | corriente (carga base + motores) |
| `ns=2;s=MT1.MW` | Double | R | potencia activa |
| `ns=2;s=MT1.Comando_Open_Close` | Boolean | **RW** | `true` = cerrar, `false` = abrir |
| `ns=2;s=MT2.*` | | | igual que MT1; alimenta al inversor. **Enclavamiento:** no cierra con MT1 abierto; si MT1 abre, MT2 abre |
| `ns=2;s=InversorSolar.DC_V` | Double | R | V lado DC |
| `ns=2;s=InversorSolar.DC_A` | Double | R | A lado DC |
| `ns=2;s=InversorSolar.AC_Power_kW` | Double | R | potencia AC (nominal 100 kW). 0 si falla o MT2 abierto (anti-isla) |
| `ns=2;s=InversorSolar.Eficiencia_Pct` | Double | R | % |
| `ns=2;s=InversorSolar.Falla` | Boolean | R | falla inyectada desde la ventana |
| `ns=2;s=InversorSolar.Curtailment_Setpoint_Pct` | Double | **RW** | límite de potencia 0..100 % |

Respuestas de escritura que un cliente puede recibir (útiles para el manejo de errores en Node-RED):
`BadNotWritable` (tag de solo lectura), `BadOutOfRange` (fuera de rango o valor no permitido),
`BadInvalidState` (enclavamiento, o válvula en modo Auto), `BadTypeMismatch` (tipo de dato incorrecto:
p.ej. escribir un número en un Boolean).

## La ventana

- **Resumen:** todos los tags en vivo con su NodeId. Se puede *Escribir* un tag RW (igual que un
  cliente) o *Forzar* cualquier tag: queda congelado frente a la simulación (útil para probar
  alarmas en el HMI, p.ej. forzar `LazoTemp.PV_Temp_C = 140`). *Liberar forzado* lo devuelve a la simulación.
- **Una pestaña por equipo:** botones Arrancar/Parar y Abrir/Cerrar, sliders de setpoint, válvula y
  curtailment, modo Auto/Manual, inyección de fallas y tendencias.
- **Detener / Iniciar servidor:** corta el servidor OPC UA (los clientes pierden la sesión) para
  practicar reconexión y manejo de datos "stale" (Etapas 2 y 4 del plan).
- **Pausar simulación:** congela los valores (se siguen publicando).
- **Log:** escrituras recibidas por OPC (con el usuario), escrituras de la UI, rechazos, sesiones que
  se conectan y desconectan, y advertencias del SDK.

## Probar sin Node-RED

Con la ventana abierta:

```bash
dotnet run --project tools/PlcSimServer.TestClient
```

Hace browse de las 6 carpetas, lee todos los tags y prueba cada escritura RW, incluyendo los rechazos
(fuera de rango, enclavamiento MT2, válvula en Auto, tipo incorrecto). Termina con `RESULTADO: OK`.

Para ver en consola lo que se mueve al operar la ventana (suscripción a todos los tags por 60 s):

```bash
dotnet run --project tools/PlcSimServer.TestClient -- --watch 60
```

También sirve cualquier cliente genérico (p.ej. UaExpert): endpoint de arriba, seguridad None, Anonymous.

## Para Sung Hee (Etapa 2)

Lo único que necesitás del servidor es el **endpoint** y el **mapa de NodeIds** de arriba. El flow
de Node-RED (cliente OPC UA → MQTT y MQTT → escritura OPC UA) es tu ejercicio; este proyecto no lo incluye.

## Estructura

```
PlcSimServer/
  Directory.Build.props / .rsp   ruta del SDK (C:\dev\UA-.NETStandard), salida a C:\dev\build y opciones
  src/PlcSimServer/
    Program.cs                   arranque
    PlcSimServer.Config.xml      configuración OPC UA (endpoint, seguridad, PKI)
    Model/PlantModel.cs          tags y validación de escrituras (fuente única de verdad)
    Simulation/SimulationEngine  lógica de proceso cada 250 ms + enclavamientos
    Opc/PlcServer.cs             StandardServer (usuarios, sesiones)
    Opc/PlcNodeManager.cs        espacio de direcciones, escrituras OPC, publicación de cambios
    Opc/OpcHost.cs               ciclo de vida del servidor y certificado
    UI/                          ventana WinForms
  tools/PlcSimServer.TestClient/ cliente de prueba de consola
```
