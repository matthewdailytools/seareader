"""Loopback server for the SeaReader prototype and its link downloads."""
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
from urllib.parse import parse_qs, quote, unquote, urlsplit

HOST = '127.0.0.1'
PORT = 18765
ORIGIN = f'http://{HOST}:{PORT}'
PAGE = Path(__file__).with_name('SeaReader-四端原型.html')
CURL = shutil.which('curl.exe') or shutil.which('curl')


class SeaReaderHandler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path != '/':
            self.send_error(404)
            return
        content = PAGE.read_bytes()
        self.send_response(200)
        self.send_header('Content-Type', 'text/html; charset=utf-8')
        self.send_header('Content-Length', str(len(content)))
        self.end_headers()
        self.wfile.write(content)

    def do_POST(self):
        if self.path != '/download':
            self.send_error(404)
            return
        if self.headers.get('Origin') != ORIGIN or self.headers.get('Host') != f'{HOST}:{PORT}':
            self.send_error(403, 'Open the prototype through the local SeaReader server.')
            return
        try:
            length = int(self.headers.get('Content-Length', '0'))
            if not 0 < length <= 8192:
                raise ValueError
            url = parse_qs(self.rfile.read(length).decode('utf-8'))['url'][0].strip()
            parsed = urlsplit(url)
            if parsed.scheme not in {'http', 'https', 'ftp'} or not parsed.hostname:
                raise ValueError
        except (ValueError, KeyError):
            self.send_error(400, 'Enter a valid HTTP, HTTPS, or FTP file URL.')
            return

        filename = unquote(parsed.path.rsplit('/', 1)[-1]) or 'download'
        filename = re.sub(r'[\x00-\x1f\x7f<>:"/\\|?*]', '_', filename).strip(' .') or 'download'
        with tempfile.TemporaryDirectory(prefix='seareader-') as directory:
            target = Path(directory) / 'download'
            result = subprocess.run(
                [CURL, '--disable', '--globoff', '--silent', '--show-error', '--fail',
                 '--location', '--max-redirs', '5', '--connect-timeout', '15',
                 '--max-time', '300', '--proto', '=http,https,ftp',
                 '--proto-redir', '=http,https,ftp', '--output', str(target), '--', url],
                capture_output=True, check=False,
            )
            if result.returncode or not target.is_file():
                self.send_error(502, f'Download failed (curl {result.returncode}). Check the URL and try again.')
                return
            self.send_response(200)
            self.send_header('Content-Type', 'application/octet-stream')
            self.send_header('X-Content-Type-Options', 'nosniff')
            self.send_header('Content-Disposition', "attachment; filename*=UTF-8''" + quote(filename, safe=''))
            self.send_header('Content-Length', str(target.stat().st_size))
            self.end_headers()
            with target.open('rb') as stream:
                shutil.copyfileobj(stream, self.wfile)


if __name__ == '__main__':
    if not CURL:
        raise SystemExit('curl is required.')
    with ThreadingHTTPServer((HOST, PORT), SeaReaderHandler) as server:
        print(f'SeaReader: {ORIGIN}', flush=True)
        server.serve_forever()
