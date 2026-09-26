const test = require('node:test');
const assert = require('node:assert/strict');
const { EventEmitter } = require('node:events');
const { monitorBackendProcess } = require('./backend-process.cjs');

function fakeChild() {
  const child = new EventEmitter();
  child.exitCode = null;
  child.signalCode = null;
  return child;
}

test('reports child process spawn errors without leaving them unhandled', () => {
  const child = fakeChild();
  const getFailure = monitorBackendProcess(child);

  child.emit('error', Object.assign(new Error('access denied'), { code: 'EACCES' }));

  assert.match(getFailure().message, /could not start \(EACCES\)/);
});

test('reports an audio service that exits during startup', () => {
  const child = fakeChild();
  const getFailure = monitorBackendProcess(child);
  child.exitCode = 3;

  assert.match(getFailure().message, /exited with code 3/);
});

test('reports a service stopped by a signal during startup', () => {
  const child = fakeChild();
  const getFailure = monitorBackendProcess(child);
  child.signalCode = 'SIGTERM';

  assert.match(getFailure().message, /stopped during startup \(SIGTERM\)/);
});

test('keeps a running audio service in the ready-to-start state', () => {
  assert.equal(monitorBackendProcess(fakeChild())(), null);
});
