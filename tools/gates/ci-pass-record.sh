#!/bin/bash
# The CI pass record (#1050): CI saves an artifact named gates-pass-<tree>-<settings hash> after a green run, and a push to
# main looks it up to skip a tree that already passed on its PR. (An actions/cache entry can't do this: a cache saved on a
# PR ref is not readable from main. An artifact is found by name through the API from any ref.)
#   ci-pass-record.sh key           print the key for HEAD's tree and the gate settings
#   ci-pass-record.sh lookup <key>  print the id of the newest run that saved <key> and hasn't expired; nothing if none
# The settings hashed are the ones that change what the gate checks: GATES_NO_KSP and KOS_RELEASE_TAG. Lookup needs
# GITHUB_REPOSITORY and GH_TOKEN (actions: read).
set -u
case "${1:-}" in
  key)
    settings=$(printf 'GATES_NO_KSP=%s KOS_RELEASE_TAG=%s' "${GATES_NO_KSP:-0}" "${KOS_RELEASE_TAG:-}" | git hash-object --stdin | cut -c1-12)
    echo "gates-pass-$(git rev-parse 'HEAD^{tree}')-$settings" ;;
  lookup)
    [ -n "${2:-}" ] || { echo "usage: ci-pass-record.sh lookup <key>" >&2; exit 2; }
    gh api "repos/$GITHUB_REPOSITORY/actions/artifacts?name=$2&per_page=100" \
      --jq '[.artifacts[] | select(.expired == false)] | sort_by(.created_at) | last | .workflow_run.id // empty' ;;
  *) echo "usage: ci-pass-record.sh key | lookup <key>" >&2; exit 2 ;;
esac
