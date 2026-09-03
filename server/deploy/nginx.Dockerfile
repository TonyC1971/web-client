# Custom nginx image with the static-brotli module enabled.
#
# `nginx:alpine` (the official Docker image) installs nginx from
# nginx.org's own repository, not from Alpine's. Alpine's
# nginx-mod-http-brotli package depends on Alpine's nginx package
# (different name + provider), so they're not interoperable on the
# official base image — `apk add nginx-mod-http-brotli` errors with
# unsatisfiable deps.
#
# Workaround: build straight from alpine:3.20, install Alpine's
# nginx + brotli module side by side. Alpine's nginx ships the same
# binary surface area (1.27.x current) and is fully compatible with
# our config files; the only difference is which init scripts /
# user are pre-created (irrelevant under docker-compose, where we
# define them ourselves).
#
# Used by /gamefiles/* to serve .mul.br twins to clients advertising
# Accept-Encoding: br (every modern browser since ~2017). Saves
# ~50-55 % of the 1.7 GB cold-cache asset payload.
#
# The .br twins live in a parallel dir (GAMEFILES_WEB_PATH, e.g. ./client/gamefiles-web/)
# precomputed by `server/scripts/precompress-gamefiles.mjs`. There is
# no symlink-based raw-byte fallback: brotli is a hard requirement,
# enforced implicitly because every browser meeting the rest of the
# wasm CUO requirements (SharedArrayBuffer, COOP/COEP, WebAssembly,
# WebGL2 — all post-2017) supports brotli too.
FROM alpine:3.20

RUN apk add --no-cache \
    nginx \
    nginx-mod-http-brotli \
 && mkdir -p /var/log/nginx /var/cache/nginx /var/run /var/lib/nginx/logs /var/lib/nginx/tmp \
 && chown -R nginx:nginx /var/log/nginx /var/cache/nginx /var/lib/nginx \
 # The Alpine nginx package creates /var/lib/nginx 0770 nginx:nginx.
 # Under cap_drop=ALL the master nginx process (running as root for
 # the privileged port-80 bind) loses CAP_DAC_OVERRIDE and can no
 # longer bypass mode checks, so dlopen() of the brotli module
 # symlinked into /var/lib/nginx/modules fails with EACCES. chmod
 # o+rx the chain of dirs nginx walks during startup so traversal
 # works without DAC_OVERRIDE.
 && chmod -R a+rX /var/lib/nginx /var/cache/nginx /var/log/nginx

# Top-level main.conf override — the only way to wire `load_module`
# directives, which must live in the main context (above http {}).
COPY nginx.main.conf /etc/nginx/nginx.conf
RUN chmod 644 /etc/nginx/nginx.conf

EXPOSE 80
STOPSIGNAL SIGQUIT

# Alpine's nginx package places the binary at /usr/sbin/nginx and
# expects /etc/nginx/conf.d/*.conf to hold site-specific config
# (mounted by docker-compose). `daemon off` keeps the container in
# the foreground.
CMD ["nginx", "-g", "daemon off;"]
