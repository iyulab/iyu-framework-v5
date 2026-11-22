// Exit 0 when the Node.js running this is one the screen's build supports (the lower bound is package.json's `engines.node`), else 3.
// The C# build runs it to tell "Node is here" from "Node is absent or too old" before it tries to build the screen. Written to run on
// any Node, old ones included: it reads the version, nothing else.
const [major, minor] = process.versions.node.split('.').map(Number);
const ok = major > 22 || (major === 22 && minor >= 12);
console.log(`node ${process.versions.node}${ok ? '' : ' - the board screen needs 22.12 or later'}`);
process.exit(ok ? 0 : 3);
