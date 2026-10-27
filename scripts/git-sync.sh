#!/usr/bin/env bash
set -euo pipefail

BRANCH="${1:-main}"
DRY_RUN=${DRY_RUN:-0}
NO_BUILD=${NO_BUILD:-0}
NO_TEST=${NO_TEST:-0}
NO_PUSH=${NO_PUSH:-0}
SHOULD_STASH=${SHOULD_STASH:-1}

print() { echo "$@"; }

if [ "$DRY_RUN" = "1" ]; then
  print "[DryRun] Planned steps: stash (if dirty), fetch --all, checkout $BRANCH, pull --rebase origin/$BRANCH, stash pop (if applied), build (unless NO_BUILD), test (unless NO_TEST), push (unless NO_PUSH)."
  exit 0
fi

# Detect dirty state
if git diff --name-only | grep -q .; then
  DIRTY=1
else
  DIRTY=0
fi

if [ "$DIRTY" -eq 1 ]; then
  if [ "$SHOULD_STASH" -eq 1 ]; then
    echo "Stashing local changes..."
    git stash push -u -m "git-sync stash before sync"
    STORED="1"
  else
    echo "Uncommitted changes detected. Use SHOULD_STASH=1 to stash automatically."
    exit 1
  fi
fi

echo "Fetching remotes..."
git fetch --all

echo "Switching to branch $BRANCH..."
if git show-ref --verify --quiet refs/heads/$BRANCH; then
  git checkout $BRANCH
else
  echo "Branch $BRANCH not found locally; attempting to create tracking branch from origin/$BRANCH"
  git checkout -b $BRANCH origin/$BRANCH
fi

echo "Pulling with rebase..."
git pull --rebase origin $BRANCH

if [ "$DRY_RUN" = "0" ] && [ -n "${STORED-}" ]; then
  echo "Applying stash..."
  git stash pop || true
fi

if [ "$NO_BUILD" -ne 1 ]; then
  echo "Building solution..."
  dotnet build MotionCommander.sln -clp:ErrorsOnly
  if [ $? -ne 0 ]; then
    echo "Build failed."; exit 1
  fi
fi

if [ "$NO_TEST" -ne 1 ]; then
  echo "Running tests..."
  dotnet test MotionCommander.sln
  if [ $? -ne 0 ]; then
    echo "Tests failed."; exit 1
  fi
fi

if [ "$NO_PUSH" -ne 1 ]; then
  echo "Pushing to origin/$BRANCH..."
  git push origin $BRANCH
fi

echo "Sync completed successfully."
