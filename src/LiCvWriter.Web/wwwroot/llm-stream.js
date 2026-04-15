window.liCvWriterLlmStream = (() => {
  const subscriptions = new Map();
  const eventTypes = ["snapshot", "progress", "progress-completed", "completed", "failed", "cancelled"];
  const terminalEventTypes = new Set(["completed", "failed", "cancelled"]);

  function dispose(handleId) {
    const existing = subscriptions.get(handleId);
    if (!existing) {
      return;
    }

    for (const [type, handler] of existing.handlers) {
      existing.source.removeEventListener(type, handler);
    }

    existing.source.close();
    subscriptions.delete(handleId);
  }

  function subscribe(handleId, url, dotNetRef) {
    dispose(handleId);

    const source = new EventSource(url);
    const handlers = [];

    for (const type of eventTypes) {
      const handler = (event) => {
        void dotNetRef.invokeMethodAsync("HandleLlmOperationEvent", type, event.data)
          .finally(() => {
            if (terminalEventTypes.has(type)) {
              dispose(handleId);
            }
          });
      };

      source.addEventListener(type, handler);
      handlers.push([type, handler]);
    }

    subscriptions.set(handleId, { source, handlers });
  }

  return {
    subscribe,
    dispose
  };
})();

window.liCvWriterUi = window.liCvWriterUi || {};
window.liCvWriterUi.scrollElementToBottom = (element) => {
  if (!element) {
    return;
  }

  requestAnimationFrame(() => {
    element.scrollTop = element.scrollHeight;
  });
};