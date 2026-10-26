// Idempotent single-node replica set bootstrap. Also used as the container healthcheck:
// it initiates rs0 when needed and exits non-zero until this node is PRIMARY.
const config = {
  _id: "rs0",
  members: [{ _id: 0, host: "mongo:27017" }],
};

try {
  rs.status();
} catch (error) {
  rs.initiate(config);
}

if (!db.hello().isWritablePrimary) {
  quit(1);
}
