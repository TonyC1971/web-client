FROM node:22-alpine

# Build args let the operator align the in-container user with the host
# user that owns bind-mounted volumes. Defaults match generic Linux /
# Docker Desktop (UID 1000:1000); NAS DSM users start at 1024 with
# group 100 (`users`) — set APP_UID / APP_GID in .env to those values so
# chown sticks and the runtime user can read /app + write /app/data
# through the volume.
#
# The user inside the container is always called `node` (alpine convention),
# but its UID/GID get rewritten to whatever the operator passed in.
#
# Note on the var names: we use APP_UID/APP_GID instead of UID/GID
# because bash sets `UID` automatically to the shell's user id, which
# shadows any `.env` value during docker-compose's interpolation — so a
# `${UID:-1000}` in compose would resolve to `0` whenever the operator
# launches `docker compose` as root and the chown below would land on
# root rather than the host user the operator intended.
ARG APP_UID=1000
ARG APP_GID=1000

WORKDIR /app

# Install production deps only — drop tsx isn't a dev dep anymore (it's
# needed at runtime to import .ts directly via `node --import tsx/esm`),
# and we never want puppeteer / @types/* / tsc in the production image.
COPY package*.json ./
RUN npm ci --omit=dev --no-audit --no-fund

COPY tsconfig.json ./
COPY src/ ./src/
COPY scripts/check-build-tag.mjs scripts/check-src-syntax.mjs ./scripts/
# The minimal stack runs its asset worker from this same image, as a second service with a different
# command. It needs the worker AND the renderer it imports — shipping one without the other produces
# an image that builds clean and crash-loops on start with a module-not-found.
COPY scripts/minimal-asset-worker.mjs scripts/bake-worldmap.mjs ./scripts/

# 🚨 THE GATE HAS TO LIVE WHERE THE DEPLOY ACTUALLY GOES THROUGH. src/ is transformed by esbuild
# at CONTAINER START, not at build time, so a syntax error in it produces a healthy-looking image
# that crash-loops on boot. Both proxies then serve 502 while the build log says success.
#
# That is not hypothetical: it happened on 2026-07-31 (a tag edit left the old text stranded past
# the closing quote) and again on 2026-08-02 (an apostrophe inside the tag ended the string early).
# BuildTag.test.ts catches both and release.bat already runs the checker; neither helps, because a
# PROXY-ONLY deploy is `compose build proxy` and goes through neither. So the check runs HERE,
# where nobody can skip it by being in a hurry -- including me, twice.
#
# esbuild is already a dependency and transforms the whole tree in milliseconds. Failing the IMAGE
# BUILD is the right outcome: a proxy that was never built cannot be deployed broken.
#
# The transform moved into scripts/check-src-syntax.mjs so it could grow a RECURSIVE walk and a
# floor on how many files it found. Inline, it was a flat readdirSync('src') that would have gone on
# reporting success while silently skipping src/shared/ after the split.
RUN node scripts/check-build-tag.mjs src/shared/UOProxy.ts \
 && node scripts/check-src-syntax.mjs

# Re-create the `node` user with caller-specified UID/GID. Alpine ships
# `node:1000:1000` by default, which matches generic Linux but collides
# with NAS's UID layout (DSM users typically start at uid=1024+
# with primary group `users` gid=100). The dance below handles both:
#   1. Drop the default `node` user/group so the UID/GID aren't already
#      taken in /etc/passwd|group.
#   2. Reuse a group that already exists at the requested GID if there
#      is one (Alpine already ships gid=100 as `users`, matching
#      NAS). Otherwise create a fresh group.
#   3. Create the `node` user with the resolved group + requested UID.
#   4. chown /app and /home/node so the runtime user can read the source
#      and write its npm cache (npm logs to ~/.npm/_logs on errors; under
#      read_only:true rootfs we mount a tmpfs over that path at runtime).
#   5. chmod -R u=rwX,go=rX restores sane modes (644 on files, 755 on
#      dirs) on top of whatever the host filesystem had. On NAS
#      DSM, the build context's host files often live under directories
#      with POSIX mode 000 (DSM uses ACLs for actual access control);
#      Docker's COPY preserves that mode verbatim into the image, which
#      makes the file unreadable at runtime even after chown. The capital
#      X grants execute only to directories and files that already had
#      it, so no executable bit gets added to package.json / .ts / etc.
RUN deluser node 2>/dev/null || true \
 && delgroup node 2>/dev/null || true \
 && if ! getent group ${APP_GID} >/dev/null 2>&1; then \
        addgroup -g ${APP_GID} nodegrp; \
    fi \
 && _grpname=$(getent group ${APP_GID} | cut -d: -f1) \
 && adduser -D -u ${APP_UID} -G "${_grpname}" -s /bin/sh node \
 && mkdir -p /app/data /home/node/.npm \
 && chown -R ${APP_UID}:${APP_GID} /app /home/node \
 && chmod -R u=rwX,go=rX /app /home/node

USER node

# DOCKER=1 lets index.ts know it's running in-container so the AssetServer
# binds 0.0.0.0 (default loopback outside docker for safety).
#
# NPM_CONFIG_CACHE / NPM_CONFIG_LOGS_DIR redirect npm's writable state to
# /tmp instead of the default ~/.npm. Reason: under read_only:true the
# rootfs (and therefore /home/node) is read-only at runtime. /tmp is the
# tmpfs we already mount in docker-compose (mode 1777 world-writable), so
# npm's cache + _logs land somewhere it can actually write — without
# requiring a second tmpfs whose default ownership root:root would block
# the runtime UID anyway. HOME stays at /home/node for any other tooling
# that reads $HOME but doesn't try to write there.
ENV DOCKER=1 \
    NODE_ENV=production \
    HOME=/home/node \
    NPM_CONFIG_CACHE=/tmp/.npm \
    NPM_CONFIG_LOGS_DIR=/tmp/.npm/_logs

# nginx routes /api/, /auth/, /ws, /uo/batch to this internal port.
EXPOSE 3000

# Healthcheck — docker-compose's healthcheck:test array also targets this,
# but having it in the Dockerfile means `docker run` (no compose) gets it
# for free. wget is in alpine's base; node base images don't ship curl.
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD wget -q --spider --tries=1 http://127.0.0.1:3000/api/config || exit 1

CMD ["npm", "start"]
