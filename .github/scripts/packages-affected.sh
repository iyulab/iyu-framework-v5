#!/usr/bin/env bash
#
# Every Iyu.* package ships on one shared version number, so the version alone cannot tell a
# consumer whether the code they depend on moved. CHANGELOG.md answers that with a
# "Packages affected" line per release, and its headnote turns that line into a promise:
#
#   "if your dependency is not listed, that release changed nothing you consume."
#
# That promise is only as good as a hand-maintained list, and a hand-maintained list has
# already been wrong -- a package was touched and left off. This script makes the claim
# checkable: the declared list is compared against what actually changed in the release
# range, derived from git.
#
# It enforces UNDER-claiming only: a package whose files changed must appear in the list.
# Listing a package whose files did not change is left alone on purpose -- a maintainer
# legitimately lists a package whose behaviour moved because a dependency of it changed,
# and a guard that fought that judgment would be turned off rather than obeyed.
#
# Usage:
#   packages-affected.sh released     # hard check on the newest dated release; exits non-zero
#   packages-affected.sh unreleased   # advisory check on [Unreleased]; never fails the build
#
# Run from the repository root, on a checkout with full history and tags (fetch-depth: 0).

set -euo pipefail

MODE="${1:-}"
case "$MODE" in
  released|unreleased) ;;
  *) echo "usage: $0 released|unreleased" >&2; exit 2 ;;
esac

CHANGELOG="CHANGELOG.md"

