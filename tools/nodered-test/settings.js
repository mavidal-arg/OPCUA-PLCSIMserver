// Settings mínimos de Node-RED para el flow de prueba del PLC Sim Server.
// Se usa con userDir = C:\dev\nodered-plcsim (ver setup.ps1), nunca dentro de Google Drive.
module.exports = {
    flowFile: 'flows.json',
    flowFilePretty: true,
    credentialSecret: false,

    // Solo accesible desde esta PC (editor en /, dashboard en /dashboard).
    uiHost: '127.0.0.1',
    uiPort: process.env.PORT || 1880,

    logging: {
        console: { level: 'info', metrics: false, audit: false }
    },

    editorTheme: {
        projects: { enabled: false },
        tours: false
    }
};
