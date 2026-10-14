#!/usr/bin/env bash
#
# Photograph the mod icon in-game and put the name on it.
#
#   bash scripts/make-icon.sh [user@host]      (default: dizzyd@vsclient.home)
#
# shots/IconShot.cs stages the scene; this boots a square client for it on the test box,
# takes the shot, and uses ImageMagick there to scale it to 480x480 and set "Lodestone"
# across the bottom in Palatino Bold. The result comes back as scripts/out/modicon-candidate.png,
# with the untouched shot beside it. It does not replace lodestone/modicon.png: the shipped
# icon is chosen and committed by hand.
#
# Shadows stay off: on the headless box they crash the client at startup (see Packrat's
# make-shots.sh). The template is swapped for the boot and put straight back.

set -euo pipefail

HOST="${1:-dizzyd@vsclient.home}"
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SLOT="$(basename "$REPO")"
TREE="vstestkit-$SLOT"
OUT="$REPO/scripts/out"
RAW=/tmp/lodestone-icon

ssh "$HOST" "cd $TREE && bash scripts/stop.sh >/dev/null 2>&1 || true" 2>/dev/null || true

echo "==> syncing to $HOST"
( cd "$REPO/../vstestkit" && bash scripts/sync-linux.sh "$HOST" --mod "$REPO/lodestone" >/dev/null )
rsync -a --delete "$REPO/shots/" "$HOST:$TREE/mods/$SLOT/shots/" --exclude bin --exclude obj

echo "==> booting a 1000x1000 client"
ssh "$HOST" bash -s "$TREE" "$RAW" <<'REMOTE'
set -euo pipefail
TREE="$1"; RAW="$2"
cd "$HOME/$TREE"

cp templates/clientsettings.json /tmp/lodestone-clientsettings.bak
trap 'cp /tmp/lodestone-clientsettings.bak templates/clientsettings.json' EXIT
python3 - <<'PY'
import json
p = "templates/clientsettings.json"
d = json.load(open(p))
d["intSettings"].update({"screenWidth": 1000, "screenHeight": 1000, "viewDistance": 128, "mipmapLevel": 3, "shadowMapQuality": 0})
d["boolSettings"].update({"renderParticles": True, "ambientParticles": True, "wavingStuff": True})
d["floatSettings"].update({"ssaa": 2.0})
json.dump(d, open(p, "w"), indent=2)
PY

mkdir -p "$RAW" && rm -f "$RAW"/*.png
# Read inside the game process, so it has to be set for the boot, not for run.sh
VSTK_SHOT_DIR="$RAW" bash scripts/boot.sh --client

SIZE=$(bash scripts/vstk eval --side client 'return capi.Render.FrameWidth + "x" + capi.Render.FrameHeight;' \
       | python3 -c 'import sys,json; print(json.load(sys.stdin)["result"]["value"])')
echo "    client window: $SIZE"
[ "$SIZE" = "1000x1000" ] || { echo "expected 1000x1000" >&2; exit 1; }
REMOTE

echo "==> taking the shot"
ssh "$HOST" "cd \$HOME/$TREE && bash scripts/run.sh mods/$SLOT/shots --client --keep" | grep -E "^ok|^FAIL|^ERR|\||passed," || true
ssh "$HOST" "cd \$HOME/$TREE && bash scripts/stop.sh" >/dev/null 2>&1 || true

echo "==> scaling and lettering"
ssh "$HOST" bash -s "$RAW" <<'REMOTE'
set -euo pipefail
RAW="$1"
test -f "$RAW/icon-source.png"
# A dark band under the name so it reads on any stone, then the name twice: a thick dark
# stroke for the outline, and the fill on top of it without one.
convert "$RAW/icon-source.png" -resize 480x480 \
  \( -size 480x150 gradient:'rgba(0,0,0,0)'-'rgba(0,0,0,0.7)' \) -gravity south -composite \
  -font Palatino-Bold -pointsize 78 -gravity south \
  -fill '#1a130d' -stroke '#1a130d' -strokewidth 10 -annotate +0+22 'Lodestone' \
  -fill '#efe3c2' -stroke none -annotate +0+22 'Lodestone' \
  -strip -colors 256 "$RAW/modicon-candidate.png"
REMOTE

mkdir -p "$OUT"
rsync -a "$HOST:$RAW/icon-source.png" "$HOST:$RAW/modicon-candidate.png" "$OUT/"
ls -la "$OUT"
echo
echo "modicon-candidate.png is a candidate only; copy it to lodestone/modicon.png to ship it."
