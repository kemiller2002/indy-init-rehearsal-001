// A fake GitHub Git Data API for the browser tests (PROVIDER-GITHUB.md §10.1): a real content-addressed object graph
// (git blob ids), trees with base_tree and nested paths, commits, and refs with fast-forward-only updates. It runs in
// the Playwright process behind page.route, so no request reaches github.com. Every request is logged.
import { createHash } from "node:crypto";

const cors = {
  "access-control-allow-origin": "*",
  "access-control-allow-headers": "*",
  "access-control-allow-methods": "GET, POST, PATCH, OPTIONS",
  "access-control-expose-headers": "*",
};

const sha1 = (data) => createHash("sha1").update(data).digest("hex");
export const blobId = (bytes) => sha1(Buffer.concat([Buffer.from(`blob ${bytes.length}\0`), bytes]));

export const createFake = ({ token, branch }) => {
  const objects = new Map();
  const refs = new Map();
  const log = [];
  const state = { offline: false, hooks: [] };

  const putTree = (entries) => {
    const sorted = [...entries].sort((a, b) => a.path.localeCompare(b.path));
    const id = sha1("tree:" + sorted.map((e) => `${e.mode} ${e.type} ${e.path} ${e.sha}`).join("|"));
    objects.set(id, { kind: "tree", entries: sorted });
    return id;
  };
  const putBlob = (bytes) => {
    const id = blobId(bytes);
    objects.set(id, { kind: "blob", bytes });
    return id;
  };
  const insert = (treeId, segments, blob) => {
    const entries = treeId ? [...(objects.get(treeId)?.entries ?? [])] : [];
    const [name, ...rest] = segments;
    const others = entries.filter((e) => e.path !== name);
    if (rest.length === 0) return putTree([...others, { path: name, mode: "100644", type: "blob", sha: blob }]);
    const sub = entries.find((e) => e.path === name && e.type === "tree")?.sha;
    return putTree([...others, { path: name, mode: "040000", type: "tree", sha: insert(sub, rest, blob) }]);
  };
  const commitFiles = (message, files) => {
    const parent = refs.get(branch);
    let tree = parent ? objects.get(parent).tree : undefined;
    for (const [path, bytes] of Object.entries(files)) tree = insert(tree, path.split("/"), putBlob(Buffer.from(bytes)));
    tree = tree ?? putTree([]);
    const parents = parent ? [parent] : [];
    const id = sha1(`commit:${tree}:${parents.join(",")}:${message}`);
    objects.set(id, { kind: "commit", tree, parents, message });
    refs.set(branch, id);
    return id;
  };
  const ancestors = (id, acc = new Set()) => {
    acc.add(id);
    for (const p of objects.get(id)?.parents ?? []) ancestors(p, acc);
    return acc;
  };
  const fileAt = (path) => {
    let tree = objects.get(objects.get(refs.get(branch)).tree);
    const parts = path.split("/");
    for (let i = 0; i < parts.length; i++) {
      const e = tree?.entries.find((x) => x.path === parts[i]);
      if (!e) return undefined;
      if (i === parts.length - 1) return objects.get(e.sha)?.bytes;
      tree = objects.get(e.sha);
    }
    return undefined;
  };

  const json = (status, body) => ({ status, headers: { ...cors, "content-type": "application/json" }, body: JSON.stringify(body) });
  const handle = (method, path, body) => {
    const m = path.match(/^\/repos\/[^/]+\/[^/]+\/git\/(.*)$/);
    if (!m) return json(404, { message: "Not Found" });
    const rest = m[1];
    if (method === "GET" && rest.startsWith("ref/heads/")) {
      const name = decodeURIComponent(rest.slice("ref/heads/".length));
      const sha = refs.get(name);
      return sha ? json(200, { ref: `refs/heads/${name}`, object: { type: "commit", sha } }) : json(404, { message: "Not Found" });
    }
    const [kind, id] = rest.split("/");
    if (method === "GET") {
      const o = objects.get(id);
      if (kind === "commits" && o?.kind === "commit") return json(200, { sha: id, tree: { sha: o.tree }, parents: o.parents.map((p) => ({ sha: p })) });
      if (kind === "trees" && o?.kind === "tree") return json(200, { sha: id, tree: o.entries, truncated: false });
      if (kind === "blobs" && o?.kind === "blob") return json(200, { sha: id, size: o.bytes.length, encoding: "base64", content: o.bytes.toString("base64").replace(/(.{60})/g, "$1\n") });
      return json(404, { message: "Not Found" });
    }
    if (method === "POST" && kind === "blobs") return json(201, { sha: putBlob(Buffer.from(body.content, body.encoding === "base64" ? "base64" : "utf8")) });
    if (method === "POST" && kind === "trees") {
      let tree = body.base_tree;
      for (const e of body.tree) tree = insert(tree, e.path.split("/"), e.sha);
      return json(201, { sha: tree, tree: objects.get(tree).entries, truncated: false });
    }
    if (method === "POST" && kind === "commits") {
      const sha = sha1(`commit:${body.tree}:${body.parents.join(",")}:${body.message}`);
      objects.set(sha, { kind: "commit", tree: body.tree, parents: body.parents, message: body.message });
      return json(201, { sha, tree: { sha: body.tree }, parents: body.parents.map((p) => ({ sha: p })) });
    }
    if (method === "PATCH" && rest.startsWith("refs/heads/")) {
      const name = decodeURIComponent(rest.slice("refs/heads/".length));
      const current = refs.get(name);
      if (!current) return json(422, { message: "Reference does not exist" });
      if (!ancestors(body.sha).has(current)) return json(422, { message: "Update is not a fast forward" });
      refs.set(name, body.sha);
      return json(200, { ref: `refs/heads/${name}`, object: { type: "commit", sha: body.sha } });
    }
    return json(404, { message: "Not Found" });
  };

  const install = async (page) => {
    await page.route("https://api.github.com/**", async (route) => {
      const request = route.request();
      if (request.method() === "OPTIONS") return route.fulfill({ status: 204, headers: cors });
      const url = new URL(request.url());
      const body = request.postData() ? JSON.parse(request.postData()) : undefined;
      log.push({ method: request.method(), url: request.url(), authorization: request.headers()["authorization"], body: request.postData() ?? "" });
      if (state.offline) return route.abort("internetdisconnected");
      if (token && request.headers()["authorization"] !== `Bearer ${token}`) return route.fulfill(json(401, { message: "Bad credentials" }));
      const hook = state.hooks.findIndex((h) => h.matches(request.method(), url.pathname));
      if (hook >= 0) {
        const [h] = state.hooks.splice(hook, 1);
        if (h.before) h.before();
        if (h.respond) return route.fulfill(json(h.respond.status, h.respond.body));
      }
      return route.fulfill(handle(request.method(), url.pathname, body));
    });
  };

  commitFiles("operator: data branch", { ".gitattributes": "* -text\n" });
  return { install, log, state, refs, commitFiles, fileAt, head: () => refs.get(branch) };
};
