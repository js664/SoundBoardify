function monitorBackendProcess(child) {
  let spawnError;
  child.on('error', error => { spawnError = error; });

  return () => {
    if (spawnError) {
      const code = spawnError.code || 'unknown error';
      return new Error(`The soundboard audio service could not start (${code}). Reinstall SimplySound and try again.`);
    }
    if (child.exitCode !== null && child.exitCode !== undefined)
      return new Error(`The soundboard audio service exited with code ${child.exitCode}.`);
    if (child.signalCode)
      return new Error(`The soundboard audio service stopped during startup (${child.signalCode}).`);
    return null;
  };
}

module.exports = { monitorBackendProcess };
