const http = require("http");

const port = Number(process.argv[2] || 55252);
const label = process.argv[3] || "node-fast";
const holdMs = Number(process.argv[4] || 10000);

const server = http.createServer((req, res) => {
  const address = server.address();
  res.writeHead(200, { "content-type": "text/plain" });
  res.end(`${label} ${address.address}:${address.port}\n`);
});

server.listen(port, "127.0.0.1", () => {
  const address = server.address();
  console.log(`NODE_BOUND ${label} ${address.address}:${address.port} pid=${process.pid}`);
});

setTimeout(() => server.close(() => process.exit(0)), holdMs);
