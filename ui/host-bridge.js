// Pont entre l'interface et l'hôte natif (WebView2). Il expose la même API que l'ancienne version Electron
// (window.systemPulse) : renderer.js n'a donc presque pas besoin de changer.
(function () {
  'use strict';
  var wv = window.chrome && window.chrome.webview;
  document.documentElement.classList.add('native-host');
  if (!wv) {
    // Ouvert hors de l'application (aperçu dans un navigateur) : API inerte.
    var none = function () { return Promise.resolve(null); };
    window.systemPulse = { getState: none, getMetrics: none, setOverlayVisible: none, setInterval: none, setVisibility: none,
      setOverlayMovable: none, relaunchAsAdmin: none, openUpdate: none, windowAction: function () {}, moveOverlay: function () {}, endOverlayMove: function () {},
      fitOverlay: function () {}, onMetrics: function () { return function () {}; }, onState: function () { return function () {}; } };
    return;
  }

  var seq = 0;
  var pending = {};
  var metricsListeners = [];
  var stateListeners = [];

  function call(type, payload) {
    return new Promise(function (resolve) {
      var id = ++seq;
      pending[id] = resolve;
      wv.postMessage(JSON.stringify({ id: id, type: type, payload: payload === undefined ? null : payload }));
    });
  }

  function subscribe(list, callback) {
    if (typeof callback !== 'function') return function () {};
    list.push(callback);
    return function () { var i = list.indexOf(callback); if (i >= 0) list.splice(i, 1); };
  }

  wv.addEventListener('message', function (event) {
    var message = event.data;
    if (typeof message === 'string') { try { message = JSON.parse(message); } catch (e) { return; } }
    if (!message) return;
    if (message.id !== undefined && pending[message.id]) {
      var resolve = pending[message.id];
      delete pending[message.id];
      resolve(message.result);
    } else if (message.event === 'metrics:update') {
      metricsListeners.slice().forEach(function (cb) { cb(message.data); });
    } else if (message.event === 'app:state') {
      stateListeners.slice().forEach(function (cb) { cb(message.data); });
    }
  });

  window.systemPulse = {
    getState: function () { return call('getState'); },
    getMetrics: function () { return call('getMetrics'); },
    setOverlayVisible: function (visible) { return call('setOverlayVisible', Boolean(visible)); },
    setInterval: function (ms) { return call('setInterval', Number(ms)); },
    setVisibility: function (visibility) { return call('setVisibility', visibility); },
    setOverlayMovable: function (movable) { return call('setOverlayMovable', Boolean(movable)); },
    relaunchAsAdmin: function () { return call('relaunchAsAdmin'); },
    openUpdate: function () { return call('openUpdate'); },
    // La fenêtre a sa barre de titre Windows ; l'overlay est dessiné par l'hôte : ces appels n'ont plus d'objet.
    windowAction: function () {},
    moveOverlay: function () {},
    endOverlayMove: function () {},
    fitOverlay: function () {},
    onMetrics: function (callback) { return subscribe(metricsListeners, callback); },
    onState: function (callback) { return subscribe(stateListeners, callback); }
  };
})();
