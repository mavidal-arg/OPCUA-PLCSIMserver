# Flow de prueba Node-RED — PLC Sim Server

Herramienta de **verificación** del PLC Sim Server: Node-RED lee los 31 tags por OPC UA (suscripción), los
muestra en un Dashboard 2.0 local y permite escribir los tags RW. Usa el mismo paquete que la Etapa 2
(`node-red-contrib-opcua`), así que confirma que el servidor funciona con Node-RED antes de entregárselo a Sung.

> **No es el ejercicio de Sung Hee.** No tiene MQTT, no publica nada al Tier0 y no resuelve el puente de la
> Etapa 2 ni el HMI de la Etapa 3.

## Uso

Con `PlcSimServer.exe` abierto:

```bash
powershell -ExecutionPolicy Bypass -File tools/nodered-test/setup.ps1
```

- Instala Node-RED 5, `node-red-contrib-opcua` y `@flowfuse/node-red-dashboard` en `C:\dev\nodered-plcsim`
  (`node_modules` nunca en Google Drive) y arranca Node-RED. Requiere Node.js 20+.
- Dashboard: <http://localhost:1880/dashboard> — Editor: <http://localhost:1880>
- Solo escucha en `127.0.0.1`. Ctrl+C para salir.
- La próxima vez alcanza con `npm start` dentro de `C:\dev\nodered-plcsim`.

Opciones de `setup.ps1`: `-NoStart` (solo instalar), `-Endpoint opc.tcp://otra-pc:4840/plc-sim`,
`-Force` (pisar el `flows.json` de `C:\dev` con el del repo).

Si se modifica el flow en el editor y se quiere guardar en el repo:

```bash
powershell -ExecutionPolicy Bypass -File tools/nodered-test/export.ps1
```

## Qué muestra y qué prueba

| Grupo | Lectura | Escritura |
|---|---|---|
| Conexión OPC UA | EN LÍNEA / **DATOS STALE** (sin notificaciones en 5 s), estado del cliente, último error | — |
| Motor1 / Motor2 | gauge RPM, estado, corriente, falla | switch `Comando_Run` |
| LazoTemp | gráfico PV vs SP, gauge PV, modo actual | slider SP, slider válvula, Auto/Manual |
| MT1 / MT2 | posición, kV, A, MW, comando | botones Cerrar / Abrir |
| InversorSolar | gauge y gráfico AC kW (+ curtailment), DC V/A, eficiencia, falla | slider curtailment |
| Todos los tags | tabla con los 31 NodeIds, valor, timestamp y antigüedad | — |

Cada escritura muestra un aviso con el resultado OPC UA: `Good` en verde, o el rechazo del servidor en rojo
(`BadInvalidState` para enclavamientos —cerrar MT2 con MT1 abierto, mover la válvula en Auto—,
`BadOutOfRange`, etc.).

Pruebas sugeridas:
1. Arrancar/parar motores desde la ventana del PLC sim y desde el dashboard; ambos lados se reflejan.
2. En la ventana, *Forzar* `LazoTemp.PV_Temp_C = 140` → el gauge pasa a rojo.
3. Cerrar MT2 con MT1 abierto → aviso `BadInvalidState`. Cerrar MT1 y luego MT2 → `Good`.
4. *Detener servidor* en la ventana → el dashboard pasa a **DATOS STALE** en ~5 s. *Iniciar servidor* → el
   cliente reconecta solo y vuelve a suscribir (puede tardar hasta ~1 min).

## Archivos

| Archivo | |
|---|---|
| `flows.json` | el flow (una pestaña "PLC Sim — prueba") |
| `settings.js` | settings mínimos de Node-RED (solo localhost, flows.json) |
| `package.json` | versiones fijadas de Node-RED y nodos |
| `setup.ps1` / `export.ps1` | instalar/arrancar en `C:\dev` y traer cambios del editor al repo |

Notas de implementación:
- Un inject al arrancar manda los 31 NodeIds al cliente OPC UA en modo *subscribe* (500 ms). Una función guarda
  el último valor de cada tag en el contexto del flow y lo reparte a los widgets.
- Los widgets de escritura llevan el NodeId en su `topic` con el formato de `node-red-contrib-opcua`:
  `ns=2;s=Motor1.Comando_Run;datatype=Boolean`.
- Los gráficos toman una muestra por segundo del último valor, porque la suscripción solo notifica cambios.
- El certificado del cliente lo crea el nodo en `%APPDATA%\node-red-opcua-nodejs`. Con seguridad `None` no
  hace falta confiar en nada.
