#!/usr/bin/env bash
# Create a Gitea release: tag → push → POST release → upload assets.
#
# Usage:
#   scripts/gitea-release.sh TAG "Release Name" file1 [file2 ...]
#
# Reads token from $HOME/.config/vertex/gitea-token (chmod 600).

set -euo pipefail

if [ "$#" -lt 3 ]; then
    echo "Usage: $0 TAG \"Release Name\" file1 [file2 ...]" >&2
    exit 2
fi

TAG="$1"; NAME="$2"; shift 2
TOKEN_FILE="${GITEA_TOKEN_FILE:-$HOME/.config/vertex/gitea-token}"
API="${GITEA_API:-https://git.it-laboratory.com/api/v1/repos/mrkutin/vertex}"

if [ ! -r "$TOKEN_FILE" ]; then
    echo "Token file not readable: $TOKEN_FILE" >&2
    exit 1
fi
TOKEN=$(cat "$TOKEN_FILE")

# Sanity: every asset must exist before we touch git.
for f in "$@"; do
    if [ ! -f "$f" ]; then
        echo "Asset missing: $f" >&2
        exit 1
    fi
done

if ! git rev-parse --verify "$TAG" >/dev/null 2>&1; then
    git tag -a "$TAG" -m "$NAME"
fi
git push origin "$TAG" 2>&1 | grep -v "^Everything up-to-date" || true

# POST release. If the connection drops mid-response (curl 56) the release
# may already exist on the server — fall back to GET-by-tag and reuse it.
REL_JSON=$(curl -sS -H "Authorization: token $TOKEN" -H "Content-Type: application/json" \
    -d "$(jq -n --arg tag "$TAG" --arg name "$NAME" '{tag_name:$tag,name:$name}')" \
    "$API/releases" || true)
REL_ID=$(echo "$REL_JSON" | jq -r '.id // empty')
if [ -z "$REL_ID" ] || [ "$REL_ID" = "null" ]; then
    echo "POST /releases didn't return an id; checking if release exists for tag..."
    REL_JSON=$(curl -sfS -H "Authorization: token $TOKEN" "$API/releases/tags/$TAG")
    REL_ID=$(echo "$REL_JSON" | jq -r '.id // empty')
    if [ -z "$REL_ID" ] || [ "$REL_ID" = "null" ]; then
        echo "Failed to create or find release for $TAG" >&2
        exit 1
    fi
    echo "Recovered existing release: id=$REL_ID"
fi
REL_URL=$(echo "$REL_JSON" | jq -r .html_url)
echo "Release: id=$REL_ID  $REL_URL"

for f in "$@"; do
    NAME_PARAM=$(basename "$f")
    echo "  uploading $NAME_PARAM ($(du -h "$f" | awk '{print $1}'))..."
    curl -sfS -H "Authorization: token $TOKEN" \
        -F "attachment=@$f" "$API/releases/$REL_ID/assets?name=$NAME_PARAM" \
        | jq -r '"    -> " + .browser_download_url'
done

echo "Done: $REL_URL"
