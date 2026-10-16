#!/usr/bin/env python3
"""Deterministic subprocess fixtures. No TikTok requests or real credentials."""
import json
import os
from pathlib import Path
import sys
import time

root = Path(os.environ['SOAP_TEST_ROOT'])
args = sys.argv[1:]
tool = Path(sys.argv[0]).name
if tool == 'yt-dlp':
    if '--version' in args:
        sys.exit(1) if (root / 'unavailable').exists() else print('test-fixture')
    else:
        url = args[-1]
        with (root / 'calls.jsonl').open('a') as log:
            log.write(json.dumps({'args': args, 'time': time.time()}) + '\n')
        if '--flat-playlist' in args:
            print('\n'.join(json.loads((root / 'scrape-urls.json').read_text())))
        elif '--print' in args:
            if 'fail' in url and not (root / 'allow-retry').exists():
                print('ERROR: Sign in to download this video', file=sys.stderr)
                sys.exit(1)
            print(json.dumps({'title': 'Fixture video', 'uploader': 'creator', 'duration': 3}))
        else:
            output = Path(args[args.index('-o') + 1])
            output.write_bytes(b'fixture media')
            if 'slow' in url:
                (root / 'download-started').touch()
                while not (root / 'finish-download').exists():
                    time.sleep(.02)
elif tool == 'ffprobe':
    print('hevc' if (root / 'transcode').exists() else 'h264')
elif tool == 'ffmpeg':
    # Fill both pipes far beyond OS capacity before returning, like a long transcode.
    sys.stdout.write('x' * 1048576)
    sys.stdout.flush()
    sys.stderr.write('x' * 1048576)
    sys.stderr.flush()
    if (root / 'transcode-fails').exists():
        sys.exit(1)
    Path(args[-1]).write_bytes(b'fixture converted media or poster')
