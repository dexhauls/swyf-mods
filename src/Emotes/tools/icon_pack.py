"""Sorts an extracted Fortnite-style emote .unitypackage (folders with pathname/asset) into dances.

Each dance is "<Name> Start.anim" + "<Name> Loop.anim", or a single "<Name>.anim"; music lives in
Audio/<Name>/ as intro and loop .wav files. Duo variants ("(Player 2)") are skipped.

    python icon_pack.py <extracted package> <out folder>
writes <out>/anims/<Name>/{start,loop}.anim and <out>/audio/<Name>/{intro,loop}.wav and prints a summary.
"""
import os
import re
import shutil
import sys


def entries(pack):
    for d in os.listdir(pack):
        p = os.path.join(pack, d, "pathname")
        a = os.path.join(pack, d, "asset")
        if os.path.exists(p) and os.path.exists(a):
            yield open(p, encoding="utf-8").readline().strip(), a


def base_name(stem):
    s = re.sub(r"\s*\((Player|P)\s*\d\)\s*$", "", stem, flags=re.I)
    m = re.match(r"^(.*?)\s+(Start|Intro|Loop|End|Outro)\s*\d*$", s, flags=re.I)
    return (m.group(1).strip(), m.group(2).lower()) if m else (s.strip(), "single")


def key(name):
    return "".join(c for c in name.lower() if c.isalnum())


def pick_audio(files):
    """files: list of (filename, path). Returns (intro, loop) paths."""
    def score(name, words):
        n = name.lower()
        return any(w in n for w in words)
    music = [f for f in files if not score(f[0], ("foley", "sfx", "vo_", "_vo", "idle", "outro", "end"))] or files
    intro = [f for f in music if score(f[0], ("intro", "start"))]
    loop = [f for f in music if score(f[0], ("loop",))]
    if not loop:
        loop = [f for f in music if f not in intro]
    loop.sort(key=lambda f: -os.path.getsize(f[1]))
    intro.sort(key=lambda f: -os.path.getsize(f[1]))
    return (intro[0][1] if intro else None), (loop[0][1] if loop else None)


def main():
    pack, out = sys.argv[1], sys.argv[2]
    anims, audio = {}, {}
    for path, asset in entries(pack):
        parts = path.split("/")
        if path.lower().endswith(".anim"):
            stem = parts[-1][:-5]
            if re.search(r"\((Player|P)\s*[2-9]\)", stem, flags=re.I):
                continue
            name, part = base_name(stem)
            anims.setdefault(name, {})[part] = asset
        elif path.lower().endswith(".wav") and "Audio" in parts:
            folder = parts[parts.index("Audio") + 1]
            audio.setdefault(folder, []).append((parts[-1], asset))

    audio_by_key = {key(k): v for k, v in audio.items()}
    shutil.rmtree(out, ignore_errors=True)
    done = 0
    for name, parts in sorted(anims.items()):
        start = parts.get("start") or parts.get("intro")
        loop = parts.get("loop") or parts.get("single")
        if loop is None:
            loop, start = start, None
        if loop is None:
            continue
        d = os.path.join(out, "anims", name)
        os.makedirs(d, exist_ok=True)
        if start:
            shutil.copyfile(start, os.path.join(d, "start.anim"))
        shutil.copyfile(loop, os.path.join(d, "loop.anim"))
        intro_wav, loop_wav = pick_audio(audio.get(name) or audio_by_key.get(key(name)) or audio_by_key.get(key(re.sub(r"\s*\d+$", "", name)), []))
        if loop_wav:
            ad = os.path.join(out, "audio", name)
            os.makedirs(ad, exist_ok=True)
            if intro_wav:
                shutil.copyfile(intro_wav, os.path.join(ad, "intro.wav"))
            shutil.copyfile(loop_wav, os.path.join(ad, "loop.wav"))
        done += 1
        print("%-32s start=%-3s music=%s" % (name, "yes" if start else "no", ("intro+loop" if intro_wav else "loop") if loop_wav else "-"))
    print("%d dances" % done)


if __name__ == "__main__":
    main()
