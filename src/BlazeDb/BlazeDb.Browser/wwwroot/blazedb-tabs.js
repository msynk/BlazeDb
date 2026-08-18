// BlazeDb cross-tab notification module.
// The writer tab posts a message after each checkpoint; replica tabs listen and reload.
// BroadcastChannel only carries the signal - the data itself always travels through storage,
// so a replica reads exactly what was made durable.

const channels = new Map();

export function open(channelName, onMessage) {
  close(channelName);
  const channel = new BroadcastChannel(channelName);
  if (onMessage) {
    channel.onmessage = (event) => onMessage(event.data?.generation ?? 0);
  }
  channels.set(channelName, channel);
}

export function post(channelName, generation) {
  const channel = channels.get(channelName);
  if (channel) {
    channel.postMessage({ generation });
  }
}

export function close(channelName) {
  const channel = channels.get(channelName);
  if (channel) {
    channel.onmessage = null;
    channel.close();
    channels.delete(channelName);
  }
}

export function isSupported() {
  return typeof BroadcastChannel !== "undefined";
}
