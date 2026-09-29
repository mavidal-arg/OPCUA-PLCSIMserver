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
| Certificados | `%LocalAppData%\PlcSimServer\pki` (self-signed, se crea solo; clientes aceptados automáticamente). Ver [Seguridad](#seguridad-sign--signandencrypt-etapa-4) |
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

## Probar con Node-RED (dashboard local)

`tools/nodered-test/` tiene un flow de **prueba** (no es el de Sung: no tiene MQTT). Lee los 31 tags por
OPC UA con `node-red-contrib-opcua`, los muestra en un Dashboard 2.0 local y permite escribir los RW. Así se
ven los rechazos, y el corte y la reconexión del servidor. Instala todo en `C:\dev\nodered-plcsim`:

```bash
powershell -ExecutionPolicy Bypass -File tools/nodered-test/setup.ps1
```

Dashboard en <http://localhost:1880/dashboard>. Detalles en `tools/nodered-test/README.md`.

## Seguridad: Sign / SignAndEncrypt (Etapa 4)

En la Etapa 1 se usa el endpoint `None`: no se firma ni se cifra nada y los certificados no se validan.
El servidor **ya publica** los endpoints seguros (`Basic256Sha256` en modo `Sign` y `SignAndEncrypt`). Para
exigirlos hay que cambiar la configuración y hacer que cliente y servidor **confíen uno en el otro**.

### Qué hay que tocar

| Archivo | Punto | Etapa 1 (hoy) | Etapa 4 |
|---|---|---|---|
| `src/PlcSimServer/PlcSimServer.Config.xml` | `<AutoAcceptUntrustedCertificates>` | `true`: acepta el certificado de cualquier cliente | **`false`**: solo clientes cuyo certificado esté en `pki\trusted` |
| `src/PlcSimServer/PlcSimServer.Config.xml` | `<SecurityPolicies>` | `None` + `Basic256Sha256` Sign + SignAndEncrypt | Borrar el bloque `None_1` para que **no** quede una puerta sin seguridad. Dejar solo `SignAndEncrypt_3` si se quiere exigir cifrado |
| `src/PlcSimServer/PlcSimServer.Config.xml` | `<UserTokenPolicies>` | `Anonymous_0` + `UserName_1` | Opcional: borrar `Anonymous_0` para exigir usuario y clave |
| `src/PlcSimServer/Opc/PlcServer.cs` | `LabUser` / `LabPassword` y `SessionManager_ImpersonateUser` | usuario fijo `lab` / `lab2026`; Anonymous aceptado | Cambiar la clave. Para rechazar Anonymous también en código, lanzar `BadIdentityTokenRejected` en la rama `AnonymousIdentityToken` |
| `src/PlcSimServer/Opc/OpcHost.cs` | `CheckApplicationInstanceCertificatesAsync` | crea/valida el certificado del servidor al arrancar | Sin cambios |
| `tools/PlcSimServer.TestClient/Program.cs` | `SelectEndpointAsync(config, endpointUrl, false, …)` y `SetAutoAcceptUntrustedCertificates(true)` | elige el endpoint sin seguridad y acepta cualquier servidor | `true` en `SelectEndpointAsync` (elige el endpoint más seguro) y `false` en el auto-accept |
| Node-RED, nodo `OpcUa-Endpoint` | Security policy / mode, usuario | `None` / `None`, Anonymous | `Basic256Sha256` / `SignAndEncrypt`, usuario `lab` |

El XML se copia a la salida al compilar (`CopyToOutputDirectory=PreserveNewest`). Después de editarlo en `src`,
hay que recompilar (`dotnet build PlcSimServer.sln -nodeReuse:false`). Para una prueba rápida sin recompilar,
se puede editar la copia en `C:\dev\build\PlcSimServer\bin\PlcSimServer\debug\PlcSimServer.Config.xml`, pero
el próximo build la pisa. En los dos casos hay que reiniciar `PlcSimServer.exe`.

### Carpetas de certificados

Cada aplicación OPC UA tiene su propia PKI (almacén de certificados). Todas quedan fuera de Google Drive.

**Servidor** — `%LocalAppData%\PlcSimServer\pki\` (definida en `<SecurityConfiguration>` del XML):

| Carpeta | Contenido |
|---|---|
| `own\certs\PLC Sim Server [<huella>].der` | certificado público del servidor (se lo entrega a los clientes) |
| `own\private\PLC Sim Server [<huella>].pfx` | clave privada del servidor, **sin contraseña**: no copiarla ni subirla a git |
| `trusted\certs` | certificados de **clientes** aceptados |
| `issuer\certs` | CAs que emiten certificados de clientes (solo si se usa una CA; no es el caso del laboratorio) |
| `rejected\certs` | clientes rechazados (hasta 20, `MaxRejectedCertificates`). Para aprobar uno, se **mueve** a `trusted\certs` |

**Clientes:**

| Cliente | PKI | Propio | Confía en servidores | Rechazados |
|---|---|---|---|---|
| Node-RED (`node-red-contrib-opcua`) | `%APPDATA%\node-red-opcua-nodejs\Config\PKI\` | `own\certs\client_certificate.pem`, `own\private\private_key.pem` | `trusted\certs` | `rejected` |
| `PlcSimServer.TestClient` | `%LocalAppData%\PlcSimServer\testclient-pki\` | `own\` | `trusted\` | `rejected\` |
| Node-RED en Docker | dentro del contenedor, en el home del usuario de Node-RED | | | |

En Docker, montar la PKI en un volumen. Si no, el certificado del cliente cambia cada vez que se recrea el
contenedor y el servidor lo rechaza como si fuera un cliente nuevo.

### Cómo se genera el certificado del servidor

Lo hace el SDK en el primer `StartAsync()` de `OpcHost`, sin intervención:

1. `LoadApplicationConfigurationAsync` lee el XML, expande `%LocalApplicationData%` y **reemplaza `localhost`
   por el nombre del equipo**. Así, `DC=localhost` y `urn:localhost:ICSH:PlcSimServer` pasan a ser
   `DC=<nombre-PC>` y `urn:<nombre-PC>:ICSH:PlcSimServer`.
2. `CheckApplicationInstanceCertificatesAsync` busca en `own\` un certificado con ese `SubjectName`. Si no
   existe, o no es válido (vencido, clave menor a `MinimumCertificateKeySize`, URI que no coincide), crea uno
   nuevo **autofirmado**:
   - RSA 2048 bits, firma SHA-256 (`CertificateTypeString = RsaSha256`)
   - validez de **12 meses**
   - Subject `CN=PLC Sim Server, O=ICSH, DC=<nombre-PC>`
   - Subject Alternative Name: `URI=urn:<nombre-PC>:ICSH:PlcSimServer` y `DNS=<nombre-PC>`
   - usos: firma digital, cifrado de claves y datos; Server Authentication y Client Authentication
3. Guarda el público en `own\certs` (`.der`) y el privado en `own\private` (`.pfx`). El diálogo del SDK
   ("¿crear certificado?") se responde solo con `SilentMessageDlg`.

Con `AddAppCertToTrustedStore = false`, el servidor no se agrega a sí mismo a `trusted`.

Para ver el certificado actual (vencimiento, SAN, huella):

```bash
powershell -Command "Get-ChildItem $env:LOCALAPPDATA\PlcSimServer\pki\own\certs\*.der | ForEach-Object { New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $_.FullName } | Format-List Subject,NotAfter,Thumbprint"
```

**Regenerar** (vencido, o cambió el nombre de la PC): cerrar el servidor, borrar `pki\own` y volver a arrancar.
El certificado nuevo tiene **otra huella**, así que cada cliente debe volver a confiar en él.

**Nombre de host:** el certificado solo incluye `DNS=<nombre-PC>`. Si un cliente se conecta con otro nombre
(`localhost`, `host.docker.internal`, una IP), los clientes estrictos avisan `BadCertificateHostNameInvalid`.
Conviene usar `opc.tcp://<nombre-PC>:4840/plc-sim`, o desactivar ese chequeo en el cliente.

### Paso a paso para conectar con SignAndEncrypt

1. En el XML: `AutoAcceptUntrustedCertificates = false` (y opcionalmente quitar `None` y `Anonymous`).
   Recompilar y arrancar el servidor.
2. En el cliente: elegir `Basic256Sha256` + `SignAndEncrypt` y el usuario `lab` / `lab2026`. Conectar.
   **El primer intento falla**: es lo esperado.
3. **El servidor confía en el cliente:** el certificado del cliente quedó en
   `%LocalAppData%\PlcSimServer\pki\rejected\certs`. Moverlo a `pki\trusted\certs`. Si el servidor lo sigue
   rechazando, usar *Detener / Iniciar servidor* en la ventana.
4. **El cliente confía en el servidor:** si el cliente rechazó al servidor, su certificado quedó en la carpeta
   `rejected` del cliente. Moverlo a `trusted\certs` del cliente. También se puede copiar directamente el
   `.der` de `pki\own\certs` del servidor.
5. Reconectar. En el log de la ventana aparecen `[OPC] sesión creada` y `[OPC] usuario autenticado: lab`.

Para volver a la Etapa 1, deshacer el cambio del XML. Para empezar de cero con los certificados, borrar la PKI
del lado que corresponda: se recrea en el próximo arranque.

### Puertos, canal seguro y sesiones

**Puerto:** un solo puerto, **TCP 4840** (`opc.tcp`, binario), escuchando en todas las interfaces (IPv4 e IPv6).
Los endpoints `None`, `Sign` y `SignAndEncrypt` comparten la misma URL y el mismo puerto: la seguridad se
negocia dentro de la conexión, no cambia el puerto. No se usan otros puertos: no hay registro en un Discovery
Server (`MaxRegistrationInterval = 0`) ni mDNS (`MultiCastDnsEnabled = false`). En el firewall solo hace falta
abrir el 4840 de entrada en la PC del servidor. El cliente usa un puerto efímero de salida.

Secuencia de una conexión:

```
Cliente                                                   Servidor :4840
  │── TCP connect ─────────────────────────────────────────────▶│
  │── Hello / Acknowledge (tamaños de buffer y mensaje) ───────▶│
  │                                                             │
  │  (descubrimiento: canal None, sin sesión)                   │
  │── OpenSecureChannel [None] ────────────────────────────────▶│
  │── GetEndpoints ────────────────────────────────────────────▶│  lista de endpoints + certificado del servidor
  │── CloseSecureChannel ──────────────────────────────────────▶│
  │                                                             │
  │  (conexión real)                                            │
  │── OpenSecureChannel [Basic256Sha256, SignAndEncrypt] ──────▶│  asimétrico: el cliente firma con su clave privada
  │     cert del cliente + nonce                                │  y cifra con la clave pública del servidor.
  │◀──────────── nonce del servidor + token del canal ──────────│  Acá el servidor valida al cliente contra trusted/
  │     de los dos nonces salen las claves simétricas            │  (si no está: rejected/ y BadSecurityChecksFailed)
  │── CreateSession (cert cliente, nonce, timeout pedido) ─────▶│
  │◀──────────── cert servidor + firma sobre el nonce ──────────│  acá el cliente valida al servidor
  │── ActivateSession (Anonymous o usuario+clave cifrada) ─────▶│  ImpersonateUser en PlcServer.cs
  │── Read / Browse / CreateSubscription / CreateMonitoredItems▶│
  │── Publish ◀──▶ notificaciones de cambios / keep-alive ─────▶│  (en bucle)
  │── Write ───────────────────────────────────────────────────▶│  Good / BadInvalidState / ...
  │── CloseSession / CloseSecureChannel ───────────────────────▶│
```

- **Canal seguro (SecureChannel):** es la capa que firma y cifra. Solo el `OpenSecureChannel` usa
  criptografía asimétrica (RSA, con los certificados). El resto del tráfico usa claves simétricas (AES-256 y
  HMAC-SHA256 en `Basic256Sha256`). El token del canal dura 1 h (`SecurityTokenLifetime = 3600000`) y el
  cliente lo renueva antes de que venza. Si se corta la red, el canal sobrevive 30 s
  (`ChannelLifetime = 30000`) para que el cliente se reconecte sin perder la sesión.
- **Sesión:** va sobre el canal y lleva la identidad del usuario. El timeout lo pide el cliente (el
  TestClient pide 60 s) y el servidor lo limita a 10 s–1 h (`MinSessionTimeout` / `MaxSessionTimeout`). Si
  el cliente deja de hablar más tiempo que el timeout, el servidor la cierra, y con ella sus suscripciones.
- **Suscripción:** va dentro de la sesión. El servidor muestrea cada ítem (intervalos disponibles desde
  100 ms) y responde a los `Publish` del cliente solo con los **cambios**. Si no hay cambios, manda un
  keep-alive. Por eso el flow de prueba muestrea cada 1 s para los gráficos.
- **Límites** del XML: 50 sesiones (`MaxSessionCount`), 200 canales (`MaxChannelCount`), timeout por operación
  120 s, mensaje máximo 4 MB.
- **Cuántas conexiones abre cada cliente:** una por cada objeto cliente. El flow de Node-RED tiene dos nodos
  `OpcUa-Client` (subscribe y write), así que abre **2 conexiones TCP, 2 canales y 2 sesiones**. Se ven en
  la ventana del PLC sim y con `netstat -ano | findstr :4840`. El TestClient abre una.
- **Clave de usuario:** con `SignAndEncrypt` viaja cifrada por el canal y, además, cifrada con el
  certificado del servidor dentro del token.

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
  tools/nodered-test/            flow Node-RED de prueba (OPC UA → Dashboard 2.0 local)
```