# --- what a consumer can actually depend on ----------------------------------------------
#
# Derived from IsPackable rather than a list written here, so adding a project that does not
# ship needs no edit to this guard -- and cannot silently widen what it checks.
shipped_packages() {
  local proj
  for proj in src/*/*.csproj; do
    grep -q '<IsPackable>false</IsPackable>' "$proj" && continue
    basename "$(dirname "$proj")"
  done
}

# --- CHANGELOG parsing --------------------------------------------------------------------
dated_versions() {  # newest first
  grep -oP '^## \[\K[0-9]+\.[0-9]+\.[0-9]+(?=\] - )' "$CHANGELOG"
}

# The package names on the "Packages affected" line of one release block.
declared_packages() {  # $1 = "Unreleased" or a version
  awk -v v="$1" '
    $0 == "## [" v "]" || index($0, "## [" v "] - ") == 1 { inblock = 1; next }
    inblock && index($0, "## [") == 1 { exit }
    inblock && index($0, "**Packages affected:**") == 1 { print; exit }
  ' "$CHANGELOG" | grep -oP '`[^`]+`' | tr -d '`' || true
}
# Matching whole `...` spans (rather than the text after a backtick) is what makes the
# alternation come out right: consuming both delimiters means the ", " between two names
# cannot be read as a third name.

# A block that mentions the list in some other shape reads to this guard as "declares nothing",
# which would then be reported as every changed package being omitted -- loud, but about the
# wrong thing. Naming the real cause is what keeps the failure actionable. This function is
# also where the canonical form is documented: the error text is the only place a maintainer
# meets it, so it states the form rather than pointing elsewhere.
mentions_list_unparseably() {  # $1 = "Unreleased" or a version
  awk -v v="$1" '
    $0 == "## [" v "]" || index($0, "## [" v "] - ") == 1 { inblock = 1; next }
    inblock && index($0, "## [") == 1 { exit }
    inblock && index($0, "**Packages affected:**") == 1 { found = 0; exit }
    inblock && /Packages affected/ { found = 1; line = $0; exit }
    END { if (found) print line }
  ' "$CHANGELOG"
}

# --- what actually changed ----------------------------------------------------------------
#
# Only src/<Package>/** counts. Root-level files are excluded deliberately: Directory.Build.props
# carries the shared <Version> and therefore changes on every single release, so mapping it to
# "all packages" would make this guard pass trivially and forever.
changed_packages() {  # $1 = git range
  git diff --name-only "$1" -- src/ | awk -F/ 'NF > 2 { print $2 }' | sort -u
}

require_tag() {  # $1 = tag, $2 = what it is needed for
  if ! git rev-parse -q --verify "refs/tags/$1" >/dev/null; then
    echo "::error::tag '$1' does not exist, so $2 cannot be determined. CHANGELOG.md records" \
         "releases from 0.9.0 onward while tags go back further, so a dated release header" \
         "whose tag is missing means the header is wrong, not that the check is inapplicable." \
         "Either the header names a version that was never released, or the checkout has no" \
         "tags (this job needs fetch-depth: 0)."
    exit 1
  fi
}

# ------------------------------------------------------------------------------------------
mapfile -t VERSIONS < <(dated_versions)

if [ "${#VERSIONS[@]}" -eq 0 ]; then
  echo "::error::no dated release header (## [X.Y.Z] - DATE) found in $CHANGELOG."
  exit 1
fi

LATEST="${VERSIONS[0]}"

if [ "$MODE" = "released" ]; then
  TARGET="$LATEST"
  # This mode assumes HEAD is the commit being released as $LATEST -- the range below is built
  # on that assumption, so it is checked rather than trusted.
  if [ -n "${GITHUB_REF_NAME:-}" ] && [ "$GITHUB_REF_NAME" != "v$LATEST" ]; then
    echo "::error::releasing tag $GITHUB_REF_NAME, but the newest dated header in $CHANGELOG" \
         "is $LATEST. The release being published must be the one the changelog describes;" \
         "otherwise the notes shipped inside the package belong to a different version."
    exit 1
  fi
  if [ "${#VERSIONS[@]}" -lt 2 ]; then
    echo "::notice::$TARGET is the oldest release recorded in $CHANGELOG, so there is no" \
         "earlier release to diff from. Nothing to check."
    exit 0
  fi
  BASE_TAG="v${VERSIONS[1]}"
  require_tag "$BASE_TAG" "the range of release $TARGET"
  # HEAD is the release commit: this job runs from the tag being released, so the range needs
  # no tag object for the new version and cannot race the tag's arrival.
  RANGE="$BASE_TAG..HEAD"
  SEVERITY="error"
else
  TARGET="Unreleased"
  BASE_TAG="v$LATEST"
  if ! git rev-parse -q --verify "refs/tags/$BASE_TAG" >/dev/null; then
    echo "::notice::tag $BASE_TAG does not exist yet, so [Unreleased] has no baseline to" \
         "diff from. The changelog-tag job reports that condition; not repeating it here."
    exit 0
  fi
  RANGE="$BASE_TAG..HEAD"
  SEVERITY="warning"
fi

echo "Range:    $RANGE"
echo "Checking: [$TARGET]"

mapfile -t SHIPPED  < <(shipped_packages | sort -u)
mapfile -t DECLARED < <(declared_packages "$TARGET" | sort -u)
mapfile -t CHANGED  < <(changed_packages "$RANGE")

if [ "${#DECLARED[@]}" -eq 0 ]; then
  MALFORMED="$(mentions_list_unparseably "$TARGET")"
  if [ -n "$MALFORMED" ]; then
    echo "::${SEVERITY}::[$TARGET] mentions the affected packages in a shape this guard cannot" \
         "read, so it counts as declaring none. Found: ${MALFORMED}. The form it reads is a" \
         'line beginning `**Packages affected:**` followed by the package names in backticks,' \
         "e.g. **Packages affected:** \`Iyu.Core\`, \`Iyu.Data\`."
    if [ "$SEVERITY" = "error" ]; then
      exit 1
    fi
  fi
fi

echo "Shipped packages:  ${SHIPPED[*]:-(none)}"
echo "Declared:          ${DECLARED[*]:-(none)}"
echo "Changed under src: ${CHANGED[*]:-(none)}"

missing=()
for pkg in "${CHANGED[@]}"; do
  # A changed directory that is not a shipped package (the test project) is not a consumer's
  # concern and must never be listed.
  printf '%s\n' "${SHIPPED[@]}" | grep -qxF "$pkg" || continue
  printf '%s\n' "${DECLARED[@]}" | grep -qxF "$pkg" || missing+=("$pkg")
done

if [ "${#missing[@]}" -eq 0 ]; then
  echo "OK: every shipped package changed in $RANGE is declared under [$TARGET]."
  exit 0
fi

echo "::${SEVERITY}::[$TARGET] omits ${missing[*]} from 'Packages affected', but files under" \
     "src/<package>/ changed in $RANGE. The headnote of $CHANGELOG promises that a package" \
     "absent from that line changed nothing the consumer depends on, so the omission makes" \
     "the file state something untrue. Add the missing name(s) to the Packages affected line."

if [ "$SEVERITY" = "error" ]; then
  exit 1
fi
exit 0
