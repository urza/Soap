"""Black-box queue and media regression checks. Run after dotnet build -c Release."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import signal
import socket
import subprocess
import tempfile
import time
import unittest
import urllib.error
import urllib.parse
import urllib.request

REPO = Path(__file__).resolve().parents[1]


def url_hash(url):
    return hashlib.sha256(url.encode()).hexdigest()[:16]


class DownloadTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='soap-regression-')
        self.root = Path(self.temp.name)
        self.data = self.root / 'Data'
        self.cache = self.data / 'media-cache'
        self.cache.mkdir(parents=True)
        self.bin = self.root / 'bin'
        self.bin.mkdir()
        for tool in ['yt-dlp', 'ffmpeg', 'ffprobe']:
            target = self.bin / tool
            shutil.copy(REPO / 'tests/fake_media_tool.py', target)
            target.chmod(0o755)
        self.process = None
        self.log = None

    def tearDown(self):
        self.stop()
        self.temp.cleanup()

    def stop(self):
        if self.process:
            os.killpg(self.process.pid, signal.SIGTERM)
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                os.killpg(self.process.pid, signal.SIGKILL)
                self.process.wait()
            self.log.close()
            self.process = None

    def start(self):
        with socket.socket() as sock:
            sock.bind(('127.0.0.1', 0))
            port = sock.getsockname()[1]
        self.base = f'http://127.0.0.1:{port}'
        env = dict(os.environ, PATH=str(self.bin) + ':' + os.environ['PATH'], SOAP_TEST_ROOT=str(self.root))
        self.log = (self.root / 'server.log').open('a')
        self.process = subprocess.Popen([
            'dotnet', str(REPO / 'Soap/bin/Release/net10.0/Soap.dll'),
            '--contentRoot', str(self.root), '--webroot', str(REPO / 'Soap/wwwroot'), '--urls', self.base,
        ], env=env, stdout=self.log, stderr=self.log, start_new_session=True)
        self.wait_for(lambda: self.request('/health'))
        self.request('/pacing', {'minMs': 0, 'maxMs': 0})

    def request(self, path, data=None):
        request = urllib.request.Request(self.base + path,
            data=None if data is None else json.dumps(data).encode(),
            headers={'Content-Type': 'application/json'})
        with urllib.request.urlopen(request, timeout=3) as response:
            return json.load(response)

    def wait_for(self, predicate, timeout=12):
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            try:
                result = predicate()
                if result:
                    return result
            except (urllib.error.URLError, ConnectionError):
                pass
            time.sleep(.03)
        self.fail('Timed out. Server log:\n' + (self.root / 'server.log').read_text()[-5000:])

    def add(self, url):
        return self.request('/reposts/bulk', {'urls': [url]})

    def status(self, url):
        return next((e for e in self.request('/reposts') if e['url'] == url), {})

    def calls(self):
        path = self.root / 'calls.jsonl'
        return [json.loads(line) for line in path.read_text().splitlines()] if path.exists() else []

    def test_completed_cache_import_finishes_without_downloading_even_when_paused(self):
        url = 'http://127.0.0.1/cached'
        (self.cache / (url_hash(url) + '.mp4')).write_bytes(b'finished')
        self.start()
        self.request('/downloads/pause', {'paused': True})
        self.add(url)
        self.assertEqual('Ready', self.status(url)['status'])
        self.assertEqual([], self.calls())

    def test_restart_reconciles_finished_file_with_pending_gallery_entry(self):
        url = 'http://127.0.0.1/restart'
        (self.cache / (url_hash(url) + '.mp4')).write_bytes(b'finished')
        (self.data / 'reposts.json').write_text(json.dumps([{'Url': url, 'Status': 'Downloading'}]))
        self.start()
        self.assertEqual('Ready', self.status(url)['status'])
        self.assertEqual([], self.calls())

    def test_partial_poster_fragment_and_empty_files_do_not_count_as_media(self):
        self.start()
        for index, suffix in enumerate(['.mp4.part', '.jpg', '.f137.mp4', '.mp4']):
            with self.subTest(suffix=suffix):
                url = f'http://127.0.0.1/partial-{index}'
                (self.cache / (url_hash(url) + suffix)).write_bytes(b'' if suffix == '.mp4' else b'partial')
                try:
                    self.request('/cache/media?url=' + urllib.parse.quote(url))
                    self.fail('Incomplete media was accepted')
                except urllib.error.HTTPError as error:
                    self.assertEqual(404, error.code)
                    error.close()
                self.add(url)
                self.wait_for(lambda: self.status(url).get('status') == 'Ready')
                self.assertTrue(self.status(url)['cachedMediaUrl'].endswith('.mp4'))
        self.assertEqual(8, len(self.calls()))

    def test_single_retry_during_cooldown_is_processed_after_the_delay(self):
        url = 'http://127.0.0.1/fail-once'
        self.start()
        self.request('/pacing', {'minMs': 1500, 'maxMs': 1500})
        self.add(url)
        self.wait_for(lambda: self.status(url).get('status') == 'Failed')
        (self.root / 'allow-retry').touch()
        self.request('/reposts/' + url_hash(url) + '/retry', {})
        self.wait_for(lambda: self.status(url).get('status') == 'Ready')
        probes = [c for c in self.calls() if '--print' in c['args']]
        self.assertEqual(2, len(probes))
        self.assertGreaterEqual(probes[1]['time'] - probes[0]['time'], 1.4)

    def test_bulk_retry_during_cooldown_finishes(self):
        url = 'http://127.0.0.1/fail-bulk'
        self.start()
        self.request('/pacing', {'minMs': 1500, 'maxMs': 1500})
        self.add(url)
        self.wait_for(lambda: self.status(url).get('status') == 'Failed')
        (self.root / 'allow-retry').touch()
        self.assertEqual(1, self.request('/reposts/retry-failed', {})['retried'])
        self.wait_for(lambda: self.status(url).get('status') == 'Ready')

    def test_noisy_transcode_and_poster_finish_without_pipe_deadlock(self):
        (self.root / 'transcode').touch()
        url = 'http://127.0.0.1/noisy-transcode'
        self.start()
        self.add(url)
        self.wait_for(lambda: self.status(url).get('status') == 'Ready')
        self.assertTrue(self.status(url)['thumbnailUrl'].endswith('.jpg'))
        self.assertEqual(b'fixture converted media or poster', (self.cache / (url_hash(url) + '.mp4')).read_bytes())

    def test_failed_transcode_is_retryable_and_not_published_as_ready(self):
        (self.root / 'transcode').touch()
        (self.root / 'transcode-fails').touch()
        url = 'http://127.0.0.1/convert-error'
        self.start()
        self.add(url)
        self.wait_for(lambda: self.status(url).get('status') == 'Failed')
        self.assertIn('prepare this video', self.status(url)['errorMessage'])
        self.assertFalse((self.cache / (url_hash(url) + '.mp4')).exists())
        (self.root / 'transcode-fails').unlink()
        self.request('/reposts/' + url_hash(url) + '/retry', {})
        self.wait_for(lambda: self.status(url).get('status') == 'Ready')

    def test_active_download_is_not_exposed_as_completed_media(self):
        url = 'http://127.0.0.1/slow-download'
        self.start()
        self.add(url)
        self.wait_for(lambda: (self.root / 'download-started').exists())
        self.assertEqual('Downloading', self.status(url)['status'])
        with self.assertRaises(urllib.error.HTTPError) as context:
            self.request('/cache/media?url=' + urllib.parse.quote(url))
        self.assertEqual(404, context.exception.code)
        context.exception.close()
        self.assertFalse((self.cache / (url_hash(url) + '.mp4')).exists())
        (self.root / 'finish-download').touch()
        self.wait_for(lambda: self.status(url).get('status') == 'Ready')
        self.assertFalse((self.data / 'media-work' / url_hash(url)).exists())

    def test_missing_downloader_reports_failure_and_cached_files_still_work(self):
        (self.root / 'unavailable').touch()
        self.start()
        url = 'http://127.0.0.1/unavailable'
        self.add(url)
        self.assertEqual('Failed', self.status(url)['status'])
        self.assertIn('yt-dlp is not installed', self.status(url)['errorMessage'])
        cached = 'http://127.0.0.1/offline-cache'
        (self.cache / (url_hash(cached) + '.mp4')).write_bytes(b'finished')
        self.add(cached)
        self.assertEqual('Ready', self.status(cached)['status'])

    def test_newest_first_import_order_survives_completion_and_restart(self):
        urls = ['http://127.0.0.1/newest-repost', 'http://127.0.0.1/middle-repost', 'http://127.0.0.1/oldest-repost']
        self.start()
        self.request('/reposts/bulk', {'urls': urls})
        self.wait_for(lambda: all(e['status'] == 'Ready' for e in self.request('/reposts')))
        self.assertEqual(urls, [e['url'] for e in self.request('/reposts')])
        saved = self.data / 'reposts.json'
        self.wait_for(lambda: saved.exists() and all(e['Status'] == 'Ready' for e in json.loads(saved.read_text())))
        self.assertEqual(urls, [e['Url'] for e in json.loads(saved.read_text())])
        self.stop()
        self.start()
        self.assertEqual(urls, [e['url'] for e in self.request('/reposts')])

    def test_new_batches_and_reimports_keep_source_order_without_requeueing(self):
        urls = ['http://127.0.0.1/newer', 'http://127.0.0.1/older']
        self.start()
        self.request('/downloads/pause', {'paused': True})
        self.request('/reposts/bulk', {'urls': urls})
        dates = {e['url']: e['addedAt'] for e in self.request('/reposts')}
        newest = 'http://127.0.0.1/newest'
        self.add(newest)
        self.assertEqual([newest] + urls, [e['url'] for e in self.request('/reposts')])
        reordered = [urls[1], newest, urls[0]]
        result = self.request('/reposts/bulk', {'urls': reordered + [urls[1]]})
        self.assertEqual(0, result['queued'])
        self.assertEqual(4, result['alreadyKnown'])
        entries = self.request('/reposts')
        self.assertEqual(reordered, [e['url'] for e in entries])
        for entry in entries:
            if entry['url'] in dates:
                self.assertEqual(dates[entry['url']], entry['addedAt'])
        self.assertEqual([], self.calls())

    def test_old_gallery_keeps_its_order_until_a_source_list_is_imported(self):
        urls = ['http://127.0.0.1/legacy-new', 'http://127.0.0.1/legacy-old']
        (self.data / 'reposts.json').write_text(json.dumps([
            {'Url': urls[1], 'Status': 'Ready', 'AddedAt': '2026-01-01T00:00:00Z'},
            {'Url': urls[0], 'Status': 'Ready', 'AddedAt': '2026-02-01T00:00:00Z'},
        ]))
        self.start()
        self.assertEqual(urls, [e['url'] for e in self.request('/reposts')])
        corrected = list(reversed(urls))
        self.request('/reposts/bulk', {'urls': corrected})
        self.assertEqual(corrected, [e['url'] for e in self.request('/reposts')])
        self.assertTrue(all(e['status'] == 'Ready' for e in self.request('/reposts')))
        self.assertEqual([], self.calls())

    def test_scraper_preserves_newest_first_feed_order(self):
        urls = ['http://127.0.0.1/feed-newest', 'http://127.0.0.1/feed-oldest']
        (self.root / 'scrape-urls.json').write_text(json.dumps(urls))
        self.start()
        self.request('/downloads/pause', {'paused': True})
        result = self.request('/scrape', {'username': 'fixture'})
        self.assertEqual(2, result['queued'])
        self.assertEqual(urls, [e['url'] for e in self.request('/reposts')])


if __name__ == '__main__':
    unittest.main(verbosity=2)
