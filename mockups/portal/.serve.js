const http = require('http'), fs = require('fs'), path = require('path');
const root = __dirname;
const types = { '.html':'text/html; charset=utf-8', '.md':'text/plain; charset=utf-8' };
http.createServer((req, res) => {
  let p = decodeURIComponent(req.url.split('?')[0]);
  if (p === '/') {
    const files = fs.readdirSync(root).filter(f => f.endsWith('.html'));
    res.writeHead(200, { 'Content-Type': types['.html'] });
    return res.end(`<meta charset="utf-8"><title>Portal proposals</title>
      <style>body{font:16px/1.6 system-ui;margin:0;padding:48px;background:#eef2f7;color:#16202e}
      a{display:block;padding:16px 20px;margin:0 0 10px;background:#fff;border-radius:10px;
      text-decoration:none;color:#1a6da8;font-weight:600;box-shadow:0 1px 2px rgba(0,0,0,.06)}
      h1{font-size:1.4rem}</style><h1>CieloOS portal — proposals</h1>` +
      files.map(f => `<a href="/${f}">${f.replace('.html','')}</a>`).join(''));
  }
  const file = path.join(root, path.basename(p));
  if (!fs.existsSync(file)) { res.writeHead(404); return res.end('not found'); }
  res.writeHead(200, { 'Content-Type': types[path.extname(file)] || 'application/octet-stream' });
  fs.createReadStream(file).pipe(res);
}).listen(5300, '127.0.0.1', () => console.log('mockup server on http://localhost:5300/'));
